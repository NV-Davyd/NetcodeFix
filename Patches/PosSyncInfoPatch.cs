using System;
using CarX;
using HarmonyLib;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Patches
{
    // Replaces NetGameSyncPosSystem.PosSyncInfo.Update entirely. The vanilla
    // implementation has two problems for close-quarters racing:
    //
    //   1. Snap threshold is 6m. By the time the error reaches 6m the remote
    //      car has been visibly wrong for seconds (slow smoothing at 1-2 m/s
    //      base rate). The result is the "slow ooze then sudden teleport"
    //      pattern users describe.
    //
    //   2. Base smoothing rates (1 m/s forward, 2 m/s side) are too low for
    //      typical drift-tandem error magnitudes — a 2m correction takes a
    //      full second to bleed off, during which the car visibly trails its
    //      true position.
    //
    // Our replacement:
    //   • Same adaptive-rate idea (rate scales with error) but with a
    //     configurable multiplier (default 2×).
    //   • Configurable snap threshold (default 3m). Errors above the
    //     threshold resolve in one frame instead of smoothing — looks like a
    //     small jump rather than a slow ooze.
    //   • Identical steer/rotation logic, untouched. Only position-error
    //     handling is replaced.
    //
    // We use a Harmony PREFIX returning false to skip the original entirely.
    // This is more invasive than a postfix tweak but unavoidable because
    // the vanilla method computes its own b2 from internal state — we'd have
    // to undo and redo. Cleaner to replace.
    internal static class PosSyncInfoPatch
    {
        // Reflection traversers cached to avoid re-resolving private fields
        // every frame. PosSyncInfo has public fields (fwdPosError, etc.) so
        // we can hit them directly without Traverse for those — but rb/carX
        // we read via the standard public APIs.
        public static bool UpdatePrefix(NetGameSyncPosSystem.PosSyncInfo __instance)
        {
            // Master kill switch — fall back to vanilla.
            if (!Settings.Current.enabled) return true;

            // Local car path is a no-op in vanilla; respect it.
            if (__instance.host.IsLocal || __instance.host.userCar == null)
                return false;

            Car carX = __instance.host.userCar.carX;
            if (carX == null) return false;
            Rigidbody rb = carX.getRigidbody;
            if (rb == null) return false;

            float dt = Time.deltaTime;

            // Steering smoothing: identical to vanilla. Lerp local steer
            // toward the synced target at 10/s.
            float steer = carX.GetSteer();
            carX.SetSteer(Mathf.Lerp(steer, __instance.syncSteer, dt * 10f), false, dt);

            // ── Tunables ───────────────────────────────────────────────────
            float rateMul = Mathf.Max(0.1f, Settings.Current.smoothingRateMultiplier);
            float snapAt  = Mathf.Max(0.5f, Settings.Current.snapThresholdMetres);

            // ── Error magnitudes ───────────────────────────────────────────
            float sideMag = __instance.sidePosError.magnitude;
            float fwdMag  = __instance.fwdPosError.magnitude;
            float totalMag = sideMag + fwdMag;

            Vector3 deltaPos;
            Quaternion deltaRot;

            if (totalMag > snapAt)
            {
                // Hard snap. Apply the entire residual error in one frame.
                // Looks like a small visual jump but is far less awful than
                // multi-second smear toward the right position.
                deltaPos = __instance.fwdPosError + __instance.sidePosError;
                deltaRot = __instance.syncRotError;
                __instance.fwdPosError  = Vector3.zero;
                __instance.sidePosError = Vector3.zero;
                __instance.syncRotError = Quaternion.identity;
            }
            else
            {
                // Adaptive smoothing — same scaling shape as vanilla
                // (rate scales with magnitude) but with our multiplier.
                // Vanilla side-rate is 2×, fwd-rate is 1× — we keep that
                // ratio because lateral error matters more for collisions
                // and visual cohesion than longitudinal trailing.
                Vector3 sideCorr = Vector3.MoveTowards(Vector3.zero,
                                       __instance.sidePosError,
                                       2f * rateMul * dt * sideMag);
                Vector3 fwdCorr  = Vector3.MoveTowards(Vector3.zero,
                                       __instance.fwdPosError,
                                       1f * rateMul * dt * fwdMag);
                deltaPos = sideCorr + fwdCorr;

                // Rotation: same 6/s slerp as vanilla, scaled by rateMul.
                deltaRot = Quaternion.Lerp(Quaternion.identity,
                                           __instance.syncRotError,
                                           6f * rateMul * dt);

                __instance.fwdPosError  -= fwdCorr;
                __instance.sidePosError -= sideCorr;
                __instance.syncRotError *= Quaternion.Inverse(deltaRot);
            }

            // Apply. Same MovePosition + MoveRotation as vanilla. We trust the
            // game's NaN check on rotation; an inversion gone wrong is rare
            // and the existing guard handles it.
            rb.MovePosition(rb.position + deltaPos);
            Quaternion newRot = rb.rotation * deltaRot;
            if (!IsRotationNaN(newRot))
                rb.MoveRotation(newRot);

            return false; // skip original
        }

        // The game has a Quaternion extension `IsRotationHaveNANs()` but it
        // lives in a private extension class. Inline the check to avoid the
        // reflection cost — same logic.
        private static bool IsRotationNaN(Quaternion q)
        {
            return float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)
                || float.IsInfinity(q.x) || float.IsInfinity(q.y)
                || float.IsInfinity(q.z) || float.IsInfinity(q.w);
        }
    }
}
