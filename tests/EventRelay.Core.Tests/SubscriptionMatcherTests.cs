namespace EventRelay.Core.Tests;

public class SubscriptionMatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, string> Attributes = new()
    {
        ["airline"] = "CT",
        ["origin"] = "NTH",
    };

    [Theory]
    [InlineData("flight.delayed", "flight.delayed", true)]
    [InlineData("flight.delayed", "flight.cancelled", false)]
    [InlineData("flight.*", "flight.delayed", true)]
    [InlineData("flight.*", "flight.gate.changed", true)]
    [InlineData("flight.*", "flight", false)]
    [InlineData("flight.*", "flights.delayed", false)]
    [InlineData("flight.gate.*", "flight.gate.changed", true)]
    [InlineData("flight.gate.*", "flight.delayed", false)]
    [InlineData("*", "anything.at.all", true)]
    [InlineData("Flight.delayed", "flight.delayed", false)]
    public void MatchesType_follows_exact_prefix_and_catch_all_rules(string pattern, string type, bool expected)
    {
        Assert.Equal(expected, SubscriptionMatcher.MatchesType(pattern, type));
    }

    [Fact]
    public void Empty_filter_matches_any_attributes()
    {
        Assert.True(SubscriptionMatcher.MatchesFilter(new Dictionary<string, string>(), Attributes));
        Assert.True(SubscriptionMatcher.MatchesFilter(new Dictionary<string, string>(), new Dictionary<string, string>()));
    }

    [Fact]
    public void Filter_requires_every_entry_to_match()
    {
        var filter = new Dictionary<string, string> { ["airline"] = "CT", ["origin"] = "NTH" };
        Assert.True(SubscriptionMatcher.MatchesFilter(filter, Attributes));

        filter["origin"] = "STH";
        Assert.False(SubscriptionMatcher.MatchesFilter(filter, Attributes));
    }

    [Fact]
    public void Filter_on_missing_attribute_does_not_match()
    {
        var filter = new Dictionary<string, string> { ["gate"] = "A1" };
        Assert.False(SubscriptionMatcher.MatchesFilter(filter, Attributes));
    }

    [Fact]
    public void Filter_values_are_case_sensitive()
    {
        var filter = new Dictionary<string, string> { ["airline"] = "ct" };
        Assert.False(SubscriptionMatcher.MatchesFilter(filter, Attributes));
    }

    [Fact]
    public void Matches_combines_type_and_filter()
    {
        var subscription = CreateSubscription(["flight.*"], new Dictionary<string, string> { ["airline"] = "CT" });

        Assert.True(SubscriptionMatcher.Matches(subscription, "flight.delayed", Attributes));
        Assert.False(SubscriptionMatcher.Matches(subscription, "baggage.lost", Attributes));
        Assert.False(SubscriptionMatcher.Matches(
            subscription, "flight.delayed", new Dictionary<string, string> { ["airline"] = "FB" }));
    }

    [Fact]
    public void Inactive_subscription_never_matches()
    {
        var subscription = CreateSubscription(["*"], filter: null);
        subscription.Update(subscription.Name, subscription.EventTypes, subscription.Filter,
            new Uri(subscription.WebhookUrl), isActive: false, Now);

        Assert.False(SubscriptionMatcher.Matches(subscription, "flight.delayed", Attributes));
    }

    [Fact]
    public void CandidatePatterns_lists_the_type_every_prefix_wildcard_and_catch_all()
    {
        Assert.Equal(
            ["flight.gate.changed", "flight.gate.*", "flight.*", "*"],
            SubscriptionMatcher.CandidatePatterns("flight.gate.changed"));

        Assert.Equal(["ping", "*"], SubscriptionMatcher.CandidatePatterns("ping"));
    }

    [Theory]
    [InlineData("flight.delayed")]
    [InlineData("flight.gate.changed")]
    [InlineData("flight.gate_changed")]
    [InlineData("ping")]
    public void CandidatePatterns_agree_with_MatchesType(string type)
    {
        string[] patterns =
        [
            "*", "flight.*", "flight.gate.*", "flight.delayed", "flight.gate.changed",
            "flight.gate_changed", "ping", "ping.*", "flight.delayed.*", "baggage.*",
        ];

        var candidates = SubscriptionMatcher.CandidatePatterns(type);

        foreach (var pattern in patterns)
        {
            Assert.Equal(SubscriptionMatcher.MatchesType(pattern, type), candidates.Contains(pattern));
        }
    }

    private static Subscription CreateSubscription(string[] eventTypes, Dictionary<string, string>? filter) =>
        Subscription.Create(
            "test",
            eventTypes,
            filter,
            new Uri("https://hooks.example.test/events"),
            Subscription.GenerateSecret(),
            Now);
}
