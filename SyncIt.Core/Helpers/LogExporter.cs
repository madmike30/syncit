using System.IO.Compression;
using System.Text.RegularExpressions;

namespace SyncIt.Core.Helpers;

/// <summary>Builds a redacted zip of SyncIt logs for bug reports.</summary>
public static partial class LogExporter
{
    [GeneratedRegex(@"ya29\.[A-Za-z0-9_\-\.]+")] private static partial Regex AccessToken();
    [GeneratedRegex(@"1//[A-Za-z0-9_\-]{20,}")] private static partial Regex RefreshToken();
    [GeneratedRegex(@"GOCSPX-[A-Za-z0-9_\-]+")] private static partial Regex ClientSecret();
    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9_\-\.=]+")] private static partial Regex Bearer();
    [GeneratedRegex(@"(?i)((?:access_token|refresh_token|client_secret|code)[""']?\s*[:=]\s*[""']?)[^\s""'&,]+")] private static partial Regex KeyValue();
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")] private static partial Regex Email();

    /// <summary>Removes tokens, secrets and email addresses from a log line.</summary>
    public static string Redact(string text)
    {
        text = AccessToken().Replace(text, "[REDACTED-TOKEN]");
        text = RefreshToken().Replace(text, "[REDACTED-TOKEN]");
        text = ClientSecret().Replace(text, "[REDACTED-SECRET]");
        text = Bearer().Replace(text, "$1[REDACTED]");
        text = KeyValue().Replace(text, "$1[REDACTED]");
        text = Email().Replace(text, "[REDACTED-EMAIL]");
        return text;
    }

    /// <summary>Zips every *.log under <paramref name="logDir"/> (redacted) into <paramref name="zipPath"/>. Returns file count.</summary>
    public static int Export(string logDir, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        var count = 0;
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        if (!Directory.Exists(logDir)) return 0;
        foreach (var file in Directory.EnumerateFiles(logDir, "*.log"))
        {
            string content;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                content = sr.ReadToEnd();
            var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open());
            w.Write(Redact(content));
            count++;
        }
        return count;
    }

    /// <summary>Writes a crash report into the log directory and returns its path.</summary>
    public static string WriteCrashLog(string logDir, Exception ex, string version)
    {
        Directory.CreateDirectory(logDir);
        var path = Path.Combine(logDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.WriteAllText(path, Redact(
            $"SyncIt {version} crash at {DateTimeOffset.Now:O}{Environment.NewLine}" +
            $"OS: {Environment.OSVersion}{Environment.NewLine}{ex}{Environment.NewLine}"));
        return path;
    }
}
