# Legion Go Smart Fan Control

A small Windows tray utility for the original Lenovo Legion Go (8APU1 / model 83E1). It automatically monitors CPU and SSD composite temperature and requests Lenovo's separate hardware Full Speed override when the configured threshold is reached, then releases it after cooling when safety checks permit. Full Speed is a separate hardware override, not a 100% point on the fan curve; the normal firmware fan curve remains untouched. Read-only diagnostics and a controlled cross-mode Full Speed test have run on model 83E1 / BIOS N3CN40WW. Raw fan readings rose while Full Speed was enabled in Quiet, Balanced, Performance, and Custom; see [docs/HARDWARE-TESTING.md](docs/HARDWARE-TESTING.md) for results and limits. The new SSD-driven automatic policy has not been validated under live thermal stress. See [docs/HARDWARE-TESTING.md](docs/HARDWARE-TESTING.md) for the recorded results.

## Build

Install the .NET 10 SDK, then from the repository root:

```powershell
dotnet build LegionGoFanControl.slnx -c Release
dotnet run --project tests/LegionGoFanControl.Tests
```

The solution builds all five projects. The tests are a dependency-free executable using simulated hardware; the latest Release run built with zero warnings/errors and passed 17 test groups and 8 isolated experiment cases. No hardware writes occur in this test suite.

## Run

Read-only diagnostics (no arguments also runs diagnostics):

```powershell
dotnet run --project src/LegionGoFanControl.Diagnostics -- diagnostics
```

For read-only NVMe composite temperature diagnostics, run `dotnet run --project src/LegionGoFanControl.Diagnostics -- read-ssd`. An elevated native read at 18:51:28 on the tested device reported PhysicalDrive0 composite 73.9°C, warning 83.9°C, and critical 87.9°C; these are point-in-time observations.

The tray app restores its saved enabled state, defaulting to enabled, and may change Full Speed when its threshold is met. Launch it with:

```powershell
dotnet run --project src/LegionGoFanControl.Tray
```

For UI preview or smoke test without hardware calls or fan writes:

```powershell
dotnet run --project src/LegionGoFanControl.Tray -- --preview
dotnet run --project src/LegionGoFanControl.Tray -- --smoke-test
```

These preview options use simulated temperature/fan state. The app may still create its settings and log directory under `%LOCALAPPDATA%\LegionGoFanControl`.

See [docs/RESEARCH.md](docs/RESEARCH.md) for source findings and limitations, and [docs/HARDWARE-TESTING.md](docs/HARDWARE-TESTING.md) for operator-run checks. Hardware writes require explicit operator consent; the diagnostic command is read-only. The isolated manual write test requires the exact `--confirm-hardware-write` flag.

## Tray controls

The tray uses one automatic enabled/disabled state; there is no manual fan mode. Left-click the tray icon to toggle automatic control. The icon is teal/green when enabled and gray when disabled. Right-click opens one configuration menu with the enable toggle, CPU On and Off temperature sliders, SSD On and Off temperature sliders, polling and cool-down sliders (values and units shown), a startup checkbox, Open Logs, and Exit. Slider changes apply after a committed/debounced update and validation; values are not written on every drag frame.

Settings are stored in a flat JSON file. The new `Enabled` setting defaults to true when absent; existing threshold, polling, and cool-down settings are preserved, and missing SSD thresholds default to 70°C on and 60°C off. The startup checkbox changes the actual startup task only when explicitly toggled; editing thermal settings does not modify startup configuration. `--settings` remains a compatibility alias for opening the right-click configuration menu in normal or preview mode. Open Logs opens the existing bounded logs.

When enabled, automatic control manages Full Speed even if it was already on at startup or enabled by another controller. Either a hot CPU or a hot SSD can enable Full Speed. It releases Full Speed after both readings are fresh and valid and remain at or below their respective off thresholds for the same continuous cooldown. Missing or invalid SSD telemetry prevents release but does not stop a known hot CPU from enabling Full Speed. Disabling stops recurring polling and releases a managed override only when both readings are fresh and cool. Full Speed may remain on when hot or telemetry is unsafe or unknown.
## Portable package

Download a versioned Windows x64 ZIP from the repository's Releases page, verify its SHA-256 against `SHA256SUMS.txt`, and extract the whole archive. Run `Tray\LegionGoFanControl.Tray.exe` for the tray app or `Diagnostics\LegionGoFanControl.Diagnostics.exe diagnostics` for read-only diagnostics. Keep each executable with its accompanying files. The package contains the .NET runtime, so a separate runtime installation is not needed. These automated prereleases are development builds; SSD-driven automatic control has not yet been validated under live thermal stress.

To build a package locally with the .NET 10 SDK:

```powershell
.\scripts\package-portable.ps1 -OutputDirectory .\artifacts\portable-candidate
```

The output directory must be new or empty. It contains the ZIP, checksum and staging files. To publish the apps without creating a ZIP, use `.\scripts\publish.ps1 -OutputDirectory .\artifacts\candidate` with a fresh output directory. Choose a different directory from any running installation. No installer or service is created.

The GitHub Actions workflow builds and tests on Windows, packages the apps and uploads the ZIP and checksum as a workflow artifact. Each push to `main` also creates a separately tagged prerelease tied to that commit; a manual run only builds an artifact. Release assets use versioned release URLs rather than a mutable latest asset. No signing certificate or winget package is currently assumed.
## Behavior and limits

CPU defaults are 80°C on and 70°C off; SSD defaults are 70°C on and 60°C off, with a 2-second poll and 30-second cool-down. The SSD thresholds are a conservative configurable policy, not a manufacturer hard limit. The [WD PC SN740 product brief](https://documents.sandisk.com/content/dam/asset-library/en_us/assets/public/western-digital/product/internal-drives/pc-sn740-nvme-ssd/product-brief-pc-sn740-nvme-ssd.pdf) specifies a 0–85°C operating range as drive-reported temperature (footnote 8). The SSD reading is the NVMe composite temperature; additional internal NVMe temperature sensors are not used. Full Speed may improve internal cooling, but no physical A/B test here has established an SSD cooling benefit. Writes are restricted to model 83E1 / family Legion Go 8APU1; automatic control ignores thermal mode. Temperature sensor failures produce an error and do not count as cool. Other control software can change the flag; detected interference pauses writes.

On the tested device, reading Lenovo `root\WMI` classes and telemetry required an administrator token; non-elevated diagnostics returned access denied. Model and BIOS discovery succeeded without elevation. Run diagnostics and the tray app elevated on this device to access Lenovo telemetry. The controlled cross-mode Full Speed test passed on N3CN40WW; SSD-driven automatic control remains unvalidated under live thermal stress.

On normal exit, the controller releases an override it owns only when its telemetry and state checks allow it. With stale/error telemetry it leaves Full Speed enabled conservatively. If either sensor was hot or its last temperature was unknown at suspend, it retains Full Speed through sleep, then relinquishes ownership; after resume automatic control adopts an already-enabled flag and applies its cooldown policy. A timed-out SSD query disables further SSD reads for that process while CPU control continues; missing SSD telemetry prevents release. A hung Lenovo WMI operation disables further hardware operations for that process, including shutdown and mode changes, until restart. Forced termination/crash also bypasses cleanup. Do not run hardware-changing tests without the device operator's approval; see the docs for details.

This project is distributed under the [MIT License](LICENSE).
