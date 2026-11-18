using System.Text.RegularExpressions;

namespace EventRelay.Core;

/// <summary>
/// Limits for event attributes and subscription filters. Attributes are small string
/// key/value pairs used for routing, not a place for the payload.
/// </summary>
public static partial class AttributeRules
{
    public const int MaxCount = 20;
    public const int MaxValueLength = 256;

    public static bool IsValidKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    public static bool IsValidValue(string? value) => value is { Length: <= MaxValueLength };

    /// <summary>Returns a human-readable problem with the attribute set, or null when it is valid.</summary>
    public static string? Validate(IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null)
        {
            return null;
        }

        if (attributes.Count > MaxCount)
        {
            return $"At most {MaxCount} entries are allowed.";
        }

        foreach (var (key, value) in attributes)
        {
            if (!IsValidKey(key))
            {
                return $"Key '{key}' is invalid: use a letter followed by letters, digits or '_' (max 64 characters).";
            }

            if (!IsValidValue(value))
            {
                return $"Value of '{key}' must be a string of at most {MaxValueLength} characters.";
            }
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();
}
