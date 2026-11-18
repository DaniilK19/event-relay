using EventRelay.Dispatcher;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDispatcher(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
