using System.Text.Json;
using LegionGoFanControl.Hardware;
using LegionGoFanControl.Core;

if (args.Length == 0 || (args.Length == 1 && args[0] == "diagnostics"))
{
    try { Console.WriteLine(JsonSerializer.Serialize(await LenovoDiagnostics.CollectAsync(), new JsonSerializerOptions { WriteIndented = true })); return 0; }
    catch (Exception ex) { Console.Error.WriteLine($"Diagnostics failed: {ex.Message}"); return 1; }
}
if (args.Length == 2 && args[0] == "test-full-speed" && args[1] == "--confirm-hardware-write")
{
    var hardware = new LenovoFanHardware();
    bool enabledByTest = false;
    try
    {
        var initial = await hardware.ReadFullSpeedAsync(CancellationToken.None);
        Console.WriteLine($"Initial Full Speed: {initial}");
        if (initial) { Console.WriteLine("Already enabled; preserving existing state. No write performed."); return 0; }
        // If the setter times out or throws, ownership and outcome are uncertain; do not blindly undo it.
        var result = await hardware.SetFullSpeedAsync(true, CancellationToken.None);
        if (result == FanWriteResult.AlreadyInRequestedState) { Console.WriteLine("State changed externally before the write; preserving it."); return 0; }
        enabledByTest = true;
        Console.WriteLine("Enabled and verified. Restoring original state.");
        await hardware.SetFullSpeedAsync(false, CancellationToken.None);
        enabledByTest = false;
        Console.WriteLine("Restored and verified.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Hardware test failed: {ex.Message}. Full Speed state may need manual inspection in Legion Space.");
        if (enabledByTest) Console.Error.WriteLine("This test enabled Full Speed, but restoration was not verified.");
        return 1;
    }
}
Console.Error.WriteLine("Usage: diagnostics | test-full-speed --confirm-hardware-write");
return 2;

