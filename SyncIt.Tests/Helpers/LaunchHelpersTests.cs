using System.IO.Compression;
using FluentAssertions;
using SyncIt.Core.Helpers;

namespace SyncIt.Tests.Helpers;

public class OAuthClientValidatorTests
{
    [Theory]
    [InlineData("123456789012-abcdefghij0123456789klmnopqr.apps.googleusercontent.com")]
    [InlineData("  123456789012-abcdefghijklmnop.apps.googleusercontent.com ")]
    public void ValidClientId_Passes(string id) => OAuthClientValidator.ValidateClientId(id).Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("GOCSPX-abcdefghijklmnopqrstuvwx")]
    [InlineData("123456789012-abc.example.com")]
    public void InvalidClientId_Fails(string? id) => OAuthClientValidator.ValidateClientId(id).Should().NotBeNull();

    [Fact]
    public void ValidSecret_Passes() => OAuthClientValidator.ValidateClientSecret("GOCSPX-AbCdEfGhIjKlMnOpQrStUvWxYz12").Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData("has spaces in it which is wrong")]
    [InlineData("123456789012-abcdefghijklmnop.apps.googleusercontent.com")]
    public void InvalidSecret_Fails(string s) => OAuthClientValidator.ValidateClientSecret(s).Should().NotBeNull();
}

public class LogExporterTests
{
    [Theory]
    [InlineData("Authorization: Bearer ya29.a0AfH6SMBxyz", "ya29.")]
    [InlineData("refresh 1//0gAbCdEfGhIjKlMnOpQrStUvWx", "1//0gAb")]
    [InlineData("secret GOCSPX-AbCdEf123", "GOCSPX-AbCdEf")]
    [InlineData("{\"access_token\": \"abc123\"}", "abc123")]
    [InlineData("user someone@gmail.com signed in", "someone@gmail.com")]
    public void Redact_RemovesSensitiveValues(string input, string mustNotContain)
        => LogExporter.Redact(input).Should().NotContain(mustNotContain);

    [Fact]
    public void Redact_KeepsOrdinaryText()
        => LogExporter.Redact("Uploaded Reports/q1.xlsx (12 KB)").Should().Be("Uploaded Reports/q1.xlsx (12 KB)");

    [Fact]
    public void Export_ZipsRedactedLogs()
    {
        var dir = Directory.CreateTempSubdirectory("syncit-logs").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "syncit-20260101.log"), "token ya29.secretvalue ok");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignored");
            var zipPath = Path.Combine(dir, "out.zip");

            LogExporter.Export(dir, zipPath).Should().Be(1);

            using var zip = ZipFile.OpenRead(zipPath);
            zip.Entries.Should().ContainSingle();
            using var r = new StreamReader(zip.Entries[0].Open());
            var text = r.ReadToEnd();
            text.Should().Contain("ok").And.NotContain("ya29.secretvalue");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteCrashLog_CreatesFile()
    {
        var dir = Directory.CreateTempSubdirectory("syncit-crash").FullName;
        try
        {
            var path = LogExporter.WriteCrashLog(dir, new InvalidOperationException("boom"), "1.0.0");
            File.ReadAllText(path).Should().Contain("boom").And.Contain("1.0.0");
        }
        finally { Directory.Delete(dir, true); }
    }
}
