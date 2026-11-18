using System.Text.RegularExpressions;

namespace EventRelay.Core;

/// <summary>
/// Naming rules for event types (<c>flight.delayed</c>) and subscription type patterns
/// (<c>flight.delayed</c>, <c>flight.*</c>, <c>*</c>).
/// </summary>
public static partial class EventTypeRules
{
    public const int MaxLength = 128;

    /// <summary>Pattern that matches every event type.</summary>
    public const string MatchAll = "*";

    private const string PrefixWildcardSuffix = ".*";

    /// <summary>
    /// A concrete event type: lowercase segments separated by '.', '_' or '-',
    /// for example <c>flight.gate_changed</c>.
    /// </summary>
    public static bool IsValidType(string? type) =>
        type is { Length: > 0 and <= MaxLength } && TypeRegex().IsMatch(type);

    /// <summary>
    /// A subscription pattern: a concrete type, a prefix wildcard such as <c>flight.*</c>,
    /// or <c>*</c> for everything.
    /// </summary>
    public static bool IsValidPattern(string? pattern) =>
        pattern is not null
        && (pattern == MatchAll
            || IsValidType(pattern)
            || (pattern.EndsWith(PrefixWildcardSuffix, StringComparison.Ordinal)
                && IsValidType(pattern[..^PrefixWildcardSuffix.Length])));

    internal static bool IsPrefixWildcard(string pattern) =>
        pattern.EndsWith(PrefixWildcardSuffix, StringComparison.Ordinal);

    [GeneratedRegex("^[a-z0-9]+(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeRegex();
}
