# NetcodeFix

KSL mod for tightening CarX remote-car sync in tandem rooms.

This release keeps the mod automatic for normal players. The core fixes and
adaptive correction are always enabled; manual tuning and the synchronization
overlay remain optional debug tools.

## What it changes

- Raises CarX's native realtime pose send rate to 60 Hz.
- Fixes CarX's fractional interpolation path in `NetGameCircularInterpolator`.
- Uses bounded velocity-aware Hermite interpolation and spherical rotation interpolation.
- Uses up to four recent frames to filter motion trends and predict short steering transitions.
- Continues stale remote cars from 300 ms to 1 second with progressively decaying acceleration instead of freezing them.
- Preserves the real sync sample's extrapolation flag across CarX's debug
  future-position sample.
- Replaces remote-car correction with adaptive position, rotation, velocity,
  and steering reconciliation.
- Immediately repositions remote cars when a real sync discontinuity reaches 30 metres.
- Adds an optional in-world debug overlay for synchronized targets and pending corrections.

## Defaults

- NetcodeFix: on
- Native send-rate patch: on
- Native send rate: 60 Hz
- Fractional interpolation fix: on
- Automatic base correction rate: 1.0
- Manual correction tuning: off
- Correction range: 0.8-2.5
- Main UI: debug button and players using this mod
- Debug UI: optional expert correction slider and tuning notes
- Debug UI: optional blue target and green remote-physics boxes

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
v1.1.0
```
