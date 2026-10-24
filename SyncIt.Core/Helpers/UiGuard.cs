namespace SyncIt.Core.Helpers;

/// <summary>
/// Runs UI actions (tray menu, dashboard links, Settings) so an exception is always reported and logged.
/// WinForms' ThreadException only sees exceptions raised from its message loop; actions started another way
/// (e.g. through Windows accessibility / UI Automation, which screen readers use) used to fail silently.
/// </summary>
public static class UiGuard
{
    /// <summary>Set once at start-up to the app's crash reporter (logs + crash file + friendly message).</summary>
    public static Action<Exception, string>? Reporter { get; set; }

    public static void Run(Action action, string source)
    {
        try { action(); }
        catch (Exception ex) when (Reporter is not null) { Report(ex, source); }
    }

    public static async Task RunAsync(Func<Task> action, string source)
    {
        try { await action(); }
        catch (Exception ex) when (Reporter is not null) { Report(ex, source); }
    }

    private static void Report(Exception ex, string source)
    {
        try { Reporter!(ex, source); } catch { /* the reporter must never throw */ }
    }
}
