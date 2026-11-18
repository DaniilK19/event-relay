using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EventRelay.Api.Security;

/// <summary>
/// API keys from configuration (<c>ApiKeys:Publisher:0</c>, <c>ApiKeys:Admin:0</c>...). Each role
/// takes a list so a key can be rotated without downtime: add the new key, move clients, remove the old one.
/// </summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "ApiKeys";
    public const int MinKeyLength = 32;

    /// <summary>May publish events and read their delivery status.</summary>
    public string[] Publisher { get; set; } = [];

    /// <summary>Everything a publisher can do, plus managing subscriptions.</summary>
    public string[] Admin { get; set; } = [];
}

public static class ApiKeyRoles
{
    public const string Publisher = "publisher";
    public const string Admin = "admin";
}

public static class ApiKeyPolicies
{
    /// <summary>Publisher or admin key.</summary>
    public const string Publish = "publish";

    /// <summary>Admin key only.</summary>
    public const string Admin = "admin";
}

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ApiKeyOptions> keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    // Keys are compared as SHA-256 hashes with a fixed-time comparison: equal-length inputs,
    // and the time taken does not reveal how many leading characters of a guess were right.
    private readonly (byte[] Hash, string Role)[] knownKeys =
    [
        .. keys.Value.Admin.Select(key => (Hash(key), ApiKeyRoles.Admin)),
        .. keys.Value.Publisher.Select(key => (Hash(key), ApiKeyRoles.Publisher)),
    ];

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = Hash(values.ToString());
        foreach (var (hash, role) in knownKeys)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, hash))
            {
                var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, role), new Claim(ClaimTypes.Role, role)], SchemeName);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }
        }

        // Neither the key nor a hint about it ends up in the logs.
        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"{SchemeName} header=\"{HeaderName}\"";
        return base.HandleChallengeAsync(properties);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

public static class ApiKeyServiceCollectionExtensions
{
    /// <summary>
    /// API key authentication with two roles. Every endpoint requires a key unless it opts out
    /// with <c>AllowAnonymous</c> (health, OpenAPI document, API reference), so a newly added
    /// endpoint is protected by default.
    /// </summary>
    public static IServiceCollection AddApiKeyAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiKeyOptions>()
            .Bind(configuration.GetSection(ApiKeyOptions.SectionName))
            .Validate(
                o => o.Admin.Length + o.Publisher.Length > 0,
                "No API keys configured. Set ApiKeys:Admin:0 and ApiKeys:Publisher:0 (see README, 'Configuration').")
            .Validate(
                o => o.Admin.Concat(o.Publisher).All(key => key.Length >= ApiKeyOptions.MinKeyLength),
                $"Every API key must be at least {ApiKeyOptions.MinKeyLength} characters long.")
            .Validate(
                o => o.Admin.Concat(o.Publisher).Distinct(StringComparer.Ordinal).Count() == o.Admin.Length + o.Publisher.Length,
                "An API key may appear only once across ApiKeys:Admin and ApiKeys:Publisher.")
            .ValidateOnStart();

        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

        services.AddAuthorizationBuilder()
            .AddPolicy(ApiKeyPolicies.Publish, policy => policy.RequireRole(ApiKeyRoles.Publisher, ApiKeyRoles.Admin))
            .AddPolicy(ApiKeyPolicies.Admin, policy => policy.RequireRole(ApiKeyRoles.Admin))
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }
}
