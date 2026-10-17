using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace HybridIntelligenceStudio;

internal static class Ui
{
    internal static readonly SolidColorBrush Accent = Hex(Brand.Accent);
    internal static readonly SolidColorBrush AccentDeep = Hex(Brand.AccentDeep);
    internal static readonly SolidColorBrush AccentTint = Hex(Brand.AccentTint);
    internal static readonly SolidColorBrush Paper = Brush(0xFA, 0xF7, 0xF3);
    internal static readonly SolidColorBrush Card = Brush(0xFF, 0xFF, 0xFF);
    internal static readonly SolidColorBrush Ink = Brush(0x1F, 0x1A, 0x17);
    internal static readonly SolidColorBrush Muted = Brush(0x66, 0x5D, 0x57);
    internal static readonly SolidColorBrush Line = Brush(0xE8, 0xE0, 0xD6);
    internal static readonly SolidColorBrush Sand = Brush(0xF3, 0xEE, 0xE7);
    internal static readonly SolidColorBrush AmberInk = Brush(0x7A, 0x4B, 0x00);
    internal static readonly SolidColorBrush AmberTint = Brush(0xFF, 0xF3, 0xD6);
    internal static readonly SolidColorBrush GreenInk = Brush(0x1C, 0x63, 0x36);
    internal static readonly SolidColorBrush GreenTint = Brush(0xE5, 0xF3, 0xE9);
    internal static readonly SolidColorBrush Slate = Brush(0x3F, 0x4A, 0x55);
    internal static readonly SolidColorBrush SlateTint = Brush(0xE9, 0xED, 0xF1);
    internal static readonly SolidColorBrush White = Brush(0xFF, 0xFF, 0xFF);

    internal static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    internal static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas");

    internal static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));

    internal static SolidColorBrush Hex(string rgb)
    {
        var (r, g, b) = ParseHex(rgb);
        return Brush(r, g, b);
    }

    internal static (byte R, byte G, byte B) ParseHex(string rgb) =>
        rgb is ['#', _, _, _, _, _, _]
            ? (Convert.ToByte(rgb[1..3], 16), Convert.ToByte(rgb[3..5], 16), Convert.ToByte(rgb[5..7], 16))
            : throw new FormatException($"Expected #RRGGBB, got '{rgb}'.");

    internal static TextBlock Text(string value, double size = 14, Brush? foreground = null, FontWeight? weight = null) => new()
    {
        Text = value,
        FontSize = size,
        Foreground = foreground ?? Ink,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = false,
    };

    internal static TextBlock Eyebrow(string value) =>
        Set(Text(value.ToUpperInvariant(), 12, Accent, FontWeights.SemiBold), t => t.CharacterSpacing = 120);

    internal static TextBlock H1(string value) =>
        Set(Text(value, 38, Ink, FontWeights.SemiBold), t => { t.FontFamily = Display; t.LineHeight = 46; });

    internal static TextBlock H2(string value) => Set(Text(value, 22, Ink, FontWeights.SemiBold), t => t.FontFamily = Display);

    internal static TextBlock H3(string value) => Text(value, 16, Ink, FontWeights.SemiBold);

    internal static TextBlock Body(string value, Brush? color = null) => Set(Text(value, 14, color ?? Muted), t => t.LineHeight = 21);

    internal static TextBlock Small(string value, Brush? color = null) => Set(Text(value, 12, color ?? Muted), t => t.LineHeight = 17);

    internal static Border CardBorder(UIElement child, double padding = 24, Brush? background = null) => new()
    {
        Child = child,
        Background = background ?? Card,
        BorderBrush = Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(padding),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    internal static Border Pill(string text, Brush background, Brush foreground) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(10, 3, 10, 4),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, 12, foreground, FontWeights.SemiBold),
    };

    internal static Button Primary(string label) => new()
    {
        Content = label,
        Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        Padding = new Thickness(18, 9, 18, 10),
        FontWeight = FontWeights.SemiBold,
        CornerRadius = new CornerRadius(8),
    };

    internal static Button Secondary(string label) => new()
    {
        Content = label,
        Padding = new Thickness(16, 8, 16, 9),
        CornerRadius = new CornerRadius(8),
    };

    internal static StackPanel Stack(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    internal static StackPanel Row(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    /// <summary>A fixed leading element (icon/pill) followed by content that takes and wraps within the remaining width.</summary>
    internal static Grid Lead(double spacing, FrameworkElement lead, FrameworkElement content)
    {
        var grid = new Grid { ColumnSpacing = spacing };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lead.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(content, 1);
        grid.Children.Add(lead);
        grid.Children.Add(content);
        return grid;
    }

    internal static T Id<T>(T element, string id) where T : DependencyObject
    {
        AutomationProperties.SetAutomationId(element, id);
        return element;
    }

    internal static T Named<T>(T element, string id, string name) where T : DependencyObject
    {
        AutomationProperties.SetAutomationId(element, id);
        AutomationProperties.SetName(element, name);
        return element;
    }

    internal static T Set<T>(T value, Action<T> configure)
    {
        configure(value);
        return value;
    }

    internal static ScrollViewer Page(UIElement content) => new()
    {
        Content = new Border
        {
            Child = content,
            Padding = new Thickness(40, 28, 40, 48),
            MaxWidth = 1240,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        },
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    internal static UIElement Stat(string value, string label, string detail, string id, Brush? accent = null)
    {
        var number = Id(Set(Text(value, 30, accent ?? Ink, FontWeights.SemiBold), t => t.FontFamily = Display), id);
        return CardBorder(Stack(4, number, Text(label, 14, Ink, FontWeights.SemiBold), Small(detail)), 20);
    }

    internal static FontIcon Glyph(string glyph, double size = 16, Brush? color = null) =>
        new() { Glyph = glyph, FontSize = size, Foreground = color ?? Accent, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
}

/// <summary>Wraps equally sized cards into as many columns as fit, reflowing on resize so content never overlaps.</summary>
internal sealed class AdaptiveGrid : Grid
{
    private readonly List<FrameworkElement> _items = [];
    private readonly double _minItemWidth;
    private readonly int _maxColumns;
    private int _columns = -1;

    internal AdaptiveGrid(double minItemWidth, int maxColumns, double spacing = 16)
    {
        _minItemWidth = minItemWidth;
        _maxColumns = maxColumns;
        ColumnSpacing = spacing;
        RowSpacing = spacing;
        SizeChanged += (_, e) => Reflow(e.NewSize.Width);
    }

    internal AdaptiveGrid With(params FrameworkElement[] items)
    {
        foreach (var item in items) { _items.Add(item); Children.Add(item); }
        _columns = -1;
        Reflow(ActualWidth);
        return this;
    }

    private void Reflow(double width)
    {
        int columns = width <= 0 ? 1 : (int)Math.Floor((width + ColumnSpacing) / (_minItemWidth + ColumnSpacing));
        columns = Math.Clamp(columns, 1, Math.Max(1, Math.Min(_maxColumns, _items.Count)));
        if (columns == _columns) return;
        _columns = columns;
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        for (int c = 0; c < columns; c++) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = (_items.Count + columns - 1) / columns;
        for (int r = 0; r < rows; r++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < _items.Count; i++)
        {
            SetColumn(_items[i], i % columns);
            SetRow(_items[i], i / columns);
        }
    }
}
