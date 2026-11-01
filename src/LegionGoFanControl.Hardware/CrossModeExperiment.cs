using System.Text.Json;
using LegionGoFanControl.Core;

namespace LegionGoFanControl.Hardware;

/// <summary>Explicitly invoked, isolated diagnostic. Never used by the controller.</summary>
public static class CrossModeExperiment
{
    private static readonly uint[] Modes = [1, 2, 3, 255];
    private static bool stopRequested;
    private static double maximumTestTemperature;
    private static bool faulted;
    private static bool uncertain;
    private static bool owned;
    private static uint? expectedMode;
    private static uint originalMode;
    internal static Func<int, Task> Delay = Task.Delay;
    internal static Func<ControllerOptions> LoadOptions = () => {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegionGoFanControl", "settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ControllerOptions>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty settings.") : new ControllerOptions();
    };
    internal static void RequestStop() => stopRequested = true;
    private static void Log(string stage, object value) => Console.WriteLine(JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, stage, value }));
    private static async Task<T> Call<T>(Func<T> action)
    {
        if (faulted) throw new InvalidOperationException("WMI adapter faulted; no further operations permitted.");
        var task = Task.Run(action);
        try { return await task.WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (TimeoutException) { faulted = true; uncertain = true; throw new TimeoutException("WMI timed out; operation may still be in flight. No further operations will be issued."); }
    }
    private static Task<uint> Mode() => Call(() => WmiProtocol.Unsigned(WmiProtocol.Call("LENOVO_GAMEZONE_DATA", "GetSmartFanMode", null, "Data")));
    private static Task<bool> Full() => Call(WmiProtocol.FullSpeed);
    private static async Task Check(uint mode, bool full)
    {
        if (await Mode() != mode || await Full() != full) { uncertain = true; throw new InvalidOperationException("Mode or Full Speed changed unexpectedly; possible interference."); }
    }
    private static async Task Sample(string stage, uint mode, bool full, List<uint> fans)
    {
        if (stopRequested) throw new OperationCanceledException("Operator requested stop.");
        await Check(mode, full);
        double temp;
        uint raw;
        try {
            temp = await Call(() => WmiProtocol.Temperature(WmiProtocol.Call("LENOVO_GAMEZONE_DATA", "GetCPUTemp", null, "Data")));
            raw = await Call(() => WmiProtocol.Unsigned(WmiProtocol.Feature(0x04030001)));
        } catch { if (owned) uncertain = true; throw; }
        Log(stage, new { mode, fullSpeed = full, temperatureC = temp, fanRawUncalibrated = raw });
        if (temp >= maximumTestTemperature || raw == 0 || raw > 65535) { if (owned) uncertain = true; throw new InvalidOperationException("Unsafe temperature or invalid fan sensor; stopping."); }
        fans.Add(raw);
    }
    private static async Task SetMode(uint mode)
    {
        if (!Modes.Contains(mode)) throw new InvalidOperationException("Unknown mode.");
        uncertain = true;
        await Call(() => { WmiProtocol.Call("LENOVO_GAMEZONE_DATA", "SetSmartFanMode", new Dictionary<string, uint> { ["Data"] = mode }, null); return 0; });
        if (await Mode() != mode) throw new InvalidOperationException("Mode write did not verify.");
        expectedMode = mode;
        uncertain = false;
    }
    private static async Task SetFull(bool value)
    {
        uncertain = true;
        await Call(() => { WmiProtocol.Call("LENOVO_OTHER_METHOD", "SetFeatureValue", new Dictionary<string, uint> { ["IDs"] = 0x04020000, ["value"] = value ? 1u : 0u }, null); return 0; });
        if (await Full() != value) throw new InvalidOperationException("Full Speed write did not verify.");
        owned = value;
        uncertain = false;
    }
    public static async Task<int> RunAsync()
    {
        faulted = false; uncertain = false; owned = false; expectedMode = null; stopRequested = false;
        var success = false;
        var eligibleToRestore = false;
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stopRequested = true; };
        Console.CancelKeyPress += handler;
        try
        {
            var options = LoadOptions();
            options.Validate();
            maximumTestTemperature = Math.Min(75, options.OnTemperature - 1);
            Log("temperatureLimit", new { options.OnTemperature, maximumTestTemperature });
            var discovery = await LenovoDiagnostics.CollectAsync();
            Log("identity", new { computerSystem = discovery.Items["computerSystem"], bios = discovery.Items["bios"] });
            Log("fanTable", "Not captured by this command; the wrapper may capture it.");
            await Call(() => { WmiProtocol.RequireSupportedDevice(); return 0; });
            originalMode = await Mode();
            if (!Modes.Contains(originalMode)) throw new InvalidOperationException("Initial mode is unknown; no writes permitted.");
            expectedMode = originalMode;
            if (await Full()) { Log("preserved", "Full Speed already on; no writes performed."); return 1; }
            eligibleToRestore = true;
            Log("initial", new { originalMode, fullSpeed = false, note = "Fan values are uncalibrated; mode changes can change stock power profile." });
            await Sample("initialSafety", originalMode, false, new List<uint>());
            foreach (var mode in Modes)
            {
                await Check(expectedMode.Value, false);
                if (stopRequested) throw new OperationCanceledException("Operator requested stop.");
                if (mode != expectedMode) await SetMode(mode);
                var baseline = new List<uint>(); var on = new List<uint>(); var off = new List<uint>();
                for (var i = 0; i < 3; i++) { await Sample("baseline", mode, false, baseline); await Delay(1000); }
                await Check(mode, false);
                if (stopRequested) throw new OperationCanceledException("Operator requested stop.");
                await SetFull(true);
                for (var i = 0; i < 7; i++) { await Delay(1000); await Sample("on", mode, true, on); }
                await Check(mode, true);
                if (stopRequested) throw new OperationCanceledException("Operator requested stop.");
                await SetFull(false);
                for (var i = 0; i < 7; i++) { await Delay(1000); await Sample("off", mode, false, off); }
                var baselineMedian = baseline.Order().ElementAt(1);
                var onMedian = on.Order().ElementAt(3);
                var offMedian = off.Order().ElementAt(3);
                Log("result", new { mode, baselineMedian, onMedian, offMedian, rawRise = (long)onMedian - baselineMedian, note = "Raw rise is uncalibrated and is not RPM; review signal and interference before claiming effectiveness." });
            }
            success = true;
        }
        catch (Exception ex) { Log("error", ex.Message); }
        finally
        {
            if (eligibleToRestore && !uncertain && !faulted && expectedMode is uint mode)
            {
                try
                {
                    await Check(mode, owned);
                    if (owned) await SetFull(false);
                    await Check(mode, false);
                    if (mode != originalMode) await SetMode(originalMode);
                    await Check(originalMode, false);
                    Log("restored", new { mode = originalMode, fullSpeed = false });
                    Log("fanTableAfter", "Not captured by this command; the wrapper may capture it.");
                }
                catch (Exception ex) { success = false; Log("restoreFailed", ex.Message); }
            }
            else if (eligibleToRestore && expectedMode.HasValue) { success = false; Log("uncertain", "No further writes attempted; inspect mode and Full Speed manually."); }
            Console.CancelKeyPress -= handler;
        }
        return success ? 0 : 1;
    }
}
