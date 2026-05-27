using KSL.API;
using SyncMultiplayer;
using UnityEngine;

namespace NetcodeFix.Net
{
    // Serializes/deserializes a PosDataFrame for transport over Kino.Sync's
    // IPacket primitives. NOT a re-implementation of the game's wire format —
    // we use IPacket's WriteFloat/ReadFloat/etc. directly, which the KSL layer
    // handles end-to-end. The receiver decodes into the SAME PosDataFrame
    // struct the game uses, so it can be fed straight into the interpolator.
    //
    // Field selection matches what the game's GetCurrentFromCar() captures
    // — we want our supplemental frames to be indistinguishable from the
    // ones the game generates internally. Omitting fields would create
    // visible inconsistencies during extrapolation.
    internal static class SyncPacketCodec
    {
        // Schema version. Bump if we change the field set so old peers can
        // detect a mismatch and ignore packets rather than reading garbage.
        public const int Schema = 1;

        public static void WritePoseFrame(IPacket p, int senderNetworkId, ref PosDataFrame f)
        {
            p.WriteInt32(Schema);
            p.WriteInt32(senderNetworkId);

            p.WriteInt64(f.modelTime);
            p.WriteByte(f.posGeneration);
            p.WriteUInt32((uint)f.flags);

            // Inputs
            p.WriteFloat(f.accelerate);
            p.WriteFloat(f.brake);
            p.WriteFloat(f.handbrake);
            p.WriteFloat(f.clutch);
            p.WriteFloat(f.steer);
            p.WriteByte(f.gear);
            p.WriteFloat(f.rpm);
            p.WriteBool(f.flares);

            // Engine state
            p.WriteBool(f.processEngineStart);
            p.WriteBool(f.ignitionEnabled);
            p.WriteBool(f.starterEnabled);
            p.WriteFloat(f.engineStartTime);
            p.WriteBool(f.autoClutch);
            p.WriteBool(f.clutchKick);

            // Pose
            WriteVec3(p, f.position);
            WriteQuat(p, f.rotation);
            WriteVec3(p, f.velocity);
            WriteVec3(p, f.angularVelocity);

            // Accelerations (used by the game's extrapolator)
            p.WriteFloat(f.longAccel);
            p.WriteFloat(f.sideAccel);
            p.WriteFloat(f.angAccel);

            p.WriteFloat(f.mileage);
        }

        // Returns false if the schema doesn't match — caller should drop the packet.
        public static bool TryReadPoseFrame(IPacket p, out int senderNetworkId, out PosDataFrame f)
        {
            f = PosDataFrame.Default;
            senderNetworkId = 0;

            int schema = p.ReadInt32();
            if (schema != Schema) return false;
            senderNetworkId = p.ReadInt32();

            f.modelTime     = p.ReadInt64();
            f.posGeneration = p.ReadByte();
            f.flags         = (DataFrameFlags)p.ReadUInt32();

            f.accelerate = p.ReadFloat();
            f.brake      = p.ReadFloat();
            f.handbrake  = p.ReadFloat();
            f.clutch     = p.ReadFloat();
            f.steer      = p.ReadFloat();
            f.gear       = p.ReadByte();
            f.rpm        = p.ReadFloat();
            f.flares     = p.ReadBool();

            f.processEngineStart = p.ReadBool();
            f.ignitionEnabled    = p.ReadBool();
            f.starterEnabled     = p.ReadBool();
            f.engineStartTime    = p.ReadFloat();
            f.autoClutch         = p.ReadBool();
            f.clutchKick         = p.ReadBool();

            f.position        = ReadVec3(p);
            f.rotation        = ReadQuat(p);
            f.velocity        = ReadVec3(p);
            f.angularVelocity = ReadVec3(p);

            f.longAccel = p.ReadFloat();
            f.sideAccel = p.ReadFloat();
            f.angAccel  = p.ReadFloat();

            f.mileage = p.ReadFloat();
            return true;
        }

        // ── Helpers ────────────────────────────────────────────────────────
        private static void WriteVec3(IPacket p, Vector3 v)
        {
            p.WriteFloat(v.x); p.WriteFloat(v.y); p.WriteFloat(v.z);
        }
        private static Vector3 ReadVec3(IPacket p)
            => new Vector3(p.ReadFloat(), p.ReadFloat(), p.ReadFloat());

        private static void WriteQuat(IPacket p, Quaternion q)
        {
            p.WriteFloat(q.x); p.WriteFloat(q.y); p.WriteFloat(q.z); p.WriteFloat(q.w);
        }
        private static Quaternion ReadQuat(IPacket p)
            => new Quaternion(p.ReadFloat(), p.ReadFloat(), p.ReadFloat(), p.ReadFloat());
    }
}
