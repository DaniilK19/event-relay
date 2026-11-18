using Microsoft.AspNetCore.Http.HttpResults;

namespace EventRelay.Api.Endpoints;

/// <summary>Collects field errors and turns them into an RFC 9457 validation problem.</summary>
internal sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> errors = new(StringComparer.Ordinal);

    public bool IsValid => errors.Count == 0;

    public void Add(string field, string message)
    {
        if (!errors.TryGetValue(field, out var messages))
        {
            errors[field] = messages = [];
        }

        messages.Add(message);
    }

    public ValidationProblem ToProblem() =>
        TypedResults.ValidationProblem(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
}
