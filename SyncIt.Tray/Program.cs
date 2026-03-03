using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SyncIt.Core.Helpers;
using SyncIt.Core.Ipc;
using SyncIt.Core.Models;
using SyncIt.Tray;

[assembly: SupportedOSPlatform("windows")]

// Single-instance check
var mutex = new Mutex(true, "SyncIt-Tray-SingleInstance", out var isFirstInstance);
if (!isFirstInstance)
{
    MessageBox.Show("SyncIt Tray is already running.", "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

// Configure Serilog for tray app
var logDir = Environment.ExpandEnvironmentVariables(@"%APPDATA%\SyncIt\logs");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.WithProcessId()
    .WriteTo.Console(
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.Async(a => a.File(
        Path.Combine(logDir, "tray-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{MachineName}] [PID:{ProcessId}] [TID:{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}"))
    .CreateLogger();

try
{
    // Build DI container
    var host = Host.CreateDefaultBuilder()
        .UseSerilog()
        .ConfigureServices((_, services) =>
        {
            services.AddSingleton<TrayApp>();
            services.AddSingleton<IpcClient>(sp =>
            {
                // Load config to get pipe name
                var configPath = PathHelper.Expand(@"%APPDATA%\SyncIt\syncit.json");
                string pipeName = "SyncItIpc"; // default
                if (File.Exists(configPath))
                {
                    try
                    {
                        var json   = File.ReadAllText(configPath);
                        var config = System.Text.Json.JsonSerializer.Deserialize<SyncConfig>(json,
                            new System.Text.Json.JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true,
                                ReadCommentHandling         = System.Text.Json.JsonCommentHandling.Skip,
                                AllowTrailingCommas         = true,
                                Converters                  = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                            });
                        if (config?.PipeName is not null) pipeName = config.PipeName;
                    }
                    catch { }
                }
                return new IpcClient(pipeName, sp.GetRequiredService<ILogger<IpcClient>>());
            });
        })
        .Build();

    var trayApp = host.Services.GetRequiredService<TrayApp>();

    // Handle --auth-only flag (first-run OAuth from command line)
    if (args.Contains("--auth-only"))
    {
        trayApp.RunAuthOnly();
        return;
    }

    Application.Run(trayApp);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Tray app crashed.");
    MessageBox.Show($"SyncIt Tray crashed:\n{ex.Message}", "SyncIt Error",
        MessageBoxButtons.OK, MessageBoxIcon.Error);
}
finally
{
    mutex.ReleaseMutex();
    await Log.CloseAndFlushAsync();
}
