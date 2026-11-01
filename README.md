# Legion Go Smart Fan Control

A small Windows tray utility for the original Lenovo Legion Go (8APU1 / model 83E1). It automatically monitors CPU temperature and requests Lenovo's separate hardware Full Speed override when the configured threshold is reached, then releases an override it owns after cooling when safety checks permit. Full Speed is a separate hardware override, not a 100% point on the fan curve; the normal firmware fan curve remains untouched. Read-only diagnostics and a controlled cross-mode Full Speed test have run on model 83E1 / BIOS N3CN40WW. Raw fan readings rose while Full Speed was enabled in Quiet, Balanced, Performance, and Custom; see [docs/HARDWARE-TESTING.md](docs/HARDWARE-TESTING.md) for results and limits. Automatic temperature-driven control and live thermal stress remain untested. See [docs/HARDWARE-TESTING.md](docs/HARDWARE-TESTING.md) for the recorded results.

## Build

Install the .NET 10 SDK, then from the repository root:

```powershell
dotnet build LegionGoFanControl.slnx -c Release
dotnet run --project tests/LegionGoFanControl.Tests
```

The solution builds all five projects. The tests are a dependency-free executable using simulated hardware; the latest Release run built with zero warnings/errors and passed 11/11 tests. No hardware writes occur in this test suite.

## Run

Read-only diagnostics (no arguments also runs diagnostics):

```powershell
dotnet run --project src/LegionGoFanControl.Diagnostics -- diagnostics
```

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

The tray uses one automatic enabled/disabled state; there is no manual fan mode. Left-click the tray icon to toggle automatic control. The icon is teal/green when enabled and gray when disabled. Right-click opens one configuration menu with the enable toggle, On and Off temperature sliders, polling and cool-down sliders (values and units shown), a startup checkbox, Open Logs, and Exit. Slider changes apply after a committed/debounced update and validation; values are not written on every drag frame.

Settings are stored in a flat JSON file. The new `Enabled` setting defaults to true when absent; existing threshold, polling, and cool-down settings are preserved. The startup checkbox changes the actual startup task only when explicitly toggled; editing thermal settings does not modify startup configuration. `--settings` remains a compatibility alias for opening the right-click configuration menu in normal or preview mode. Open Logs opens the existing bounded logs.

Disabling stops recurring WMI polling. The app first releases a Full Speed override it owns only when its existing safety/telemetry checks allow release. An override owned by another controller, or one retained because the CPU is hot or telemetry is unsafe/unknown, may remain enabled; the UI does not promise the fan will be off.
## Portable package

Download a versioned Windows x64 ZIP from the repository's Releases page, verify its SHA-256 against `SHA256SUMS.txt`, and extract the whole archive. Run `Tray\LegionGoFanControl.Tray.exe` for the tray app or `Diagnostics\LegionGoFanControl.Diagnostics.exe diagnostics` for read-only diagnostics. Keep each executable with its accompanying files. The package contains the .NET runtime, so a separate runtime installation is not needed. These automated prereleases are development builds; automatic thermal control has not yet been validated under live thermal stress.

To build a package locally with the .NET 10 SDK:

```powershell
.\scripts\package-portable.ps1 -OutputDirectory .\artifacts\portable-candidate
```

The output directory must be new or empty. It contains the ZIP, checksum and staging files. To publish the apps without creating a ZIP, use `.\scripts\publish.ps1 -OutputDirectory .\artifacts\candidate` with a fresh output directory. Choose a different directory from any running installation. No installer or service is created.

The GitHub Actions workflow builds and tests on Windows, packages the apps and uploads the ZIP and checksum as a workflow artifact. Each push to `main` also creates a separately tagged prerelease tied to that commit; a manual run only builds an artifact. Release assets use versioned release URLs rather than a mutable latest asset. No signing certificate or winget package is currently assumed.
## Behavior and limits

Defaults are 80°C on, 70°C off, a 2-second poll, and a 10-second cool-down. Writes are restricted to model 83E1 / family Legion Go 8APU1; automatic control ignores thermal mode. Temperature sensor failures produce an error and do not count as cool. Other control software can change the flag; detected interference pauses writes.

On the tested device, reading Lenovo `root\WMI` classes and telemetry required an administrator token; non-elevated diagnostics returned access denied. Model and BIOS discovery succeeded without elevation. Run diagnostics and the tray app elevated on this device to access Lenovo telemetry. The controlled cross-mode Full Speed test passed on N3CN40WW; automatic thermal control remains untested.

On normal exit, the controller releases an override it owns only when its telemetry and state checks allow it. With stale/error telemetry it leaves Full Speed enabled conservatively. If the CPU was hot or its last temperature was unknown at suspend, it retains Full Speed through sleep, then relinquishes ownership; after resume an already-enabled flag is treated as external and may require manual inspection or release in Legion Space. A hung sensor permanently disables hardware operations for that process, including shutdown and mode changes, until restart. Forced termination/crash also bypasses cleanup. Do not run hardware-changing tests without the device operator's approval; see the docs for details.

This project is distributed under the [MIT License](LICENSE).
