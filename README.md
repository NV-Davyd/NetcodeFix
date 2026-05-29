# NetcodeFix

KSL mod for tightening CarX remote-car sync in tandem rooms.

This release keeps the mod intentionally simple for normal players. The core
fixes are always enabled and hidden; the only exposed control is remote-car
correction strength.

## What it changes

- Raises CarX's native realtime pose send rate to 60 Hz.
- Fixes CarX's fractional interpolation path in `NetGameCircularInterpolator`.
- Preserves the real sync sample's extrapolation flag across CarX's debug
  future-position sample.
- Replaces remote-car correction with a configurable correction rate.

## Defaults

- NetcodeFix: on
- Native send-rate patch: on
- Native send rate: 60 Hz
- Fractional interpolation fix: on
- Correction rate: 2.5
- UI controls: correction slider only

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
v1.0.0
```
