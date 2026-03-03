using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Services;
using SyncIt.Service;

[assembly: SupportedOSPlatform("windows10.0.19041.0")]

// Single-instance guard — prevents two concurrent service processes from racing each other
// (e.g. two "dotnet run" windows open at the same time both trying to upload the same files)
var mutex = new Mutex(initiallyOwned: true, name: "Global\\SyncItServiceMutex", out var isNewInstance);
if (!isNewInstance)
{
    Console.Error.WriteLine("Another SyncIt service instance is already running. Exiting.");
    return 1;
}

var configPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "SyncIt",
    "syncit.json");

// Load config
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
    Console.Error.WriteLine("Run SyncIt.Tray.exe first to set up your credentials and sync pairs.");
    return 1;
}

var config = JsonSerializer.Deserialize<SyncConfig>(
    File.ReadAllText(configPath),
    new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        Converters                  = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    })
    ?? throw new InvalidOperationException("Failed to parse syncit.json");

// Configure Serilog before host builds
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .Build())
    .CreateLogger();

try
{
    var host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(opts => opts.ServiceName = "SyncItService")
        .UseSerilog()
        .ConfigureServices((ctx, services) =>
        {
            // Register config and config watcher as singletons
            services.AddSingleton(config);
            services.AddSingleton<IConfigWatcher>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ConfigWatcher>>();
                return new ConfigWatcher(configPath, config, logger);
            });

            // Register the worker
            services.AddHostedService<SyncWorker>();
        })
        .Build();

    Log.Information("SyncIt Service host built. Starting...");
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "SyncIt Service failed to start.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
