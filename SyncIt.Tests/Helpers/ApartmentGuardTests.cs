using FluentAssertions;
using SyncIt.Core.Helpers;

namespace SyncIt.Tests.Helpers;

public sealed class ApartmentGuardTests
{
    private static bool RunOn(ApartmentState state)
    {
        var result = true;
        var t = new Thread(() => result = ApartmentGuard.CanUseOleFeatures());
        if (OperatingSystem.IsWindows()) t.SetApartmentState(state);
        t.Start();
        t.Join();
        return result;
    }

    [Fact]
    public void MtaThread_CannotUseDragAndDrop()
    {
        // The crash: the sign-in window enabled drag-and-drop on an MTA thread.
        RunOn(ApartmentState.MTA).Should().BeFalse();
    }

    [Fact]
    public void StaThread_CanUseDragAndDrop_OnWindows()
    {
        RunOn(ApartmentState.STA).Should().Be(OperatingSystem.IsWindows());
    }
}
