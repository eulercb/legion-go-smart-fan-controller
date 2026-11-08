# Hardware testing checklist

Read-only hardware access and a controlled cross-mode Full Speed test were verified on model 83E1 / Legion Go 8APU1, BIOS N3CN40WW. The test showed a higher median raw fan reading while Full Speed was enabled in modes 1, 2, 3, and 255. Values are uncalibrated and are not RPM. The test does not validate other firmware versions, hot loads, or automatic thermal control.

## 1. Read-only discovery

From the repository root, run in PowerShell:

```powershell
dotnet run --project src/LegionGoFanControl.Diagnostics -- diagnostics
```

No arguments also runs the same diagnostics. It queries model and BIOS, enumerates Lenovo WMI classes/method signatures, reads both temperature routes, Full Speed, raw fan reading, and smart-fan mode. It does not write hardware settings. From the publish output, run `artifacts\win-x64\Diagnostics\LegionGoFanControl.Diagnostics.exe diagnostics` in an elevated PowerShell.

On the tested device, running without an administrator token allowed model/BIOS queries but returned access denied for every `root\WMI` query. Run PowerShell elevated for Lenovo diagnostics on this device. One elevated run reported model `83E1`, family `Legion Go 8APU1`, BIOS `N3CN40WW`, both temperature routes at 60°C, Full Speed off, raw fan value 4099, and smart-fan mode 3. These values are from one moment and are not expected constants. The saved sanitized sample is [diagnostics/legion-go-83e1-n3cn40ww.json](diagnostics/legion-go-83e1-n3cn40ww.json).

Confirm `compatibleDevice`, BIOS, class signatures, plausible Celsius readings, and Full Speed 0/1. Errors are captured per item and do not imply success. `fanSpeedRawUncalibrated` must not be interpreted as RPM. Record the output timestamp, power mode, and any errors; compare both temperature routes across idle/load before relying on them.

Read-only NVMe diagnostics are available with `dotnet run --project src/LegionGoFanControl.Diagnostics -- read-ssd`. An elevated native read at 18:51:28 on the tested device reported PhysicalDrive0 composite 73.9°C, warning 83.9°C, and critical 87.9°C. These are point-in-time observations.

## 2. Isolated Full Speed write test

Hardware writes require explicit operator authorization and an operator present to observe the device. Run commands from an elevated PowerShell at the repository root. The simple flag test is:

```powershell
dotnet run --project src/LegionGoFanControl.Diagnostics -- test-full-speed --confirm-hardware-write
```

The test preserves an already-on Full Speed flag without writing. Otherwise it enables the flag, verifies readback, disables it, and verifies restoration.

The explicit mode experiment writes Lenovo thermal modes and requires stopping the tray first. It was run only on original model 83E1 / 8APU1 BIOS N3CN40WW:

```powershell
dotnet run --project src/LegionGoFanControl.Diagnostics -- test-full-speed-modes --confirm-hardware-write
```

This test preserves the initial Full Speed state, tests modes Quiet (1), Balanced (2), Performance (3), and Custom (255), and restores the initial mode. If it times out, temperature becomes unsafe or unknown, mode becomes unknown, or state cannot be verified, abort without further writes. Hardware may retain the last test mode or Full Speed setting and need manual inspection. Do not generalize this mode experiment to other firmware.
## 3. Tray preview without hardware writes

```powershell
dotnet run --project src/LegionGoFanControl.Tray -- --preview
dotnet run --project src/LegionGoFanControl.Tray -- --smoke-test
```

Both flags select an in-memory fake hardware backend; they do not invoke Lenovo WMI methods or write fan state. Preview remains open until exited. Smoke test starts the tray and exits after about 1.5 seconds. The app can create local settings/log files under `%LOCALAPPDATA%\LegionGoFanControl`.

## 4. Tray operation

Left-click the tray icon to toggle automatic control. Teal/green indicates enabled; gray indicates disabled. Right-click opens the single configuration menu: enable toggle, CPU On/Off temperature sliders, SSD On/Off temperature sliders, polling and cool-down sliders with values/units, startup checkbox, Open Logs, and Exit. Slider values apply on committed/debounced update after validation, not on every drag frame. The `--settings` compatibility option opens this same right-click menu in normal and preview runs.

Settings are persisted as flat JSON. Missing `Enabled` defaults to true, while prior threshold and timing values remain intact; missing SSD thresholds default to 70°C on and 60°C off. The startup checkbox changes the startup task only when explicitly toggled; editing thermal settings alone does not write startup configuration. Logs remain bounded.

Disabling stops recurring WMI polling after attempting to release only a Full Speed override owned by this app and allowed by its safety checks. An override retained due to hot temperature or unsafe/unknown telemetry may remain enabled. Disabled state does not promise the fan is off. Automatic control ignores thermal mode. The tested hardware results above remain limited to BIOS N3CN40WW; automatic operation under live thermal stress remains unvalidated.
## Access, ownership, and recovery

For the tested Legion Go, administrator elevation is required to read Lenovo `root\WMI` classes and telemetry; model/BIOS CIM queries succeeded without elevation. The admin requirement is confirmed only for this device/environment. The controlled cross-mode test passed on N3CN40WW. SSD-driven automatic operation under live thermal stress remains unvalidated.

Enabled automatic control manages Full Speed even if it is already on at startup or observed on later. A hot CPU or SSD can enable Full Speed. It releases it only after both fresh, valid readings stay at or below their respective off thresholds for the same continuous configured cooldown. Missing SSD telemetry prevents release, while a known hot CPU can still enable Full Speed. On normal exit, hot temperature or stale/error telemetry causes it to leave a managed Full Speed override enabled for safety. A cool CPU and SSD with fresh telemetry and a clearly owned override is released on suspend. If either last reading is hot or unknown, the controller retains Full Speed during sleep but relinquishes ownership; after resume automatic control adopts an already-on flag and applies the cooldown policy. A timed-out SSD query disables further SSD reads for that process while CPU control continues; missing SSD telemetry prevents release. A hung Lenovo WMI operation stops further hardware operations in that process, including shutdown and mode changes; restart the app to retry. Failed writes, mismatched readback, detected outside changes, forced termination, provider hangs, firmware overrides, and external-controller races can leave the actual state uncertain. Firmware thermal protections remain in place, but the app does not independently validate their behavior.

## Cross-mode evidence (N3CN40WW)

The test began and ended in Custom (255) with Full Speed off. It tested modes 1/Quiet, 2/Balanced, 3/Performance, and 255/Custom, with three baseline and seven enabled/disabled raw fan samples per mode. Median baseline → enabled → disabled values: Quiet 4045 → 8729 → 8261; Balanced 7673 → 8984 → 8422; Performance 7812 → 9059 → 8455; Custom 7840 → 9098 → 8489. The fan-table fingerprint was identical before and after; startup task/XML remained enabled and unchanged; the tray was restarted. Values are raw, uncalibrated readings, not RPM. Disabled readings show coast-down/carryover and had not fully settled after seven seconds. This cool/low-load result does not establish exact fan speed or behavior on other firmware/hot loads. The production app does not change modes or power limits.

## SSD policy and limits

The default CPU thresholds are 80°C on / 70°C off; SSD thresholds are 70°C on / 60°C off. SSD thresholds are a conservative configurable policy, not a manufacturer hard limit. The [WD PC SN740 product brief](https://documents.sandisk.com/content/dam/asset-library/en_us/assets/public/western-digital/product/internal-drives/pc-sn740-nvme-ssd/product-brief-pc-sn740-nvme-ssd.pdf) specifies 0–85°C operating temperature as drive-reported temperature (footnote 8). The controller uses the NVMe composite temperature and does not use additional internal NVMe temperatures. Full Speed may improve internal cooling, but no physical A/B test here has established a cooling benefit for the SSD, and SSD-driven automatic behavior under live thermal stress remains unvalidated.
