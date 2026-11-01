using LegionGoFanControl.Hardware;
CrossModeExperiment.Delay = _ => Task.CompletedTask;
CrossModeExperiment.LoadOptions = () => new LegionGoFanControl.Core.ControllerOptions();
async Task Run(string name, Action setup, Func<bool> verify) {
  WmiProtocol.Reset(); setup(); var result = await CrossModeExperiment.RunAsync();
  if (!verify()) throw new Exception(name + " failed; exit " + result);
  Console.Error.WriteLine(name + " passed");
}
await Run("normal", () => {}, () => WmiProtocol.Mode == 3 && !WmiProtocol.On && WmiProtocol.Writes > 0);
await Run("initial-on", () => WmiProtocol.On = true, () => WmiProtocol.On && WmiProtocol.Writes == 0);
await Run("hot-while-on", () => WmiProtocol.HotWhileOn = true, () => WmiProtocol.On && WmiProtocol.Mode == 1);
await Run("mode-mismatch", () => WmiProtocol.Mismatch = true, () => WmiProtocol.Writes == 1 && !WmiProtocol.On);
await Run("timeout", () => WmiProtocol.Timeout = true, () => WmiProtocol.Writes == 1 && !WmiProtocol.On);
await Run("cancel", () => WmiProtocol.Cancel = true, () => WmiProtocol.Mode == 3 && !WmiProtocol.On);
await Run("cancel-while-owned", () => WmiProtocol.CancelWhileOn = true, () => WmiProtocol.Mode == 3 && !WmiProtocol.On && WmiProtocol.Writes >= 4);
await Run("sensor-failure-while-owned", () => WmiProtocol.BadSensorWhileOn = true, () => WmiProtocol.On && WmiProtocol.Mode == 1);
namespace LegionGoFanControl.Hardware {
 public class DiagnosticReport { public Dictionary<string, object> Items = new() { ["computerSystem"] = "fake", ["bios"] = "fake" }; }
 public static class LenovoDiagnostics { public static Task<DiagnosticReport> CollectAsync() => Task.FromResult(new DiagnosticReport()); }
 internal static class WmiProtocol {
  internal static uint Mode; internal static bool On, HotWhileOn, Mismatch, Timeout, Cancel, CancelWhileOn, BadSensorWhileOn; internal static int Writes;
  internal static void Reset() { Mode = 3; On = HotWhileOn = Mismatch = Timeout = Cancel = CancelWhileOn = BadSensorWhileOn = false; Writes = 0; }
  internal static void RequireSupportedDevice() { }
  internal static uint Unsigned(object? x) => Convert.ToUInt32(x);
  internal static double Temperature(object? x) => Convert.ToDouble(x);
  internal static bool FullSpeed() => On;
  internal static object Feature(uint id) => BadSensorWhileOn && On ? throw new InvalidOperationException("fake sensor failure") : On ? 5000u : 3000u;
  internal static object? Call(string c,string m,Dictionary<string,uint>? inputs,string? output) {
   if (m == "GetSmartFanMode") return Mode;
   if (m == "GetCPUTemp") { if (Cancel || (CancelWhileOn && On)) { Cancel = CancelWhileOn = false; CrossModeExperiment.RequestStop(); } return HotWhileOn && On ? 90u : 50u; }
   Writes++;
   if (Timeout) throw new TimeoutException("fake hung call");
   if (m == "SetSmartFanMode") { if (!Mismatch) Mode = inputs!["Data"]; }
   else if (m == "SetFeatureValue") On = inputs!["value"] == 1;
   return null;
  }
 }
}
