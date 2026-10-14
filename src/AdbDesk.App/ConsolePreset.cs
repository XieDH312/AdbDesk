using System.ComponentModel;
using System.Windows;
using AdbDesk.Core;

namespace AdbDesk.App;

internal sealed class ConsolePresetGroup(string key, string title, IReadOnlyList<ConsolePresetItem> items, bool expanded)
{
    public string Key { get; } = key;
    public string Title => $"{title} · {Items.Count}";
    public IReadOnlyList<ConsolePresetItem> Items { get; } = items;
    public bool IsExpanded { get; set; } = expanded;
    public bool IsFavorite => false;
    public bool IsSelected { get; set; }
}

internal sealed class ConsolePresetItem(ConsolePreset preset, bool isFavorite) : INotifyPropertyChanged
{
    private bool _selected;
    public ConsolePreset Preset { get; } = preset;
    public bool IsFavorite { get; } = isFavorite;
    public string Title => Preset.Title;
    public string Star => IsFavorite ? "★" : "☆";
    public string FavoriteLabel => IsFavorite ? "取消收藏" : "收藏命令";
    public bool IsExpanded { get; set; }
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }
    public string Tooltip => $"{Preset.Description}\n{Preset.Command}\n{Preset.Requirements}";
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class ConsoleParameterInput(ConsoleParameter definition) : INotifyPropertyChanged
{
    private string _text = definition.DefaultValue;
    public ConsoleParameter Definition { get; } = definition;
    public string Label => Definition.Label;
    public string Hint => Definition.Hint;
    public Visibility BrowseVisibility => Definition.Browse.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string BrowseLabel => Definition.Kind == "package" ? "选择" : "…";
    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
