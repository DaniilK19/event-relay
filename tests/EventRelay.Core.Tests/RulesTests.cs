namespace EventRelay.Core.Tests;

public class RulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("flight.delayed", true)]
    [InlineData("flight.gate_changed", true)]
    [InlineData("order-service.v2.created", true)]
    [InlineData("ping", true)]
    [InlineData("", false)]
    [InlineData("Flight.Delayed", false)]
    [InlineData("flight..delayed", false)]
    [InlineData(".flight", false)]
    [InlineData("flight.", false)]
    [InlineData("flight.*", false)]
    [InlineData("flight delayed", false)]
    public void IsValidType(string type, bool expected)
    {
        Assert.Equal(expected, EventTypeRules.IsValidType(type));
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("flight.*", true)]
    [InlineData("flight.gate.*", true)]
    [InlineData("flight.delayed", true)]
    [InlineData("*.delayed", false)]
    [InlineData("flight.*.changed", false)]
    [InlineData("flight*", false)]
    [InlineData(".*", false)]
    public void IsValidPattern(string pattern, bool expected)
    {
        Assert.Equal(expected, EventTypeRules.IsValidPattern(pattern));
    }

    [Fact]
    public void Attribute_validation_reports_bad_keys_values_and_counts()
    {
        Assert.Null(AttributeRules.Validate(null));
        Assert.Null(AttributeRules.Validate(new Dictionary<string, string> { ["airline"] = "CT" }));

        Assert.NotNull(AttributeRules.Validate(new Dictionary<string, string> { ["1bad"] = "x" }));
        Assert.NotNull(AttributeRules.Validate(new Dictionary<string, string> { ["key"] = new string('x', 257) }));

        var tooMany = Enumerable.Range(0, AttributeRules.MaxCount + 1).ToDictionary(i => $"k{i}", _ => "v");
        Assert.NotNull(AttributeRules.Validate(tooMany));
    }

    [Theory]
    [InlineData("https://hooks.example.test/a", true)]
    [InlineData("http://webhook-receiver:8080/webhooks/all", true)]
    [InlineData("ftp://hooks.example.test/a", false)]
    [InlineData("https://user:pass@hooks.example.test/a", false)]
    [InlineData("https://hooks.example.test/a#frag", false)]
    public void Webhook_url_structure(string url, bool expected)
    {
        Assert.Equal(expected, Subscription.IsSupportedWebhookUrl(new Uri(url)));
    }

    [Fact]
    public void Subscription_rejects_invalid_input()
    {
        var url = new Uri("https://hooks.example.test/a");
        var secret = Subscription.GenerateSecret();

        Assert.Throws<ArgumentException>(() => Subscription.Create("s", [], null, url, secret, Now));
        Assert.Throws<ArgumentException>(() => Subscription.Create("s", ["Not Valid"], null, url, secret, Now));
        Assert.Throws<ArgumentException>(() => Subscription.Create("s", ["a.b"], null, url, "short", Now));
        Assert.Throws<ArgumentException>(() =>
            Subscription.Create("s", ["a.b"], null, new Uri("ftp://hooks.example.test"), secret, Now));
    }

    [Fact]
    public void Generated_secrets_are_long_and_unique()
    {
        var a = Subscription.GenerateSecret();
        var b = Subscription.GenerateSecret();

        Assert.StartsWith("whsec_", a);
        Assert.True(a.Length >= 40);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Event_rejects_invalid_input()
    {
        Assert.Throws<ArgumentException>(() => new Event(Guid.Empty, "a.b", "src", Now, null, null, Now));
        Assert.Throws<ArgumentException>(() => new Event(Guid.NewGuid(), "A B", "src", Now, null, null, Now));
        Assert.Throws<ArgumentException>(() => new Event(Guid.NewGuid(), "a.b", " ", Now, null, null, Now));
    }

    [Fact]
    public void Event_normalizes_times_to_utc()
    {
        var local = new DateTimeOffset(2026, 1, 1, 15, 0, 0, TimeSpan.FromHours(3));

        var evt = new Event(Guid.NewGuid(), "a.b", "src", local, null, null, local);

        Assert.Equal(TimeSpan.Zero, evt.Time.Offset);
        Assert.Equal(local, evt.Time);
    }
}
