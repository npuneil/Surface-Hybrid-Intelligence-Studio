namespace Forge.Core.Ir;

/// <summary>Segoe Fluent Icons glyphs, the same table the Python Forge verified on a real device.</summary>
public static class Icons
{
    public const string FontName = "Segoe Fluent Icons";

    private static readonly Dictionary<string, int> Glyphs = new(StringComparer.Ordinal)
    {
        ["cloud"] = 0xE753, ["laptop"] = 0xE7F8, ["monitor"] = 0xE7F4, ["chip"] = 0xE950, ["gpu"] = 0xEEA1, ["memory"] = 0xEEA0,
        ["gear"] = 0xE713, ["people"] = 0xE716, ["person"] = 0xE77B, ["search"] = 0xE721, ["lock"] = 0xE72E, ["shield"] = 0xEA18,
        ["globe"] = 0xE774, ["document"] = 0xE8A5, ["chat"] = 0xE8BD, ["bolt"] = 0xE945, ["puzzle"] = 0xEA86, ["clock"] = 0xE823,
        ["home"] = 0xE80F, ["library"] = 0xE8F1, ["code"] = 0xE943, ["star"] = 0xE734, ["check"] = 0xE73E, ["cross"] = 0xE711,
        ["arrow"] = 0xE72A, ["folder"] = 0xE8B7, ["wifi"] = 0xE701, ["speed"] = 0xEC4A, ["ai"] = 0xE99A, ["bank"] = 0xE825,
        ["warning"] = 0xE7BA, ["mic"] = 0xE720, ["network"] = 0xE968, ["battery"] = 0xE83F, ["leaf"] = 0xEC0A, ["money"] = 0xE8C7,
        ["chart"] = 0xE9D2, ["pulse"] = 0xE9D9, ["idea"] = 0xEA80, ["flag"] = 0xE7C1, ["database"] = 0xEE94, ["server"] = 0xE839,
        ["cube"] = 0xF158, ["swap"] = 0xE8AB, ["gears"] = 0xE9F5, ["key"] = 0xE8D7, ["airplane"] = 0xE709, ["car"] = 0xE804,
        ["map"] = 0xE826, ["camera"] = 0xE722, ["video"] = 0xE714, ["eye"] = 0xE7B3, ["sync"] = 0xE895, ["upload"] = 0xE898,
        ["download"] = 0xE896, ["plug"] = 0xE83E, ["heart"] = 0xEB51, ["tag"] = 0xE8EC, ["calendar"] = 0xE787, ["mail"] = 0xE715,
        ["phone"] = 0xE717, ["edit"] = 0xE70F, ["filter"] = 0xE71C, ["table"] = 0xE80A, ["piechart"] = 0xEB05, ["barchart"] = 0xE9F9,
        ["education"] = 0xE7BE, ["health"] = 0xE95E, ["storage"] = 0xEDA2, ["fingerprint"] = 0xE928, ["sliders"] = 0xE9E9,
        ["layers"] = 0xE81E, ["apps"] = 0xECA5, ["world"] = 0xE909, ["ticket"] = 0xE8D1, ["timer"] = 0xE916, ["devices"] = 0xE772,
        ["wrench"] = 0xE90F, ["pin"] = 0xE718, ["sun"] = 0xE706, ["power"] = 0xE7E8, ["shop"] = 0xE719, ["certificate"] = 0xEB95,
        ["megaphone"] = 0xEC24, ["thermometer"] = 0xE9CA, ["drop"] = 0xEB42, ["checklist"] = 0xE9D5, ["target"] = 0xE8F0,
        ["flame"] = 0xECAD, ["signal"] = 0xE95A, ["thumbsup"] = 0xE8E1, ["quote"] = 0xE9B2, ["history"] = 0xE81C,
        ["calculator"] = 0xE8EF, ["team"] = 0xE902, ["tools"] = 0xEC7A, ["multitask"] = 0xEBC6, ["approve"] = 0xEB50,
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["brain"] = "ai", ["robot"] = "ai", ["model"] = "ai", ["processor"] = "chip", ["cpu"] = "chip", ["npu"] = "chip",
        ["ram"] = "memory", ["data"] = "database", ["security"] = "shield", ["privacy"] = "lock", ["offline"] = "airplane",
        ["speedometer"] = "speed", ["fast"] = "bolt", ["energy"] = "bolt", ["sustainability"] = "leaf", ["cost"] = "money",
        ["dollar"] = "money", ["savings"] = "money", ["settings"] = "gear", ["user"] = "person", ["users"] = "people",
        ["file"] = "document", ["docs"] = "document", ["message"] = "chat", ["time"] = "clock", ["rocket"] = "flag",
        ["growth"] = "chart", ["analytics"] = "barchart", ["trend"] = "chart", ["build"] = "tools", ["pilot"] = "flag",
        ["kitchen"] = "home", ["house"] = "home", ["chef"] = "person", ["car_engine"] = "car", ["engine"] = "gears",
        ["library_book"] = "library", ["book"] = "library", ["storage_drive"] = "storage", ["laptop_ultra"] = "laptop",
        ["surface"] = "laptop", ["pc"] = "laptop", ["device"] = "laptop", ["router"] = "swap", ["routing"] = "swap",
        ["hybrid"] = "swap", ["water"] = "drop", ["co2"] = "leaf", ["carbon"] = "leaf", ["lightning"] = "bolt",
        ["trophy"] = "star", ["goal"] = "target", ["idea_bulb"] = "idea", ["lightbulb"] = "idea", ["graph"] = "chart",
    };

    /// <summary>Every icon name the model may use, sorted. Aliases are accepted on input but not offered.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Glyphs.Keys.Order(StringComparer.Ordinal)];

    private static string Normalize(string? name) => (name ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    public static bool Known(string? name)
    {
        string key = Normalize(name);
        return Glyphs.ContainsKey(key) || Aliases.ContainsKey(key);
    }

    /// <summary>The glyph for an icon name; unknown names fall back to "idea".</summary>
    public static string Glyph(string? name)
    {
        string key = Normalize(name);
        if (Aliases.TryGetValue(key, out string? target)) key = target;
        return char.ConvertFromUtf32(Glyphs.TryGetValue(key, out int code) ? code : Glyphs["idea"]);
    }
}
