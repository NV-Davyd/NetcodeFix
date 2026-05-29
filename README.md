# NetcodeFix

KSL mod for tightening CarX remote-car sync in tandem rooms.

This version deliberately does not add custom collision arbitration. The
primary path keeps CarX's own multiplayer packets and physics, then improves
the places where the client sends, samples, and corrects remote cars.

## What it changes

- Optionally raises CarX's native realtime pose send rate from stock timing to
  a selected 30-60 Hz target.
- Fixes CarX's fractional interpolation path in `NetGameCircularInterpolator`.
- Preserves the real sync sample's extrapolation flag across CarX's debug
  future-position sample.
- Replaces remote-car correction with a configurable correction rate.
- Includes optional KSL shared pose packets for testing, kept off by default
  because KSL sync currently arrives at about 10 packets per second.
- Shows native send/receive diagnostics and shared-packet diagnostics for
  controlled lobby tests.

## Defaults

- NetcodeFix: on
- Native send-rate patch: on
- Native send rate: 50 Hz
- Fractional interpolation fix: on
- Shared pose packets: off
- Correction rate: 2.5

## Build

```powershell
dotnet build .\NetcodeFix.KSL.csproj -c Release
```

Output: `bin\Release\net48\NetcodeFix.dll`.

## Package

Export the Maykr key as `NetcodeFix_maykr.kmc`, then run:

```powershell
.\build_ksm.ps1
```

## KSL updater release

Register this as a KSL `PublicMod` named `NetcodeFix`, link the public GitHub
repository in KSL dev tools, and set the updater type to `Automatic`.

Create the release archive with:

```powershell
.\build_release.ps1 -KeyPath "D:\SteamLibrary\steamapps\common\CarX Drift Racing Online\kino\dev\NetcodeFix_maykr.kmc"
```

Upload the generated `build\release\NetcodeFix_<version>_win.zip` to a GitHub
release. The release tag must match KSL's required format, for example:

```text
v0.6.3
```
