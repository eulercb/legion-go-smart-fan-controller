using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LegionGoFanControl.Tray;

internal static class StartupTaskLookup
{
    // The COM interop layer maps HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) to
    // FileNotFoundException on some .NET versions, rather than COMException.
    internal static bool IsMissing(Exception ex) =>
        (ex is COMException or FileNotFoundException && (uint)ex.HResult == 0x80070002)
        || (ex is Win32Exception win32 && win32.NativeErrorCode == 2);

    internal static T? Find<T>(Func<T> lookup) where T : class
    {
        try { return lookup(); }
        catch (Exception ex) when (IsMissing(ex)) { return null; }
    }
}
