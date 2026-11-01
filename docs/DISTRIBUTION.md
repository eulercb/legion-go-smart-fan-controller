# Windows distribution: signing and WinGet

This project currently packages a self-contained Windows x64 portable ZIP. The Actions workflow creates commit-specific prereleases with versioned tags, ZIPs, and SHA-256 checksums. These are development builds. No signing certificate is configured, and no WinGet submission has been made.

For public adoption, keep the automated prereleases for commit review and validation, then offer WinGet on chosen stable releases rather than indexing every CI build. Stable assets should use version-specific release URLs and checksums; avoid mutable `latest` URLs in manifests.

## Code signing

Authenticode signing identifies the publisher and lets Windows detect changes to the signed binary. It does not grant administrator rights or suppress UAC consent. UAC elevation is controlled by the executable manifest and the user's consent. Lenovo WMI hardware access may require running this app elevated on the tested device; that is separate from signing.

Signing does not promise a warning-free first run. SmartScreen considers publisher and file reputation, and a newly signed app can still be reported as unrecognized. Microsoft says EV certificates no longer bypass first-download SmartScreen warnings. Smart App Control is a separate Windows 11 feature and can block unsigned apps without positive reputation.

Options:

- **Microsoft Artifact Signing (formerly Trusted Signing):** Public Trust individual identity validation is currently available only to individuals in the US and Canada; organizations have a wider country list. Check current eligibility against the developer's actual location and identity before planning around it. A paid Azure subscription is required; free, trial, and sponsored subscriptions are unsupported. Basic is currently $9.99 per account per month for 5,000 signatures, then $0.005 per additional signature. Microsoft recommends allowing a few business days for identity validation.
- **Traditional OV certificate:** Microsoft lists this as an alternative available worldwide, with indicative pricing of $150–$300/year. Verify current provider pricing and issuance requirements. EV is substantially more expensive and offers no SmartScreen first-download bypass.
- **Unsigned releases:** No certificate cost, but the publisher will appear unverified and users may see SmartScreen or Smart App Control friction. Do not claim unsigned releases are warning-free.

Effort estimate: after certificate approval and setup, adding signing to the release workflow should be a small integration, likely a few hours. Certificate approval time is separate and can take several business days. These are estimates, not service guarantees. Sign the final EXE before building the ZIP and computing its SHA-256; any later binary modification invalidates its signature. Keep the signing identity consistent across stable releases to build publisher reputation.

## WinGet portable ZIP

WinGet's manifest schema supports ZIP archives with `NestedInstallerType: portable`, `NestedInstallerFiles`, and optional command aliases. The alias is for a declared executable command; the tray GUI does not need one. The schema and existing manifests demonstrate ZIP plus nested portable declarations, but this project's complete self-contained .NET publish directory has not yet been validated through WinGet. Do not advertise the package as install-tested until that check passes.

Before submitting a manifest, publish a stable versioned ZIP asset and checksum, then test the manifest on a clean Windows machine. Verify extraction of all bundled .NET runtime and app files, first launch, upgrade from the prior version, and uninstall/cleanup. Run `winget validate --manifest <path>` and test the local manifest install; use Windows Sandbox when practical. The community repository runs automated manifest/installer validation and requires maintainer or moderator review. Passing local checks does not guarantee PR acceptance.

WinGet's portable package handling does not implement this app's own elevated startup task. Keep any launch-at-login or elevated startup behavior explicit and user-controlled; the current package creates no installer or service.

Estimated effort after stable release assets exist: roughly 2–4 hours to draft the three manifest files and validate install/update/uninstall, plus 1–3 hours for likely PR revisions. These estimates exclude repository review time and assume no package behavior changes are needed. No manifest or submission has been created as part of this document.

## Primary sources

- Microsoft, [SmartScreen reputation for Windows app developers](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation)
- Microsoft, [Code-signing options for Windows app developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)
- Microsoft, [Artifact Signing quickstart and geography](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart), [pricing tiers](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-change-sku), and [FAQ](https://learn.microsoft.com/en-us/azure/artifact-signing/faq)
- Microsoft, [Smart App Control code-signing guidance](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control) and [running with administrator privileges](https://learn.microsoft.com/en-us/windows/win32/secbp/running-with-administrator-privileges)
- Microsoft WinGet community repository, [installer manifest schema](https://github.com/microsoft/winget-pkgs/blob/master/doc/manifest/schema/1.9.0/installer.md), [submission and testing overview](https://github.com/microsoft/winget-pkgs/blob/master/doc/README.md), and [authoring guide](https://github.com/microsoft/winget-pkgs/blob/master/doc/Authoring.md)
