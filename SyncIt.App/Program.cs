using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Serilog;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;
using SyncIt.Core.Safety;
using SyncIt.Core.Services;

[assembly: SupportedOSPlatform("windows10.0.19041.0")]

// ─── Single instance ───────────────────────────────────────────────────────────
var mutex = new Mutex(true, "Global\\SyncIt-SingleInstance", out var isFirst);
if (!isFirst)
{
    MessageBox.Show("SyncIt is already running.", "SyncIt", MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

// ─── Logging ────────────────────────────────────────────────────────────────────
var logDir = PathHelper.Expand(@"%APPDATA%\SyncIt\logs");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithThreadId()
    .Enrich.WithProcessId()
    .WriteTo.Async(a => a.File(
        Path.Combine(logDir, "syncit-.log"),
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [TID:{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}"))
    .CreateLogger();

try
{
    using var loggerFactory = LoggerFactory.Create(b => b.AddSerilog(dispose: false));
    var app = new SyncIt.App.SyncItApp(loggerFactory);

    if (args.Contains("--auth-only"))
    {
        app.RunAuthOnly();
        return;
    }

    Application.Run(app);
}
catch (Exception ex)
{
    Log.Fatal(ex, "SyncIt crashed.");
    MessageBox.Show($"SyncIt crashed:\n{ex.Message}", "SyncIt Error",
        MessageBoxButtons.OK, MessageBoxIcon.Error);
}
finally
{
    mutex.ReleaseMutex();
    await Log.CloseAndFlushAsync();
}
