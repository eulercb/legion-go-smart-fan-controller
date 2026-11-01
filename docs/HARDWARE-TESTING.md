# Hardware testing checklist

Read-only hardware access and one approved manual Full Speed write/readback/restore test were verified on model 83E1 / Legion Go 8APU1, BIOS N3CN40WW. The write test passed in Custom mode 255 with an administrator token. Automatic temperature-driven operation, live thermal stress, sleep/resume, and other firmware versions remain untested. Use an original Legion Go and keep Legion Space available for observation.

## 1. Read-only discovery

From the repository root, run in PowerShell:

```powershell
dotnet run --project src/LegionGoFanControl.Diagnostics -- diagnostics
```

No arguments also runs the same diagnostics. It queries model and BIOS, enumerates Lenovo WMI classes/method signatures, reads both temperature routes, Full Speed, raw fan reading, and smart-fan mode. It does not write hardware settings. From the publish output, run `artifacts\win-x64\Diagnostics\LegionGoFanControl.Diagnostics.exe diagnostics` in an elevated PowerShell.

On the tested device, running without an administrator token allowed model/BIOS queries but returned access denied for every `root\WMI` query. Run PowerShell elevated for Lenovo diagnostics on this device. One elevated run reported model `83E1`, family `Legion Go 8APU1`, BIOS `N3CN40WW`, both temperature routes at 60°C, Full Speed off, raw fan value 4099, and smart-fan mode 3. These values are from one moment and are not expected constants. The saved sanitized sample is [diagnostics/legion-go-83e1-n3cn40ww.json](diagnostics/legion-go-83e1-n3cn40ww.json).

Confirm `compatibleDevice`, BIOS, class signatures, plausible Celsius readings, and Full Speed 0/1. Errors are captured per item and do not imply success. `fanSpeedRawUncalibrated` must not be interpreted as RPM. Record the output timestamp, power mode, and any errors; compare both temperature routes across idle/load before relying on them.

## 2. Isolated Full Speed write test

This test changes hardware briefly. Run it only after the device operator explicitly approves the test and is present to observe it. On the tested device the current smart-fan mode was 3, but the write path requires mode 255 (Custom). Select Custom in Legion Space first and rerun diagnostics to confirm the mode is 255. From an elevated PowerShell at the repository root:

```powershell
dotnet run --project src/LegionGoFanControl.Diagnostics -- test-full-speed --confirm-hardware-write
```

The test reads the initial state. If Full Speed is already on, it preserves it and performs no write. Otherwise it requests on, requires readback to confirm, then requests off and verifies restoration. A success prints `Enabled and verified` and `Restored and verified`. The approved run on the reference device began with Full Speed off, printed both success lines, and exited 0; the subsequent read-only check confirmed Full Speed off. Before/after values are recorded in [diagnostics/full-speed-test-83e1-n3cn40ww.json](diagnostics/full-speed-test-83e1-n3cn40ww.json). This result does not establish automatic control or long-term operation. If a future run errors or readback mismatches, the outcome can be uncertain; inspect Legion Space and manually restore firmware behavior as appropriate. Do not repeatedly retry an uncertain write without checking state.

## 3. Tray preview without hardware writes

```powershell
dotnet run --project src/LegionGoFanControl.Tray -- --preview
dotnet run --project src/LegionGoFanControl.Tray -- --smoke-test
```

Both flags select an in-memory fake hardware backend; they do not invoke Lenovo WMI methods or write fan state. Preview remains open until exited. Smoke test starts the tray and exits after about 1.5 seconds. The app can create local settings/log files under `%LOCALAPPDATA%\LegionGoFanControl`.

## 4. Automatic operation

Only after diagnostics and the isolated test have been reviewed, an operator may launch the app elevated. The UI starts in Automatic mode and can write the Full Speed flag when temperature meets the configured threshold. Default on/off thresholds are 80°C/70°C, polling every 2 seconds, with a 30-second release cool-down. Use Legion Space Custom mode (mode 255); an earlier read-only sample reported mode 3, which does not pass the current write gate. Monitor temperature and reported state; automatic thermal operation has not yet been validated under load.

## Access, ownership, and recovery

For the tested Legion Go, administrator elevation is required to read Lenovo `root\WMI` classes and telemetry; model/BIOS CIM queries succeeded without elevation. The admin requirement is confirmed only for this device/environment. One manual Full Speed write/readback/restore test passed; automatic thermal operation and sustained behavior remain untested.

The controller avoids taking ownership when Full Speed is already on. On normal exit in Automatic mode, stale/error telemetry causes it to leave an owned Full Speed override enabled for safety. A cool CPU with fresh telemetry and a clearly owned override is released on suspend. If the last reading is hot or unknown, the controller retains Full Speed during sleep but relinquishes ownership; after resume an already-on flag is treated as external and is not released automatically. Inspect Legion Space and release it manually if needed. A hung sensor stops all further hardware operations in that process, including shutdown and mode changes; restart the app to retry. Failed writes, mismatched readback, detected outside changes, forced termination, provider hangs, firmware overrides, and external-controller races can leave the actual state uncertain. Firmware thermal protections remain in place, but the app does not independently validate their behavior.
