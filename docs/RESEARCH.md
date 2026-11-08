# Hardware and protocol research

Research date: 2026-11-01. Evidence is separated below into source review, device read-only observations, and an approved hardware write test. The target is model **83E1**, family **Legion Go 8APU1**, BIOS **N3CN40WW**. Read-only CIM identified the device; an administrator-token run read Lenovo WMI metadata and telemetry. An operator-approved controlled cross-mode test recorded higher raw fan medians while Full Speed was enabled in Quiet, Balanced, Performance, and Custom. The new SSD-driven automatic policy has not been validated under live thermal stress.

## Device read-only observation

At 2026-11-01 20:37 UTC, elevated diagnostics reported both CPU temperature routes as **60°C**, Full Speed **off**, raw fan feature **4099**, and smart-fan mode **3**. These are point-in-time observations and may vary with workload, power mode, and time; 4099 is not calibrated RPM. Model/BIOS and the complete sanitized hardware-only report are in [diagnostics/legion-go-83e1-n3cn40ww.json](diagnostics/legion-go-83e1-n3cn40ww.json).

The non-elevated run read model and BIOS successfully but every `root\\WMI` query returned access denied. On this tested device, Lenovo telemetry access therefore requires an administrator token. This is an empirical result for this device and environment, not a general Windows WMI rule.

## Approved write test

The controlled test used BIOS N3CN40WW, began in Custom (255) with Full Speed off, and exercised modes 1, 2, 3, and 255 with three baseline samples and seven enabled/disabled samples per mode. Median raw readings baseline → on → off were 4045→8729→8261, 7673→8984→8422, 7812→9059→8455, and 7840→9098→8489. Flag readback tracked writes; table fingerprint and startup task/XML were unchanged; final mode and Full Speed restored to 255/off. Values are uncalibrated, not RPM; off samples show carryover after seven seconds. This cool/low-load test does not validate hot loads or other firmware. See [diagnostics/cross-mode-N3CN40WW.json](diagnostics/cross-mode-N3CN40WW.json).

## Findings

- The Full Speed override is separate from the fan table. Source implementations map it to `LENOVO_OTHER_METHOD.GetFeatureValue` / `SetFeatureValue`, feature ID `0x04020000`, and values 0/1. The setter is a void/out method; invocation alone does not establish success. This project reads the value back and requires a match.
- The hardware reports smart-fan mode through `LENOVO_GAMEZONE_DATA.GetSmartFanMode`. The production app ignores thermal mode and does not change modes or power limits. The manual mode experiment is a separate, explicit diagnostic operation. The application does not edit or raise the fan curve.
- Temperature sources found in upstream implementations are `LENOVO_GAMEZONE_DATA.GetCPUTemp` (`Data`) and `LENOVO_OTHER_METHOD.GetFeatureValue(0x05040000)` (`Value`). This implementation tries the first and falls back to the second; values are treated as Celsius and constrained to 1–110°C. The diagnostic command reports both independently.
- Feature `0x04030001` is exposed as a **raw, uncalibrated fan reading**, not reliable RPM. Although some projects label it RPM, another primary implementation cautions against presenting it as calibrated speed.
- Firmware mode, Legion Space, or other utilities may override or conflict with this feature. On BIOS N3CN40WW the test showed higher raw fan readings in all four tested modes under cool/low-load conditions. Other firmware and hot-load behavior remain unknown. The controller detects some state changes and pauses rather than repeatedly fighting them.

## Source review and licensing

The protocol identifiers and behavior were reviewed in [Legion Go Cooling Tray's implementation](https://github.com/OXOOOOX/legion-go-cooling-tray/blob/main/src/CoolingEngine.cs), [README](https://github.com/OXOOOOX/legion-go-cooling-tray/blob/main/README.md), and [verification notes](https://github.com/OXOOOOX/legion-go-cooling-tray/blob/main/docs/VERIFICATION.md); [Handheld Companion's Legion Go source](https://github.com/Valkirie/HandheldCompanion/blob/main/HandheldCompanion/Devices/Lenovo/LegionGo.cs) and [issue 1118](https://github.com/Valkirie/HandheldCompanion/issues/1118); [GoTweaks WMI service](https://github.com/corando98/GoTweaks/blob/release/v0.3.98.0/XboxGamingBarHelper/Devices/Libraries/Legion/LenovoWMIService.cs) and [feature mapping](https://github.com/corando98/GoTweaks/blob/release/v0.3.98.0/XboxGamingBarHelper/Devices/Libraries/Legion/LegionGoLibrary.cs); and the [LLG_Dev_scripts WMI map](https://github.com/corando98/LLG_Dev_scripts/blob/main/wmi_interface.md) and [hwinfo Legion Go notes](https://github.com/hhd-dev/hwinfo/blob/master/devices/legion_go/readme.md).

This repository independently implements the protocol from these documented identifiers and signatures; it does not copy upstream source. Upstream projects have different licenses and terms. Review each project's license before reusing its code; this research does not assert or select a license for this project.

## Remaining validation

Manual Full Speed activation, readback, and restoration passed once in the approved test above. Cross-mode evidence applies only to N3CN40WW and cool/low-load observations; automatic thermal control and other BIOS versions remain unvalidated. SSD-driven automatic control under live thermal stress remains unvalidated. Do not infer long-term behavior from one successful operation. Sensor freshness under load, coexistence with other utilities, suspend/resume behavior, and behavior across BIOS/Legion Space versions remain open.
