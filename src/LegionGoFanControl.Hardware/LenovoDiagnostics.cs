using System.Management;

namespace LegionGoFanControl.Hardware;

/// <summary>Read-only discovery; errors are included per item so partial reports remain useful.</summary>
public static class LenovoDiagnostics
{
    public static async Task<DiagnosticReport> CollectAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var task = Task.Run(Collect, CancellationToken.None);
        return await task.WaitAsync(timeout ?? TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
    }
    private static DiagnosticReport Collect()
    {
        var report = new DiagnosticReport { Timestamp = DateTimeOffset.UtcNow };
        Capture(report.Items, "computerSystem", () => Query("root\\CIMV2", "SELECT Model, SystemFamily FROM Win32_ComputerSystem"));
        Capture(report.Items, "bios", () => Query("root\\CIMV2", "SELECT SMBIOSBIOSVersion, Version, Manufacturer FROM Win32_BIOS"));
        Capture(report.Items, "compatibleDevice", () => { WmiProtocol.RequireSupportedDevice(); return true; });
        foreach (var name in WmiProtocol.Classes) Capture(report.Items, $"class.{name}", () => Describe(name));
        Capture(report.Items, "temperature.gameZoneCelsius", () => WmiProtocol.Temperature(WmiProtocol.Call("LENOVO_GAMEZONE_DATA", "GetCPUTemp", null, "Data")));
        Capture(report.Items, "temperature.featureCelsius", () => WmiProtocol.Temperature(WmiProtocol.Feature(0x05040000)));
        Capture(report.Items, "fullSpeed", () => WmiProtocol.FullSpeed());
        Capture(report.Items, "fanSpeedRawUncalibrated", () => WmiProtocol.Unsigned(WmiProtocol.Feature(0x04030001)));
        Capture(report.Items, "smartFanMode", () => WmiProtocol.Unsigned(WmiProtocol.Call("LENOVO_GAMEZONE_DATA", "GetSmartFanMode", null, "Data")));
        return report;
    }
    private static void Capture(Dictionary<string, DiagnosticItem> target, string key, Func<object> action)
    {
        try { target[key] = new DiagnosticItem { Value = action() }; }
        catch (Exception ex) { target[key] = new DiagnosticItem { Error = $"{ex.GetType().Name}: {ex.Message}", HResult = $"0x{ex.HResult:X8}" }; }
    }
    private static object Query(string scope, string query)
    {
        using var search = new ManagementObjectSearcher(scope, query);
        using var results = search.Get();
        var rows = results.Cast<ManagementObject>().ToArray();
        try { return rows.Select(row => row.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => DescribeValue(p))).ToArray(); }
        finally { foreach (var row in rows) row.Dispose(); }
    }
    private static object Describe(string name)
    {
        using var klass = new ManagementClass(new ManagementScope("root\\WMI"), new ManagementPath(name), null);
        using var instances = klass.GetInstances();
        return new { InstanceCount = instances.Count, Methods = klass.Methods.Cast<MethodData>().Select(m => new { m.Name, Input = DescribeParameters(m.InParameters), Output = DescribeParameters(m.OutParameters) }).ToArray() };
    }
    private static object[] DescribeParameters(ManagementBaseObject? parameters) => parameters?.Properties.Cast<PropertyData>().Select(p => (object)new { p.Name, CimType = p.Type.ToString(), p.IsArray }).ToArray() ?? [];
    private static object DescribeValue(PropertyData p) => new { p.Value, ClrType = p.Value?.GetType().FullName, CimType = p.Type.ToString(), p.IsArray };
}
public sealed class DiagnosticReport
{
    public DateTimeOffset Timestamp { get; init; }
    public Dictionary<string, DiagnosticItem> Items { get; } = new();
}
public sealed class DiagnosticItem
{
    public object? Value { get; init; }
    public string? Error { get; init; }
    public string? HResult { get; init; }
}
