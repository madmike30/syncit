using FluentAssertions;
using SyncIt.Core.Helpers;

namespace SyncIt.Tests.Helpers;

[Collection("UiGuard")] // static reporter: run these tests one at a time
public sealed class UiGuardTests : IDisposable
{
    private readonly List<(Exception Ex, string Source)> _reports = new();

    public UiGuardTests() => UiGuard.Reporter = (ex, source) => _reports.Add((ex, source));
    public void Dispose() => UiGuard.Reporter = null;

    [Fact]
    public void ExceptionFromAUiAction_IsReportedWithItsSource_NotLost()
    {
        var act = () => UiGuard.Run(() => throw new NullReferenceException("settings layout"), "ShowSettings");
        act.Should().NotThrow();
        _reports.Should().ContainSingle();
        _reports[0].Source.Should().Be("ShowSettings");
        _reports[0].Ex.Should().BeOfType<NullReferenceException>();
    }

    [Fact]
    public async Task ExceptionFromAnAsyncUiAction_IsReported()
    {
        await UiGuard.RunAsync(async () => { await Task.Yield(); throw new InvalidOperationException("update"); }, "InstallUpdate");
        _reports.Should().ContainSingle().Which.Source.Should().Be("InstallUpdate");
    }

    [Fact]
    public void SuccessfulAction_ReportsNothing()
    {
        var ran = false;
        UiGuard.Run(() => ran = true, "x");
        ran.Should().BeTrue();
        _reports.Should().BeEmpty();
    }

    [Fact]
    public void ReporterThatThrows_NeverCrashesTheCaller()
    {
        UiGuard.Reporter = (_, _) => throw new Exception("reporter broke");
        var act = () => UiGuard.Run(() => throw new Exception("boom"), "x");
        act.Should().NotThrow();
    }

    [Fact]
    public void WithoutReporter_ExceptionPropagatesToWinForms()
    {
        UiGuard.Reporter = null;
        var act = () => UiGuard.Run(() => throw new InvalidOperationException(), "x");
        act.Should().Throw<InvalidOperationException>();
    }
}
