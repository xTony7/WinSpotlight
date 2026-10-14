using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Windows.UI;

namespace WinSpotlight;

/// <summary>Look-and-feel settings, stored under "appearance" in config.json.</summary>
public sealed class Appearance
{
    public string Theme { get; set; } = "system";   // system | light | dark
    public string Accent { get; set; } = "blue";
    public int Blur { get; set; } = 60;             // 0-100, 0 = clear glass
    public int Transparency { get; set; } = 45;     // 0-100, how much of the desktop shows through the tint
    public int Vibrancy { get; set; } = 40;         // 0-100, saturation boost of what shows through
    public bool GlassEdge { get; set; } = true;     // Liquid Glass rim highlight
    public bool Shadow { get; set; } = true;
    public bool Preview { get; set; } = true;       // preview pane next to the results

    /// <summary>The macOS accent palette.</summary>
    public static readonly (string Key, string Name, Color Color)[] Accents =
    {
        ("blue", "Blue", Color.FromArgb(255, 0x0A, 0x84, 0xFF)),
        ("purple", "Purple", Color.FromArgb(255, 0xBF, 0x5A, 0xF2)),
        ("pink", "Pink", Color.FromArgb(255, 0xFF, 0x37, 0x5F)),
        ("red", "Red", Color.FromArgb(255, 0xFF, 0x45, 0x3A)),
        ("orange", "Orange", Color.FromArgb(255, 0xFF, 0x9F, 0x0A)),
        ("yellow", "Yellow", Color.FromArgb(255, 0xE6, 0xB8, 0x00)),
        ("green", "Green", Color.FromArgb(255, 0x30, 0xB0, 0x50)),
        ("graphite", "Graphite", Color.FromArgb(255, 0x8E, 0x8E, 0x93)),
    };

    [JsonIgnore] public Color AccentColor => Accents.FirstOrDefault(a => a.Key == Accent, Accents[0]).Color;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static Appearance Load()
    {
        try { return SetupWindow.LoadConfig()["appearance"]?.Deserialize<Appearance>(Json) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            var cfg = SetupWindow.LoadConfig();
            cfg["appearance"] = JsonSerializer.SerializeToNode(this, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(SetupWindow.ConfigPath)!);
            File.WriteAllText(SetupWindow.ConfigPath, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { App.Log("appearance save " + e.Message); }
    }
}
