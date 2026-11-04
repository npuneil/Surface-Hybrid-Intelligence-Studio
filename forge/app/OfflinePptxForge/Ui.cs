using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace OfflinePptxForge;

/// <summary>Code-only WinUI helpers: the palette (the Surface deck theme's own colours, so the app and the decks look related) and small
/// builders for text, cards and pills. Controls get an AutomationId so a test can reach them through UI Automation.</summary>
internal static class Ui
{
    internal static readonly SolidColorBrush Accent = Hex("#1F5FBF");
    internal static readonly SolidColorBrush AccentDeep = Hex("#194D99");
    internal static readonly SolidColorBrush AccentTint = Hex("#E6EEFA");
    internal static readonly SolidColorBrush Paper = Hex("#F7F5F2");
    internal static readonly SolidColorBrush Card = Hex("#FFFFFF");
    internal static readonly SolidColorBrush Ink = Hex("#141414");
    internal static readonly SolidColorBrush Muted = Hex("#5E5A54");
    internal static readonly SolidColorBrush Line = Hex("#E4DFD8");
    internal static readonly SolidColorBrush Sand = Hex("#ECE8E2");
    internal static readonly SolidColorBrush AmberInk = Hex("#7A4B00");
    internal static readonly SolidColorBrush AmberTint = Hex("#FFF3D6");
    internal static readonly SolidColorBrush GreenInk = Hex("#1C6336");
    internal static readonly SolidColorBrush GreenTint = Hex("#E5F3E9");
    internal static readonly SolidColorBrush RedInk = Hex("#9B1C1C");
    internal static readonly SolidColorBrush RedTint = Hex("#FCE8E8");
    internal static readonly SolidColorBrush Slate = Hex("#3F4A55");
    internal static readonly SolidColorBrush SlateTint = Hex("#E9EDF1");
    internal static readonly SolidColorBrush White = Hex("#FFFFFF");
    internal static readonly SolidColorBrush Clear = new(Colors.Transparent);

    internal static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    internal static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas");
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    internal static SolidColorBrush Hex(string rgb)
    {
        var (r, g, b) = ParseHex(rgb);
        return new SolidColorBrush(Color.FromArgb(255, r, g, b));
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
        Set(Text(value.ToUpperInvariant(), 11, Accent, FontWeights.SemiBold), t => t.CharacterSpacing = 100);

    internal static TextBlock H1(string value) => Set(Text(value, 26, Ink, FontWeights.SemiBold), t => t.FontFamily = Display);

    internal static TextBlock H2(string value) => Set(Text(value, 18, Ink, FontWeights.SemiBold), t => t.FontFamily = Display);

    internal static TextBlock H3(string value) => Text(value, 14, Ink, FontWeights.SemiBold);

    internal static TextBlock Body(string value, Brush? color = null) => Set(Text(value, 13, color ?? Muted), t => t.LineHeight = 19);

    internal static TextBlock Small(string value, Brush? color = null) => Set(Text(value, 12, color ?? Muted), t => t.LineHeight = 17);

    internal static Border CardBorder(UIElement child, double padding = 18, Brush? background = null) => new()
    {
        Child = child,
        Background = background ?? Card,
        BorderBrush = Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(padding),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    internal static Border Pill(string text, Brush background, Brush foreground) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(9, 2, 9, 3),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, 11, foreground, FontWeights.SemiBold),
    };

    internal static Button Primary(string label) => new()
    {
        Content = label,
        Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        Padding = new Thickness(18, 8, 18, 9),
        FontWeight = FontWeights.SemiBold,
        CornerRadius = new CornerRadius(8),
    };

    internal static Button Secondary(string label) => new()
    {
        Content = label,
        Padding = new Thickness(14, 7, 14, 8),
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

    /// <summary>A fixed leading element followed by content that takes and wraps within the remaining width.</summary>
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

    internal static FontIcon Glyph(string glyph, double size = 16, Brush? color = null) =>
        new() { Glyph = glyph, FontSize = size, Foreground = color ?? Accent, FontFamily = Icons };

    internal static (Brush Back, Brush Fore) StatusColors(string status) => status switch
    {
        "available" => (GreenTint, GreenInk),
        "limited" => (AmberTint, AmberInk),
        _ => (SlateTint, Slate),
    };
}

/// <summary>Lays children out left to right and wraps to a new line when the next one does not fit. Used for pills, thumbnails and tiles
/// that must never overlap or run off the window at any width.</summary>
internal sealed class WrapPanel : Panel
{
    internal double HorizontalSpacing { get; init; } = 8;
    internal double VerticalSpacing { get; init; } = 8;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double x = 0, lineHeight = 0, height = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(available.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > available.Width)
            {
                height += lineHeight + VerticalSpacing;
                widest = Math.Max(widest, x - HorizontalSpacing);
                x = 0;
                lineHeight = 0;
            }
            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        widest = Math.Max(widest, x - HorizontalSpacing);
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? Math.Max(widest, 0) : Math.Min(Math.Max(widest, 0), available.Width), height + lineHeight);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > final.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }
            child.Arrange(new Windows.Foundation.Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return new Windows.Foundation.Size(final.Width, y + lineHeight);
    }
}

/// <summary>Wraps equally sized cards into as many columns as fit, reflowing on resize so content never overlaps.</summary>
internal sealed class AdaptiveGrid : Grid
{
    private readonly List<FrameworkElement> _items = [];
    private readonly double _minItemWidth;
    private readonly int _maxColumns;
    private int _columns = -1;

    internal AdaptiveGrid(double minItemWidth, int maxColumns, double spacing = 12)
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
