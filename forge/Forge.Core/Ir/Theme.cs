namespace Forge.Core.Ir;

/// <summary>A programmatic look and feel (no official template assets). Same three themes as the Python Forge.</summary>
public sealed record Theme
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Look { get; init; }
    public bool Dark { get; init; }
    public required string Bg { get; init; }
    public required string Bg2 { get; init; }
    public required string Card { get; init; }
    public required string CardLine { get; init; }
    public required string Text { get; init; }
    public required string Subtext { get; init; }
    public required string Muted { get; init; }
    public required string Primary { get; init; }
    public required string[] Accents { get; init; }
    public required string OnAccent { get; init; }
    public required string FontHead { get; init; }
    public required string FontBody { get; init; }
    public required string FontNumber { get; init; }
    public required string Footer { get; init; }
    public required string Motif { get; init; }
    public double Radius { get; init; } = 0.12;

    public string Accent(int i) => Accents[((i % Accents.Length) + Accents.Length) % Accents.Length];

    public static readonly Theme Microsoft = new()
    {
        Key = "microsoft", Label = "Microsoft", Look = "Clean white, four-square motif, Fluent blue", Dark = false,
        Bg = "FFFFFF", Bg2 = "FFFFFF", Card = "F3F3F3", CardLine = "E1E1E1",
        Text = "1B1B1B", Subtext = "505050", Muted = "8A8A8A", Primary = "0078D4",
        Accents = ["0078D4", "107C10", "D83B01", "FFB900", "5C2D91"], OnAccent = "FFFFFF",
        FontHead = "Segoe UI Semibold", FontBody = "Segoe UI", FontNumber = "Segoe UI Semibold",
        Footer = "Microsoft", Motif = "ms_squares", Radius = 0.06,
    };

    public static readonly Theme Surface = new()
    {
        Key = "surface", Label = "Surface", Look = "Warm platinum, sapphire accents, soft bloom", Dark = false,
        Bg = "F7F5F2", Bg2 = "ECE8E2", Card = "FFFFFF", CardLine = "E4DFD8",
        Text = "141414", Subtext = "4A4A4A", Muted = "8C8780", Primary = "1F5FBF",
        Accents = ["1F5FBF", "3F8F7E", "B8742F", "6B5BD2", "C2416B"], OnAccent = "FFFFFF",
        FontHead = "Segoe UI Semibold", FontBody = "Segoe UI", FontNumber = "Segoe UI Light",
        Footer = "Surface", Motif = "surface_bloom", Radius = 0.18,
    };

    /// <summary>The dark theme. The footer names the device the deck is about, so it is filled in at render time
    /// from the detected hardware instead of being fixed to one model.</summary>
    public static readonly Theme SurfaceUltra = new()
    {
        Key = "surface_ultra", Label = "Surface Dark", Look = "Dark, green glow, high-contrast", Dark = true,
        Bg = "07080C", Bg2 = "121521", Card = "141824", CardLine = "283044",
        Text = "F4F6FA", Subtext = "AEB6C8", Muted = "6F7890", Primary = "76B900",
        Accents = ["76B900", "3DA9FC", "9B6BFF", "FFB547", "FF5D8F"], OnAccent = "07080C",
        FontHead = "Segoe UI Semibold", FontBody = "Segoe UI", FontNumber = "Segoe UI Black",
        Footer = "Surface Laptop Ultra  |  NVIDIA RTX Spark", Motif = "ultra_glow", Radius = 0.14,
    };

    public static IReadOnlyList<Theme> All { get; } = [Microsoft, Surface, SurfaceUltra];

    public static Theme Get(string key) =>
        All.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown template '{key}'. Choose: {string.Join(", ", All.Select(t => t.Key))}");
}
