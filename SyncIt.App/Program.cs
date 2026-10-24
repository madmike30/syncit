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

// ─── Crash handlers ──────────────────────────────────────────────────────────────
void ReportCrash(Exception ex, string source)
{
    try
    {
        Log.Fatal(ex, "Unhandled exception ({Source}).", source);
        var path = LogExporter.WriteCrashLog(logDir, ex, SyncIt.App.AppInfo.Version);
        MessageBox.Show($"SyncIt hit an unexpected error and wrote a crash report:\n{path}\n\nUse tray menu > Export Logs to share it.",
            "SyncIt Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    catch { /* never throw from the crash handler */ }
}
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
Application.ThreadException += (_, e) => ReportCrash(e.Exception, "UI thread");
AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) ReportCrash(ex, "AppDomain"); };
TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error(e.Exception, "Unobserved task exception."); e.SetObserved(); };

Log.Information("SyncIt {Version} starting.", SyncIt.App.AppInfo.Version);

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
    ReportCrash(ex, "main");
}
finally
{
    mutex.ReleaseMutex();
    await Log.CloseAndFlushAsync();
}
