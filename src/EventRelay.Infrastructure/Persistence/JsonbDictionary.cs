using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventRelay.Infrastructure.Persistence;

/// <summary>Stores a string dictionary (attributes, filters) as a jsonb object.</summary>
internal static class JsonbDictionary
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);

    public static PropertyBuilder<IReadOnlyDictionary<string, string>> IsJsonbDictionary(
        this PropertyBuilder<IReadOnlyDictionary<string, string>> property) =>
        property
            .HasConversion(
                value => JsonSerializer.Serialize(value, SerializerOptions),
                json => Deserialize(json),
                new ValueComparer<IReadOnlyDictionary<string, string>>(
                    (left, right) => AreEqual(left, right),
                    value => GetHashCode(value),
                    value => new Dictionary<string, string>(value, StringComparer.Ordinal)))
            .HasColumnType("jsonb");

    private static IReadOnlyDictionary<string, string> Deserialize(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, SerializerOptions)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static bool AreEqual(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // Order-independent, consistent with AreEqual.
    private static int GetHashCode(IReadOnlyDictionary<string, string> value) =>
        value.Aggregate(0, (hash, entry) => hash ^ HashCode.Combine(entry.Key, entry.Value));
}
