using System.Globalization;

namespace Fmp.Cli;

/// <summary>
/// Minimal cursor over command-line arguments supporting `--name=value`,
/// `--name value`, and bare flags. Numeric values are parsed with the
/// invariant culture. A `--` token ends option parsing: all remaining tokens
/// are positionals.
/// </summary>
internal ref struct ArgumentReader
{
    private readonly string[] _args;
    private int _index;
    private bool _afterDoubleDash;
    private string _inlineValue;

    public ArgumentReader(string[] args)
    {
        _args = args;
        _index = 0;
        _afterDoubleDash = false;
        _inlineValue = null;
    }

    public bool HasMore => _index < _args.Length;

    /// <summary>
    /// Consumes and returns the next option token's name and inline value
    /// (null when there is no '='). Returns false when exhausted, when the next
    /// token is a positional or "--", or after "--" has been seen. For
    /// "--name=value" the value is retained and returned by the next
    /// RequireValue/ReadInt/ReadDouble call.
    /// </summary>
    public bool TryReadOption(out string name, out string value)
    {
        if (_afterDoubleDash || !HasMore)
        {
            name = null;
            value = null;
            return false;
        }
        string token = _args[_index];
        if (token == "--" || !token.StartsWith('-'))
        {
            name = null;
            value = null;
            return false;
        }
        _index++;
        int eq = token.IndexOf('=');
        if (eq > 0)
        {
            name = token[..eq];
            value = token[(eq + 1)..];
            _inlineValue = value;
        }
        else
        {
            name = token;
            value = null;
            _inlineValue = null;
        }
        return true;
    }

    /// <summary>
    /// Consumes and returns the next token as a positional. After a "--" token
    /// is consumed, all remaining tokens are positionals.
    /// </summary>
    public string Next()
    {
        if (!HasMore) return null;
        string token = _args[_index++];
        if (token == "--") _afterDoubleDash = true;
        return token;
    }

    /// <summary>
    /// Returns the value for an option already consumed by TryReadOption: the
    /// inline "=value" when present, otherwise the next token. Throws
    /// ArgumentException when no value is available.
    /// </summary>
    public string RequireValue(string option)
    {
        if (_inlineValue != null)
        {
            string v = _inlineValue;
            _inlineValue = null;
            return v;
        }
        if (!HasMore) throw new ArgumentException($"missing value for {option}");
        return _args[_index++];
    }

    /// <summary>
    /// Reads an optional value for an option already consumed by TryReadOption:
    /// the inline "=value" when present, otherwise the next token when it does
    /// not start with '-' and exists. Returns null when the option was emitted
    /// bare (e.g. a fully-resolved command with a null/empty field) — the
    /// caller treats null as "unspecified/empty". Never throws for a bare
    /// option.
    /// </summary>
    public string? OptionalValue(string option)
    {
        if (_inlineValue != null)
        {
            string v = _inlineValue;
            _inlineValue = null;
            return v;
        }
        if (!HasMore) return null;
        string next = _args[_index];
        if (next.StartsWith("-", StringComparison.Ordinal))
            return null;
        _index++;
        return next;
    }

    /// <summary>
    /// Reads an int option value (inline or next token) with the invariant
    /// culture. Throws ArgumentException when it is not a valid int.
    /// </summary>
    public int ReadInt(string option)
    {
        string raw = RequireValue(option);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new ArgumentException($"invalid numeric value for {option}: '{raw}'");
        return value;
    }

    /// <summary>
    /// Reads a double option value (inline or next token) with the invariant
    /// culture. Throws ArgumentException when it is not a finite double.
    /// </summary>
    public double ReadDouble(string option)
    {
        string raw = RequireValue(option);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
            throw new ArgumentException($"invalid numeric value for {option}: '{raw}'");
        return value;
    }

    /// <summary>Reads a signed 64-bit integer option with invariant parsing.</summary>
    public long ReadLong(string option)
    {
        string raw = RequireValue(option);
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            throw new ArgumentException($"invalid integer value for {option}: '{raw}'");
        return value;
    }
}
