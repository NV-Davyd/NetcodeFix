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
        private static readonly FieldInfo F_lastExtrapolated =
            AccessTools.Field(typeof(NetGameCircularInterpolator), "<lastExtrapolated>k__BackingField");

        public static bool UpdatePrefix(NetGameSyncPosSystem.PosSyncInfo __instance)
        {
            if (__instance.host.IsLocal || __instance.host.userCar == null)
                return false;

            Car carX = __instance.host.userCar.carX;
            if (carX == null) return false;

            Rigidbody rb = carX.getRigidbody;
            if (rb == null) return false;

            float dt = Time.deltaTime;
            float steer = carX.GetSteer();
            carX.SetSteer(Mathf.Lerp(steer, __instance.syncSteer, dt * 10f), false, dt);

            float rateMul = Mathf.Max(0.1f, Settings.CorrectionRate);
            float sideMag = __instance.sidePosError.magnitude;
            float fwdMag = __instance.fwdPosError.magnitude;

            Vector3 sideCorr = Vector3.MoveTowards(Vector3.zero, __instance.sidePosError, 2f * rateMul * dt * sideMag);
            Vector3 fwdCorr = Vector3.MoveTowards(Vector3.zero, __instance.fwdPosError, 1f * rateMul * dt * fwdMag);
            Vector3 deltaPos = sideCorr + fwdCorr;
            Quaternion deltaRot = Quaternion.Lerp(Quaternion.identity, __instance.syncRotError, 6f * rateMul * dt);

            __instance.fwdPosError -= fwdCorr;
            __instance.sidePosError -= sideCorr;
            __instance.syncRotError *= Quaternion.Inverse(deltaRot);

            rb.MovePosition(rb.position + deltaPos);

            Quaternion newRot = rb.rotation * deltaRot;
            if (!IsRotationNaN(newRot))
                rb.MoveRotation(newRot);

            return false;
        }

        public static IEnumerable<CodeInstruction> SyncTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            LocalBuilder actualExtrapolated = generator.DeclareLocal(typeof(bool));

            MethodInfo getDataFrame = AccessTools.Method(typeof(NetGameCircularInterpolator), nameof(NetGameCircularInterpolator.GetDataFrame));
            MethodInfo getLastExtrapolated = AccessTools.PropertyGetter(typeof(NetGameCircularInterpolator), nameof(NetGameCircularInterpolator.lastExtrapolated));
            MethodInfo restore = AccessTools.Method(typeof(PosSyncInfoPatch), nameof(RestoreActualExtrapolatedState));
            FieldInfo interpolatorField = AccessTools.Field(typeof(NetGameSyncPosSystem.PosSyncInfo), nameof(NetGameSyncPosSystem.PosSyncInfo.interpolator));
            FieldInfo debugPositionField = AccessTools.Field(typeof(NetGameSyncPosSystem.PosSyncInfo), "m_debugSyncPosition");

            int getFrameCalls = 0;
            bool saveAfterStore = false;
            bool restoreAfterDebugStore = false;

            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction instruction = code[i];
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
