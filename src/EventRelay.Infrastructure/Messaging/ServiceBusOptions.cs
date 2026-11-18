namespace EventRelay.Infrastructure.Messaging;

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// Connection string (local emulator, or SAS). Usually supplied as
    /// <c>ConnectionStrings:ServiceBus</c>. Ignored when <see cref="FullyQualifiedNamespace"/> is set.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// For Azure: <c>my-namespace.servicebus.windows.net</c>. Authenticates with
    /// <c>DefaultAzureCredential</c> (managed identity in Container Apps), so no key is stored anywhere.
    /// </summary>
    public string? FullyQualifiedNamespace { get; set; }

    public string TopicName { get; set; } = "events";

    /// <summary>Topic subscription the dispatcher reads from.</summary>
    public string SubscriptionName { get; set; } = "dispatcher";

    public int MaxConcurrentCalls { get; set; } = 8;
}
