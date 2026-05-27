# NetcodeFix

A KSL mod that **patches CarX Drift Racing Online's multiplayer netcode** for tighter close-quarters racing — tandem drifts, door-to-door passes, busy rooms.

Not a server-side fix (we don't control SmartFox). Client-side Harmony patches that operate at three tiers:

- **Tier 1 — universal** (works against vanilla peers). Local-only mitigations: adaptive smoothing, smart snap threshold, sanity filter for impossible packet deltas.
- **Tier 2 — modded room** (activates when every peer also runs NetcodeFix). Input-history broadcast over `Kino.Sync`, higher local upload rate. *Status: pending.*
- **Tier 3 — modded room with arbitration** (the iRacing-style trick at the peer-coordination level). When two clients disagree about a contact, they exchange assertion packets and apply a deterministic, identical impulse on both sides. *Status: pending.*

A built-in **peer-presence handshake** (à la NoDynCollisions) advertises capability flags between modded clients so Tier 2/3 light up automatically when the room qualifies and drop back to Tier 1 the moment a vanilla player joins.

## Tier 1 in detail

### Adaptive smoothing + smart snap
CarX's vanilla `PosSyncInfo.Update` smooths remote position error at 1 m/s forward / 2 m/s side (rates scale with error magnitude) and snap-resets only above 6m total error. In practice this means a 3m error takes ~1.5s to bleed off — visibly trailing the truth — and a 5m lag spike slow-oozes for several seconds before tripping the snap. NetcodeFix:
- Bumps the smoothing rate multiplier (default 2×, configurable).
- Lowers the snap threshold (default 3m, configurable). Above the threshold, apply the entire residual in one frame — looks like a small discrete jump rather than a slow ooze.
- Rotation smoothing scales with the same multiplier.

### Sanity filter
Vanilla code feeds every received packet into the interpolator buffer unchecked. A corrupted, mis-timestamped, or massively-delayed packet lands at an impossible position, the interpolator happily snaps to it, then the smoothing system spends seconds dragging the car back. We reject any frame whose implied speed (Δpos / Δt vs. previous buffer entry) exceeds a sane threshold (default 80 m/s ≈ 288 km/h). Generation changes (respawn) are excluded.

## Build

```sh
dotnet build -c Release
```

Output: `bin/Release/net48/NetcodeFix.dll`.

To pack as `.ksm` (required for KSL to load the mod): use `maykr.exe` from the KSL SDK with your developer key — see [KSL docs](https://github.com/trbflxr/ksl/blob/master/doc/guide/dev/developer_rights.md).

## Settings

UI lives in the KSL mod-settings page (open KSL → NetcodeFix). Configurable:

- Master enable / kill switch.
- Smoothing rate × (0.5 – 5.0)
- Snap threshold m (1 – 10)
- Sanity reject threshold m/s (40 – 200)
- Tier 2 / 3 stub toggles (pending implementation)

Persisted to `netcodefix.json` next to the DLL.

## Roadmap

- [x] Tier 1: adaptive smoothing + smart snap
- [x] Tier 1: sanity filter
- [x] Peer presence handshake with capability flags
- [ ] Tier 2: input-history broadcast + replay-based extrapolation
- [ ] Tier 2: 50Hz local upload-rate boost (in modded rooms only)
- [ ] Tier 3: contact arbitration protocol

## Credit

Mod-loading and presence-pattern conventions follow [NoDynamicCollisions](../NoDynamicCollisions.KSL) by Envy. Built on the KSL SDK by trbflxr.
