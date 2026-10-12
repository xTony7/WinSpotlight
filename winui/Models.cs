using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LSearch;

public record FileHit(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("mtime")] double? Mtime,
    [property: JsonPropertyName("snippet")] string? Snippet);

public record Section(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("items")] List<FileHit> Items);

public record SearchResponse(
    [property: JsonPropertyName("top")] FileHit? Top,
    [property: JsonPropertyName("sections")] List<Section> Sections,
    [property: JsonPropertyName("calc")] string? Calc,
    [property: JsonPropertyName("task")] TaskInfo? Task);

public record TaskInfo([property: JsonPropertyName("target")] string Target);

public record StatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("hotkey")] string Hotkey,
    [property: JsonPropertyName("llm")] string? Llm);

public enum ItemType { Header, File, Calc, Ask, Task }

/// <summary>One row in the results list (section headers are rows too).</summary>
public class ResultItem : INotifyPropertyChanged
{
    public ItemType Type { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Glyph { get; init; } = "";
    public Brush? GlyphBrush { get; init; }
    public FileHit? Hit { get; init; }
    public string? Value { get; init; }
    public Visibility HasSubtitle => string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static ResultItem Header(string title) => new() { Type = ItemType.Header, Title = title.ToUpperInvariant() };
    public static readonly SolidColorBrush Purple = new(ColorHelper.FromArgb(255, 0xBF, 0x5A, 0xF2));
}

public partial class ResultTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Header { get; set; }
    public DataTemplate? Row { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is ResultItem { Type: ItemType.Header } ? Header : Row;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
