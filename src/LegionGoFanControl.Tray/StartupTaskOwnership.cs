namespace LegionGoFanControl.Tray;

internal static class StartupTaskOwnership
{
    internal const string Marker = "LegionGoFanControl managed startup task v1";

    internal static bool IsOwned(string? marker, string? principal, int logonType, int actionCount, string? actionPath, string currentSid, Func<string, string?> resolveSid)
    {
        if (marker != Marker || string.IsNullOrWhiteSpace(principal) || logonType != 3 || actionCount != 1 || !string.Equals(Path.GetFileName(actionPath), "LegionGoFanControl.Tray.exe", StringComparison.OrdinalIgnoreCase)) return false;
        string? sid;
        try { sid = resolveSid(principal); }
        catch (Exception) { return false; }
        return string.Equals(sid, currentSid, StringComparison.OrdinalIgnoreCase);
    }
}
