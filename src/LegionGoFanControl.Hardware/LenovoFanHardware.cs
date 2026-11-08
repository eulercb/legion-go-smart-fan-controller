using System.Management;
using LegionGoFanControl.Core;

namespace LegionGoFanControl.Hardware;

/// <summary>Conservative Lenovo WMI adapter. A timed-out operation permanently faults this instance.</summary>
public sealed class LenovoFanHardware : IFanHardware
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan timeout;
    private volatile bool faulted;
    private volatile bool ssdFaulted;
    private readonly SemaphoreSlim ssdGate = new(1, 1);
    private Task<(SystemSsdTemperature.Reading Reading, DateTimeOffset Time)>? ssdRead;
    private (SystemSsdTemperature.Reading Reading, DateTimeOffset Time)? lastSsd;
    public LenovoFanHardware(TimeSpan? timeout = null) => this.timeout = timeout ?? TimeSpan.FromSeconds(5);

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken)
    {
        if (faulted) throw new InvalidOperationException("WMI adapter is faulted after a timeout; restart is required because an operation may still be in flight.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (faulted) { gate.Release(); throw new InvalidOperationException("WMI adapter is faulted after a timeout."); }
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var task = Task.Run(() => { try { deadline.Token.ThrowIfCancellationRequested(); return operation(deadline.Token); } finally { gate.Release(); deadline.Dispose(); } });
        try { return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { faulted = true; try { deadline.Cancel(); } catch (ObjectDisposedException) { } throw new TimeoutException("WMI operation timed out. Its outcome may be unknown; this adapter is disabled until restart."); }
        catch (OperationCanceledException) { if (!task.IsCompleted) faulted = true; throw; }
    }
    public async Task<TemperatureSample> ReadTemperatureAsync(CancellationToken cancellationToken)
    {
        double cpu = double.NaN; string? cpuError = null; double? ssd = null; string? ssdError = null;
        await ssdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ssdFaulted && ssdRead is null)
                ssdRead = Task.Run(() => (SystemSsdTemperature.Read(), DateTimeOffset.UtcNow)).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { ssdGate.Release(); }
        try
        {
            cpu = await ExecuteAsync(_ =>
            {
                try { return WmiProtocol.Temperature(WmiProtocol.Call("LENOVO_GAMEZONE_DATA", "GetCPUTemp", null, "Data")); }
                catch { return WmiProtocol.Temperature(WmiProtocol.Feature(0x05040000)); }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { cpuError = "CPU temperature unavailable: " + ex.Message; }
        DateTimeOffset cpuTime = DateTimeOffset.UtcNow;
        await ssdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ssdFaulted) ssdError = "System SSD reader timed out; restart is required.";
            else if (ssdRead is null || !ssdRead.IsCompleted)
            {
                if (lastSsd is { } previous) ssd = previous.Reading.CompositeCelsius;
                else ssdError = "System SSD temperature query is pending.";
            }
            else
            {
                try { lastSsd = await ssdRead.ConfigureAwait(false); ssd = lastSsd.Value.Reading.CompositeCelsius; }
                catch (TimeoutException) { ssdFaulted = true; lastSsd = null; ssdError = "System SSD reader timed out; restart is required."; }
                catch (Exception ex) { lastSsd = null; ssdError = "System SSD temperature unavailable: " + ex.Message; }
                finally { ssdRead = null; }
            }
        }
        finally { ssdGate.Release(); }
        return new TemperatureSample(cpu, cpuTime, ssd, ssd.HasValue ? lastSsd?.Time : null, ssdError, cpuError);
    }
    public Task<bool> ReadFullSpeedAsync(CancellationToken cancellationToken) => ExecuteAsync(_ => WmiProtocol.FullSpeed(), cancellationToken);
    public Task<FanWriteResult> SetFullSpeedAsync(bool enabled, CancellationToken cancellationToken) => ExecuteAsync(operationToken =>
    {
        return FullSpeedOperation.Set(enabled, operationToken, WmiProtocol.RequireSupportedDevice, WmiProtocol.FullSpeed,
            value => WmiProtocol.Call("LENOVO_OTHER_METHOD", "SetFeatureValue", new Dictionary<string, uint> { ["IDs"] = 0x04020000, ["value"] = value ? 1u : 0u }, null));
    }, cancellationToken);
}

internal static class WmiProtocol
{
    internal static readonly string[] Classes = ["LENOVO_OTHER_METHOD", "LENOVO_GAMEZONE_DATA", "LENOVO_FAN_METHOD"];
    internal static void RequireSupportedDevice()
    {
        using var search = new ManagementObjectSearcher("root\\CIMV2", "SELECT Model, SystemFamily FROM Win32_ComputerSystem");
        using var results = search.Get();
        var rows = results.Cast<ManagementObject>().ToArray();
        try {
            if (rows.Length != 1 || !string.Equals(Convert.ToString(rows[0]["Model"]), "83E1", StringComparison.OrdinalIgnoreCase) || !string.Equals(Convert.ToString(rows[0]["SystemFamily"]), "Legion Go 8APU1", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsupported device: writes require model 83E1 and system family Legion Go 8APU1.");
        } finally { foreach (var row in rows) row.Dispose(); }
    }
    internal static uint Unsigned(object? value) => value switch { byte x => x, ushort x => x, uint x => x, sbyte x when x >= 0 => (uint)x, short x when x >= 0 => (uint)x, int x when x >= 0 => (uint)x, _ => throw new InvalidOperationException($"Unexpected WMI output type: {value?.GetType().Name ?? "null"}.") };
    internal static double Temperature(object? value) { var n = Unsigned(value); if (n is < 1 or > 110) throw new InvalidOperationException($"Implausible CPU temperature: {n} °C."); return n; }
    internal static bool FullSpeed() => Unsigned(Feature(0x04020000)) switch { 0 => false, 1 => true, var n => throw new InvalidOperationException($"Unexpected Full Speed value {n}.") };
    internal static object Feature(uint id) => Call("LENOVO_OTHER_METHOD", "GetFeatureValue", new Dictionary<string, uint> { ["IDs"] = id }, "Value")!;
    internal static object? Call(string className, string methodName, Dictionary<string, uint>? inputs, string? outputName)
    {
        using var klass = new ManagementClass(new ManagementScope("root\\WMI"), new ManagementPath(className), null);
        var method = klass.Methods.Cast<MethodData>().SingleOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"Missing {className}.{methodName}.");
        var inProps = method.InParameters?.Properties.Cast<PropertyData>().ToArray() ?? [];
        var outProps = method.OutParameters?.Properties.Cast<PropertyData>().ToArray() ?? [];
        if (inProps.Length != (inputs?.Count ?? 0) || inProps.Any(p => p.IsArray || p.Type != CimType.UInt32 || inputs is null || !inputs.Keys.Any(k => string.Equals(k, p.Name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException($"Incompatible input signature for {className}.{methodName}.");
        if (outputName is not null && !outProps.Any(p => string.Equals(p.Name, outputName, StringComparison.OrdinalIgnoreCase) && !p.IsArray && p.Type is CimType.UInt32 or CimType.UInt16 or CimType.UInt8 or CimType.SInt32 or CimType.SInt16 or CimType.SInt8))
            throw new InvalidOperationException($"Incompatible output signature for {className}.{methodName}: {outputName} missing or non-numeric.");
        using var instances = klass.GetInstances();
        var rows = instances.Cast<ManagementObject>().ToArray();
        try
        {
            if (rows.Length != 1) throw new InvalidOperationException($"Expected one {className} instance, found {rows.Length}.");
            using var args = rows[0].GetMethodParameters(methodName);
            if (inputs is not null) foreach (var pair in inputs) args[pair.Key] = pair.Value;
            using var result = rows[0].InvokeMethod(methodName, args, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(5) });
            // Lenovo methods are void/out. A ReturnValue is not required; if present it must be an explicit successful boolean.
            if (result is not null && result.Properties.Cast<PropertyData>().FirstOrDefault(p => p.Name.Equals("ReturnValue", StringComparison.OrdinalIgnoreCase)) is { } status && (status.Value is not bool ok || !ok))
                throw new InvalidOperationException($"{className}.{methodName} returned an unrecognized or failed status.");
            if (outputName is null) return null;
            var property = result?.Properties.Cast<PropertyData>().FirstOrDefault(p => p.Name.Equals(outputName, StringComparison.OrdinalIgnoreCase));
            return property?.Value ?? throw new InvalidOperationException($"{className}.{methodName} returned no {outputName}.");
        }
        finally { foreach (var row in rows) row.Dispose(); }
    }
}
