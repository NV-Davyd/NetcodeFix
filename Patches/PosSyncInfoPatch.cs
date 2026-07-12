using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CarX;
using HarmonyLib;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Patches
{
    internal static class PosSyncInfoPatch
    {
        private const float RecoveryStartErrorM = 0.35f;
        private const float FullRecoveryErrorM = 3f;
        private const float MaxRecoveryRateMultiplier = 4f;
        private const float MaxRecoverySpeedMps = 18f;
        private const float TeleportThresholdM = 30f;

        private static readonly FieldInfo F_lastExtrapolated =
            AccessTools.Field(typeof(NetGameCircularInterpolator), "<lastExtrapolated>k__BackingField");
        private static readonly FieldInfo F_syncTime =
            AccessTools.Field(typeof(NetGameSyncPosSystem), "m_syncTime");

        public static bool UpdatePrefix(NetGameSyncPosSystem.PosSyncInfo __instance)
        {
            if (__instance.host.IsLocal || __instance.host.userCar == null)
                return false;

            Car carX = __instance.host.userCar.carX;
            if (carX == null) return false;

            Rigidbody rb = carX.getRigidbody;
            if (rb == null) return false;

            SyncCorrectionOverlay.Capture(__instance, carX, rb);

            float dt = Time.deltaTime;
            Vector3 totalError = __instance.fwdPosError + __instance.sidePosError;
            float errorMagnitude = totalError.magnitude;

            if (errorMagnitude >= TeleportThresholdM)
            {
                Quaternion targetRotation = rb.rotation * __instance.syncRotError;
                rb.position += totalError;
                if (!IsRotationNaN(targetRotation))
                {
                    rb.rotation = targetRotation;
                }

                __instance.fwdPosError = Vector3.zero;
                __instance.sidePosError = Vector3.zero;
                __instance.syncRotError = Quaternion.identity;
                return false;
            }

            float recovery = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(RecoveryStartErrorM, FullRecoveryErrorM, errorMagnitude));
            float trajectoryAssist = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(0.05f, 2f, errorMagnitude));

            ApplyTrajectoryAssist(__instance, rb, trajectoryAssist, dt);

            float steer = carX.GetSteer();
            float steerRate = Mathf.Lerp(10f, 35f, recovery);
            float steerResponse = 1f - Mathf.Exp(-steerRate * dt);
            carX.SetSteer(Mathf.Lerp(steer, __instance.syncSteer, steerResponse), false, dt);

            float rateMul = Mathf.Max(0.1f, Settings.EffectiveCorrectionRate);
            float adaptiveRate = rateMul * Mathf.Lerp(1f, MaxRecoveryRateMultiplier, recovery);
            float sideResponse = 1f - Mathf.Exp(-2f * adaptiveRate * dt);
            float fwdResponse = 1f - Mathf.Exp(-adaptiveRate * dt);

            Vector3 sideCorr = __instance.sidePosError * sideResponse;
            Vector3 fwdCorr = __instance.fwdPosError * fwdResponse;
            Vector3 deltaPos = sideCorr + fwdCorr;

            float maxStep = Mathf.Lerp(5f, MaxRecoverySpeedMps, recovery) * dt;
            if (deltaPos.magnitude > maxStep && deltaPos.sqrMagnitude > Mathf.Epsilon)
            {
                float scale = maxStep / deltaPos.magnitude;
                sideCorr *= scale;
                fwdCorr *= scale;
                deltaPos = sideCorr + fwdCorr;
            }

            float rotationRate = Mathf.Min(20f, 6f * rateMul * Mathf.Lerp(1f, 2.5f, recovery));
            float rotationResponse = 1f - Mathf.Exp(-rotationRate * dt);
            Quaternion deltaRot = Quaternion.Lerp(Quaternion.identity, __instance.syncRotError, rotationResponse);

            __instance.fwdPosError -= fwdCorr;
            __instance.sidePosError -= sideCorr;
            __instance.syncRotError *= Quaternion.Inverse(deltaRot);

            rb.MovePosition(rb.position + deltaPos);

            Quaternion newRot = rb.rotation * deltaRot;
            if (!IsRotationNaN(newRot))
                rb.MoveRotation(newRot);

            return false;
        }

        private static void ApplyTrajectoryAssist(
            NetGameSyncPosSystem.PosSyncInfo syncInfo,
            Rigidbody rigidbody,
            float recovery,
            float dt)
        {
            if (recovery <= 0f
                || syncInfo.interpolator == null
                || PreciseTime.fixedUnscaledTimeMs < syncInfo.lockedByCollision)
            {
                return;
            }

            NetGameSyncTimeSystem syncTime = F_syncTime?.GetValue(null) as NetGameSyncTimeSystem;
            if (syncTime == null)
            {
                return;
            }

            bool previousExtrapolated = syncInfo.interpolator.lastExtrapolated;
            PosDataFrame targetFrame;
            try
            {
                targetFrame = syncInfo.interpolator.GetDataFrame(syncTime.GetModelFixedTimeMs());
            }
            finally
            {
                F_lastExtrapolated?.SetValue(syncInfo.interpolator, previousExtrapolated);
            }

            if (!targetFrame.IsValid()
                || targetFrame.IsFlaged(DataFrameFlags.Inactive)
                || targetFrame.IsFlaged(DataFrameFlags.Finished)
                || targetFrame.posGeneration != syncInfo.syncGeneration)
            {
                return;
            }

            float velocityResponse = 1f - Mathf.Exp(-12f * recovery * dt);
            float angularResponse = 1f - Mathf.Exp(-16f * recovery * dt);
            rigidbody.velocity = Vector3.Lerp(rigidbody.velocity, targetFrame.velocity, velocityResponse);
            rigidbody.angularVelocity = Vector3.Lerp(
                rigidbody.angularVelocity,
                targetFrame.angularVelocity,
                angularResponse);
        }

        public static void ApplyNetworkVelocity(Rigidbody rigidbody, Vector3 targetVelocity)
        {
            if (rigidbody == null || !IsFinite(targetVelocity))
            {
                return;
            }

            float difference = (targetVelocity - rigidbody.velocity).magnitude;
            float rate = Mathf.Lerp(18f, 40f, Mathf.InverseLerp(0.5f, 10f, difference));
            float response = 1f - Mathf.Exp(-rate * Time.fixedDeltaTime);
            rigidbody.velocity = Vector3.Lerp(rigidbody.velocity, targetVelocity, response);
        }

        public static void ApplyNetworkAngularVelocity(Rigidbody rigidbody, Vector3 targetAngularVelocity)
        {
            if (rigidbody == null || !IsFinite(targetAngularVelocity))
            {
                return;
            }

            float difference = (targetAngularVelocity - rigidbody.angularVelocity).magnitude;
            float rate = Mathf.Lerp(20f, 45f, Mathf.InverseLerp(0.2f, 5f, difference));
            float response = 1f - Mathf.Exp(-rate * Time.fixedDeltaTime);
            rigidbody.angularVelocity = Vector3.Lerp(rigidbody.angularVelocity, targetAngularVelocity, response);
        }

        public static IEnumerable<CodeInstruction> SyncTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            LocalBuilder actualExtrapolated = generator.DeclareLocal(typeof(bool));

            MethodInfo getDataFrame = AccessTools.Method(typeof(NetGameCircularInterpolator), nameof(NetGameCircularInterpolator.GetDataFrame));
            MethodInfo getLastExtrapolated = AccessTools.PropertyGetter(typeof(NetGameCircularInterpolator), nameof(NetGameCircularInterpolator.lastExtrapolated));
            MethodInfo restore = AccessTools.Method(typeof(PosSyncInfoPatch), nameof(RestoreActualExtrapolatedState));
            MethodInfo setVelocity = AccessTools.PropertySetter(typeof(Rigidbody), nameof(Rigidbody.velocity));
            MethodInfo setAngularVelocity = AccessTools.PropertySetter(typeof(Rigidbody), nameof(Rigidbody.angularVelocity));
            MethodInfo applyVelocity = AccessTools.Method(typeof(PosSyncInfoPatch), nameof(ApplyNetworkVelocity));
            MethodInfo applyAngularVelocity = AccessTools.Method(typeof(PosSyncInfoPatch), nameof(ApplyNetworkAngularVelocity));
            FieldInfo interpolatorField = AccessTools.Field(typeof(NetGameSyncPosSystem.PosSyncInfo), nameof(NetGameSyncPosSystem.PosSyncInfo.interpolator));
            FieldInfo debugPositionField = AccessTools.Field(typeof(NetGameSyncPosSystem.PosSyncInfo), "m_debugSyncPosition");

            int getFrameCalls = 0;
            bool saveAfterStore = false;
            bool restoreAfterDebugStore = false;

            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction instruction = code[i];

                if (instruction.Calls(setVelocity))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = applyVelocity;
                }
                else if (instruction.Calls(setAngularVelocity))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = applyAngularVelocity;
                }

                yield return instruction;

                if (instruction.Calls(getDataFrame))
                {
                    getFrameCalls++;
                    if (getFrameCalls == 1)
                    {
                        saveAfterStore = true;
                    }
                    else if (getFrameCalls == 2)
                    {
                        restoreAfterDebugStore = true;
                    }
                }

                if (saveAfterStore && IsStoreLocal(instruction))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, interpolatorField);
                    yield return new CodeInstruction(OpCodes.Callvirt, getLastExtrapolated);
                    yield return new CodeInstruction(OpCodes.Stloc, actualExtrapolated);
                    saveAfterStore = false;
                }

                if (restoreAfterDebugStore && instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, debugPositionField))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, interpolatorField);
                    yield return new CodeInstruction(OpCodes.Ldloc, actualExtrapolated);
                    yield return new CodeInstruction(OpCodes.Call, restore);
                    restoreAfterDebugStore = false;
                }
            }
        }

        public static void RestoreActualExtrapolatedState(NetGameCircularInterpolator interpolator, bool value)
        {
            if (interpolator != null)
            {
                F_lastExtrapolated?.SetValue(interpolator, value);
            }
        }

        private static bool IsRotationNaN(Quaternion q)
        {
            return float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)
                || float.IsInfinity(q.x) || float.IsInfinity(q.y)
                || float.IsInfinity(q.z) || float.IsInfinity(q.w);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static bool IsStoreLocal(CodeInstruction instruction)
        {
            return instruction.opcode == OpCodes.Stloc
                || instruction.opcode == OpCodes.Stloc_0
                || instruction.opcode == OpCodes.Stloc_1
                || instruction.opcode == OpCodes.Stloc_2
                || instruction.opcode == OpCodes.Stloc_3
                || instruction.opcode == OpCodes.Stloc_S;
        }
    }
}
