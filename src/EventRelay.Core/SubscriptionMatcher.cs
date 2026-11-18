namespace EventRelay.Core;

/// <summary>
/// Decides whether an event should be delivered to a subscription:
/// the event type must match one of the subscription's patterns and every
/// filter entry must be present in the event attributes with the same value.
/// </summary>
public static class SubscriptionMatcher
{
    public static bool Matches(
        Subscription subscription,
        string eventType,
        IReadOnlyDictionary<string, string> attributes) =>
        subscription.IsActive
        && MatchesAnyType(subscription.EventTypes, eventType)
        && MatchesFilter(subscription.Filter, attributes);

    /// <summary>
    /// <c>*</c> matches everything, <c>flight.*</c> matches any type starting with
    /// <c>flight.</c>, anything else must be equal (ordinal).
    /// </summary>
    public static bool MatchesType(string pattern, string eventType)
    {
        if (pattern == EventTypeRules.MatchAll)
        {
            return true;
        }

        if (EventTypeRules.IsPrefixWildcard(pattern))
        {
            var prefix = pattern.AsSpan(0, pattern.Length - 1); // keep the trailing '.'
            return eventType.Length > prefix.Length
                   && eventType.AsSpan().StartsWith(prefix, StringComparison.Ordinal);
        }

        return string.Equals(pattern, eventType, StringComparison.Ordinal);
    }

    public static bool MatchesAnyType(IEnumerable<string> patterns, string eventType) =>
        patterns.Any(pattern => MatchesType(pattern, eventType));

    /// <summary>An empty filter matches every event.</summary>
    public static bool MatchesFilter(
        IReadOnlyDictionary<string, string> filter,
        IReadOnlyDictionary<string, string> attributes) =>
        filter.All(entry =>
            attributes.TryGetValue(entry.Key, out var value)
            && string.Equals(value, entry.Value, StringComparison.Ordinal));

    /// <summary>
    /// Every pattern that can match <paramref name="eventType"/>:
    /// <c>a.b.c</c> → <c>a.b.c</c>, <c>a.b.*</c>, <c>a.*</c>, <c>*</c>.
    /// Lets storage pre-select candidate subscriptions with an index-friendly
    /// "array overlaps" query; <see cref="Matches"/> still makes the final decision.
    /// </summary>
    public static IReadOnlyList<string> CandidatePatterns(string eventType)
    {
        var patterns = new List<string> { eventType };

        for (var dot = eventType.LastIndexOf('.'); dot > 0; dot = eventType.LastIndexOf('.', dot - 1))
        {
            patterns.Add(string.Concat(eventType.AsSpan(0, dot), ".*"));
        }

        patterns.Add(EventTypeRules.MatchAll);
        return patterns;
    }
}
