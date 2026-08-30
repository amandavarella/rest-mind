using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RestMind.Core;
using RestMind.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "RestMind");
builder.Services.AddHostedService<Worker>();

builder.Logging.AddEventLog(settings => settings.SourceName = "RestMind");
builder.Logging.SetMinimumLevel(LogLevel.Information);

// A rolling file in ProgramData is easier to read than the event log when something
// misbehaves on the child's machine.
var paths = new RestMindPaths();
paths.EnsureCreated();
builder.Logging.AddProvider(new FileLoggerProvider(paths.LogDirectory));

var host = builder.Build();
await host.RunAsync();
