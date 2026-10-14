using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace WinSpotlight;

/// <summary>Talks to (and if needed launches) the Python indexer/search/LLM backend.</summary>
public sealed class Backend : IDisposable
{
    public const int Port = 52123;
    private readonly HttpClient _http = new() { BaseAddress = new Uri($"http://127.0.0.1:{Port}/"), Timeout = Timeout.InfiniteTimeSpan };
    private Process? _proc;

    public static string? FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("WINSPOTLIGHT_ROOT");
        if (env != null && File.Exists(Path.Combine(env, "winspotlight", "server.py"))) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "winspotlight", "server.py"))) return d.FullName;
        return null;
    }

    public async Task EnsureRunningAsync()
    {
        if (await StatusAsync() != null) return;
        // Packaged builds ship the backend as an exe next to WinSpotlight.exe; dev builds run the Python source.
        var bundled = Path.Combine(AppContext.BaseDirectory, "backend", "winspotlight-backend.exe");
        if (File.Exists(bundled))
        {
            _proc = Process.Start(new ProcessStartInfo(bundled, $"--port {Port}") { UseShellExecute = false, CreateNoWindow = true });
            return;
        }
        var root = FindRoot() ?? throw new InvalidOperationException("Can't find the winspotlight Python package (set WINSPOTLIGHT_ROOT).");
        _proc = Process.Start(new ProcessStartInfo("pythonw.exe", $"-m winspotlight.server --port {Port}")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public async Task<StatusResponse?> StatusAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(1500);
            return await _http.GetFromJsonAsync<StatusResponse>("status", cts.Token);
        }
        catch { return null; }
    }

    public Task<SearchResponse?> SearchAsync(string q, bool rerank, CancellationToken ct) =>
        _http.GetFromJsonAsync<SearchResponse>($"search?rerank={(rerank ? 1 : 0)}&q=" + Uri.EscapeDataString(q), ct);

    public Task OpenAsync(string path, bool reveal) =>
        _http.PostAsJsonAsync("open", new { path, reveal });

    /// <summary>Stops whichever backend is running and starts a fresh one so it rereads config.json.</summary>
    public async Task RestartAsync()
    {
        try { await _http.PostAsync("shutdown", null); } catch { }
        for (int i = 0; i < 20 && await StatusAsync() != null; i++) await Task.Delay(250);
        _proc = null;
        await EnsureRunningAsync();
    }

    public Task ReindexAsync() => _http.PostAsync("reindex", null);

    /// <summary>Streams LLM tokens. Throws with the backend's message on error.</summary>
    public async IAsyncEnumerable<string> AskAsync(string q, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "ask") { Content = new StringContent(JsonSerializer.Serialize(new { q }), Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            using var doc = JsonDocument.Parse(line[5..]);
            var root = doc.RootElement;
            if (root.TryGetProperty("t", out var t)) yield return t.GetString() ?? "";
            else if (root.TryGetProperty("error", out var e)) throw new Exception(e.GetString());
            else if (root.TryGetProperty("done", out _)) yield break;
        }
    }

    /// <summary>Streams agent events from POST /task (start, thought, tool, summary, done, error).</summary>
    public async IAsyncEnumerable<JsonElement> TaskAsync(string q, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "task") { Content = new StringContent(JsonSerializer.Serialize(new { q }), Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            using var doc = JsonDocument.Parse(line[5..]);
            yield return doc.RootElement.Clone();
        }
    }

    public void Dispose()
    {
        try { _proc?.Kill(entireProcessTree: true); } catch { }
        _http.Dispose();
    }
}
