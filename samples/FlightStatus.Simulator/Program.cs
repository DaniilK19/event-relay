using FlightStatus.Simulator;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<SimulatorOptions>()
    .Bind(builder.Configuration.GetSection(SimulatorOptions.SectionName))
    .Validate(o => o.ApiKey.Length > 0, "Simulator:ApiKey (an EventRelay publisher key) is required (set Simulator__ApiKey).")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient(FlightEventGenerator.HttpClientName, (provider, client) =>
{
    var settings = provider.GetRequiredService<IOptions<SimulatorOptions>>().Value;
    client.BaseAddress = settings.EventRelayUrl;
    client.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHostedService<FlightEventGenerator>();

var host = builder.Build();
await host.RunAsync();
