namespace Forge.Cli;

/// <summary>Takes options out of the argument list one at a time, so whatever is left over is a mistake the person hears about.</summary>
internal sealed class CliArgs
{
    private readonly List<string> _rest;

    public CliArgs(IEnumerable<string> args) => _rest = [.. args];

    public bool Flag(string name) => _rest.Remove(name);

    public string? Value(string name)
    {
        int i = _rest.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= _rest.Count) throw new ArgumentException($"{name} needs a value.");
        string value = _rest[i + 1];
        _rest.RemoveRange(i, 2);
        return value;
    }

    /// <summary>An option whose value may be left out: the next token is its value unless it is another option.</summary>
    public string? OptionalValue(string name, out bool present)
    {
        int i = _rest.IndexOf(name);
        present = i >= 0;
        if (i < 0) return null;
        string? value = i + 1 < _rest.Count && !_rest[i + 1].StartsWith("--", StringComparison.Ordinal) ? _rest[i + 1] : null;
        _rest.RemoveRange(i, value is null ? 1 : 2);
        return value;
    }

    public int? Number(string name, int min, int max)
    {
        string? text = Value(name);
        if (text is null) return null;
        if (!int.TryParse(text, out int n) || n < min || n > max) throw new ArgumentException($"{name} must be a whole number from {min} to {max}.");
        return n;
    }

    public void RequireEmpty(string usage)
    {
        if (_rest.Count > 0) throw new ArgumentException($"Unknown option '{_rest[0]}'.\n\n{usage}");
    }
}
