namespace SyncIt.Core.Helpers;

/// <summary>
/// Windows UI features such as drag-and-drop and folder pickers only work on a
/// single-threaded apartment (STA) thread. Turning them on from an MTA thread throws
/// ("DragDrop registration did not succeed"), which crashed the 1.1.1 sign-in window.
/// </summary>
public static class ApartmentGuard
{
    public static bool CanUseOleFeatures(Thread? thread = null) =>
        OperatingSystem.IsWindows() && (thread ?? Thread.CurrentThread).GetApartmentState() == ApartmentState.STA;
}
