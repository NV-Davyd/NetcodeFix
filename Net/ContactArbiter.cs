using System;
using System.Collections.Generic;
using System.Reflection;
using CarX;
using HarmonyLib;
using KSL.API;
using SyncMultiplayer;
using SyncMultiplayer.DynamicCollisions.Stages;
using Unity.Jobs;
using UnityEngine;

namespace NetcodeFix.Net
{
    // Tier 3 — peer-to-peer contact arbitration.
    //
    // Vanilla CarX collision resolution is asymmetric: each client runs its
    // own physics with its own slightly-disagreeing view of where opponents
    // are, so contact impulses differ between the two clients. The visible
    // outcome is the "I touched, they were 0.5m away" problem.
    //
    // Our fix:
    //   1. When the room is verified all-modded and Tier 3 is enabled, we
    //      suppress the vanilla DynamicCollisionResolver job entirely
    //      (same trick NoDynCollisions uses, but conditionally).
    //   2. We do proximity-based detection ourselves in FixedUpdate.
    //   3. On detected contact with a modded peer, we send a ContactAssertion
    //      packet via Kino.Sync.SendTo (point-to-point — not broadcast).
    //   4. Both peers' assertions get matched by (opponent, timestamp ± window).
    //      When matched, both sides compute the SAME deterministic Δv from
    //      the averaged canonical state and apply it to their local rigidbody.
    //   5. Phantom contacts (one side saw it, other didn't within timeout)
    //      apply a softened single-sided impulse — better than nothing,
    //      avoids being "stuck" on a ghost contact.
    //
    // Mass is currently assumed equal (drift cars are roughly similar).
    // Restitution is configurable (default 0.4 — drift-cars don't bounce
    // hard).
    public sealed class ContactArbiter
    {
        private const string PacketName = "netcodefix_contact";

        // Tolerances for matching incoming peer assertions to our pending ones.
        private const float MatchWindowSecs    = 0.150f;  // ±150ms window
        private const float PendingTimeoutSecs = 0.300f;  // assume phantom after this

        // Detection params.
        private const float CarBoundsRadius = 1.6f;  // m. Drift cars ~3-4m long, 1.8m wide.
        private const float ContactCooldownSecs = 0.080f;

        // Impulse params.
        private const float Restitution = 0.40f;
        // Phantom contacts apply a fraction of the impulse a confirmed contact would.
        // Lower = more conservative (no rage from ghost-hits), higher = closer to vanilla.
        private const float PhantomImpulseScale = 0.25f;

        private readonly PeerPresence _presence;
        private int   _packetId = -1;
        private bool  _ready;

        // Pending contacts we've sent but not yet received a peer match for.
        // Keyed by (opponentNetworkId, sequence) — each side picks its own sequence.
        private readonly Dictionary<long, Pending> _pending = new Dictionary<long, Pending>();
        private long _nextSeq;

        // Per-opponent cooldown so we don't fire multiple contacts within
        // a few frames as bodies separate. ms since startup.
        private readonly Dictionary<int, float> _lastContactByPeer = new Dictionary<int, float>();

        // ── Stats (shown in UI) ────────────────────────────────────────────
        public long ContactsDetected  { get; private set; }
        public long ContactsConfirmed { get; private set; }
        public long ContactsPhantom   { get; private set; }
        public long ContactsTimedOut  { get; private set; }

        // ── Vanilla-resolver-suppression patch ─────────────────────────────
        // Same hook NoDynCollisions uses; we wrap it in a runtime check so
        // it only suppresses while Tier 3 is active. Off otherwise.
        private static readonly Harmony ResolverHarmony = new Harmony("com.netcodefix.tier3");
        private static bool _resolverPatched;
        private static bool _suppressVanillaResolver;

        public ContactArbiter(PeerPresence presence)
        {
            _presence = presence;
        }

        public void Initialize()
        {
            if (!Kino.Sync.IsAvailable)
            {
                Kino.Log.Warning("[NetcodeFix] Kino.Sync unavailable; Tier 3 disabled.");
                return;
            }
            if (!Kino.Sync.TryRegisterPacket(PacketName, out _packetId))
            {
                Kino.Log.Warning("[NetcodeFix] Could not register contact packet.");
                return;
            }
            if (!Kino.Sync.SetCallback(_packetId, OnReceive))
            {
                Kino.Log.Warning("[NetcodeFix] Could not register contact callback.");
                return;
            }

            // Install the conditional CollisionResolver suppression patch
            // once. Whether it actually suppresses is controlled by
            // _suppressVanillaResolver, set per-tick from RoomCaps.
            if (!_resolverPatched)
            {
                try
                {
                    ResolverHarmony.Patch(
                        AccessTools.Method(typeof(CollisionResolver), nameof(CollisionResolver.Process)),
                        prefix: new HarmonyMethod(typeof(ContactArbiter), nameof(ResolverPrefix)));
                    _resolverPatched = true;
                }
                catch (Exception ex)
                {
                    Kino.Log.Warning("[NetcodeFix] CollisionResolver patch failed: " + ex.Message);
                }
            }

            _ready = true;
            Kino.Log.Info("[NetcodeFix] Contact arbitration ready (packetId=" + _packetId + ")");
        }

        public void Shutdown()
        {
            if (_resolverPatched)
            {
                try { ResolverHarmony.UnpatchSelf(); } catch { }
                _resolverPatched = false;
            }
            _ready = false;
        }

        // Per-frame tick driven from FixedUpdate so detection runs at
        // physics rate (where car positions actually update).
        public void FixedTick()
        {
            if (!_ready) return;

            bool active = Settings.Current.enabled
                       && Settings.Current.tier3ContactArbitration
                       && _presence.RemoteModdedCount > 0
                       && (_presence.RoomCaps & PeerPresence.Caps.ContactArbiter) != 0;

            _suppressVanillaResolver = active;
            if (!active)
            {
                // Drain stale pending entries — when we go inactive, abandon
                // anything outstanding without trying to apply phantom.
                _pending.Clear();
                return;
            }

            ScanForContacts();
            SweepExpiredPending();
        }

        // ── Detection ──────────────────────────────────────────────────────
        // Walk all visible modded peers, check proximity vs. local car.
        // When within bounding-radius sum AND closing, fire contact.
        private void ScanForContacts()
        {
            var game = NetworkController.InstanceGame;
            if (game == null) return;
            var localPlayer = game.LocalPlayer;
            var localCar    = localPlayer?.userCar;
            if (localCar?.carX == null || localCar.carX.getRigidbody == null) return;

            Rigidbody localRb = localCar.carX.getRigidbody;
            Vector3 localPos = localRb.position;
            Vector3 localVel = localRb.velocity;
            float now = Time.fixedUnscaledTime;

            const float thresholdDist = 2f * CarBoundsRadius;
            float thresholdSqr = thresholdDist * thresholdDist;

            foreach (var peerEntry in _presence.Peers)
            {
                // Only check confirmed modded peers (those who advertised
                // ContactArbiter cap — otherwise we'd try to arbitrate
                // contact with someone who can't reciprocate).
                if ((peerEntry.Value.Capabilities & PeerPresence.Caps.ContactArbiter) == 0)
                    continue;

                var remotePlayer = FindNetworkPlayerById(peerEntry.Key);
                if (remotePlayer == null) continue;
                var remoteCar = remotePlayer.userCar;
                if (remoteCar?.carX == null || remoteCar.carX.getRigidbody == null) continue;

                Rigidbody remoteRb = remoteCar.carX.getRigidbody;
                Vector3 separation = remoteRb.position - localPos;
                float sqr = separation.sqrMagnitude;
                if (sqr > thresholdSqr) continue;

                // Cooldown gate (so a single physical contact doesn't fire
                // multiple times as the cars sit overlapping).
                if (_lastContactByPeer.TryGetValue(remotePlayer.NetworkID, out float last)
                    && now - last < ContactCooldownSecs)
                    continue;

                // Approach check: relative velocity along separation > 0.
                Vector3 relVel = localVel - remoteRb.velocity;
                Vector3 normal = separation.sqrMagnitude > 1e-6f ? separation.normalized : Vector3.right;
                float closing = Vector3.Dot(relVel, normal);
                if (closing <= 0.1f) continue; // not actually colliding (just nearby)

                OnLocalContactDetected(remotePlayer, localCar, remoteCar, localPos, localVel, remoteRb, normal, closing);
                _lastContactByPeer[remotePlayer.NetworkID] = now;
            }
        }

        // ── Local contact handler ──────────────────────────────────────────
        private void OnLocalContactDetected(
            NetworkPlayer peer, RaceCar localCar, RaceCar remoteCar,
            Vector3 localPos, Vector3 localVel, Rigidbody remoteRb,
            Vector3 normalFromMe, float closing)
        {
            ContactsDetected++;

            long seq = ++_nextSeq;
            long timestamp = (long)(Time.fixedUnscaledTime * 1000f);

            // Send assertion to the peer (point-to-point).
            try
            {
                var p = Kino.Sync.CreatePacket(_packetId);
                p.WriteInt32(1); // schema
                p.WriteInt64(seq);
                p.WriteInt64(timestamp);
                p.WriteInt32(NetworkController.InstanceGame.LocalPlayer.NetworkID);
                p.WriteInt32(peer.NetworkID);
                WriteVec3(p, localPos);
                WriteVec3(p, localVel);
                WriteVec3(p, remoteRb.position);
                WriteVec3(p, remoteRb.velocity);
                WriteVec3(p, normalFromMe);
                p.WriteFloat(closing);

                // SendTo (peer.NetworkID) doesn't exist on Kino.Sync directly;
                // we'd need a uint64 peer ID. KSL's ISync takes ulong client
                // ID for point-to-point. For now broadcast — receivers filter
                // by target field. Slight bandwidth cost, simpler protocol.
                Kino.Sync.SendToAll(p);
            }
            catch (Exception ex)
            {
                Kino.Log.Warning("[NetcodeFix] Contact send failed: " + ex.Message);
            }

            // Park pending until peer's matching assertion arrives or we time out.
            _pending[seq] = new Pending
            {
                Sequence       = seq,
                LocalCar       = localCar,
                PeerNetworkId  = peer.NetworkID,
                Timestamp      = timestamp,
                LocalPos       = localPos,
                LocalVel       = localVel,
                RemotePosLocal = remoteRb.position,
                RemoteVelLocal = remoteRb.velocity,
                Normal         = normalFromMe,
                Closing        = closing,
                CreatedAt      = Time.fixedUnscaledTime,
            };
        }

        // ── Receive ────────────────────────────────────────────────────────
        private void OnReceive(IPacket packet)
        {
            if (packet.Id != _packetId) return;
            try
            {
                int schema = packet.ReadInt32();
                if (schema != 1) return;
                long peerSeq        = packet.ReadInt64();
                long peerTimestamp  = packet.ReadInt64();
                int  peerSenderId   = packet.ReadInt32();
                int  peerTargetId   = packet.ReadInt32();
                Vector3 peerLocalPos = ReadVec3(packet);
                Vector3 peerLocalVel = ReadVec3(packet);
                Vector3 peerMyPos    = ReadVec3(packet); // peer's view of US
                Vector3 peerMyVel    = ReadVec3(packet);
                Vector3 peerNormal   = ReadVec3(packet);
                float   peerClosing  = packet.ReadFloat();

                // Filter: only act on assertions targeting us specifically.
                var localPlayer = NetworkController.InstanceGame?.LocalPlayer;
                if (localPlayer == null || peerTargetId != localPlayer.NetworkID) return;

                // Find a pending contact we sent that matches this peer's
                // assertion (same opponent + close-enough timestamp).
                Pending matched = null;
                foreach (var p in _pending.Values)
                {
                    if (p.PeerNetworkId != peerSenderId) continue;
                    float dtSecs = Mathf.Abs((peerTimestamp - p.Timestamp) / 1000f);
                    if (dtSecs > MatchWindowSecs) continue;
                    matched = p;
                    break;
                }

                if (matched != null)
                {
                    _pending.Remove(matched.Sequence);
                    ContactsConfirmed++;
                    ApplyConfirmedImpulse(matched, peerLocalPos, peerLocalVel, peerMyPos, peerMyVel, peerNormal);
                }
                else
                {
                    // We didn't detect a contact ourselves but the peer did.
                    // This is the asymmetric case — they hit us on their
                    // screen. Apply softened single-sided impulse so we
                    // visually react. Better than the vanilla "they hit
                    // air, you keep driving" result.
                    ContactsPhantom++;
                    ApplyPhantomImpulse(peerSenderId, peerMyPos, peerMyVel, peerLocalPos, peerLocalVel, peerNormal);
                }
            }
            catch (Exception ex)
            {
                Kino.Log.Warning("[NetcodeFix] Contact receive failed: " + ex.Message);
            }
        }

        // ── Impulse computation ────────────────────────────────────────────
        // Canonical pre-contact state = average of the two views.
        // Δv for an elastic collision with equal masses:
        //   Δv_self = -0.5 * (1+e) * ((v_self - v_other) · n) * n
        //
        // Both ends apply the same magnitude/direction (each to their own
        // car) → both screens see identical post-contact dynamics.
        private void ApplyConfirmedImpulse(Pending mine,
            Vector3 peerSelfPos, Vector3 peerSelfVel,
            Vector3 peerMyPos, Vector3 peerMyVel, Vector3 peerNormal)
        {
            // Canonical local-car view = average of "my local pos" and
            // "peer's view of my pos." Same for velocity.
            Vector3 canonLocalVel = (mine.LocalVel + peerMyVel) * 0.5f;
            Vector3 canonPeerVel  = (mine.RemoteVelLocal + peerSelfVel) * 0.5f;

            // Normal: average of both views, pointing FROM peer TO us.
            // mine.Normal points from us to them; peerNormal points from
            // them to us. Negate one, average, normalize.
            Vector3 normTowardUs = (-mine.Normal + peerNormal) * 0.5f;
            if (normTowardUs.sqrMagnitude < 1e-6f) normTowardUs = -mine.Normal;
            normTowardUs.Normalize();

            float relAlongN = Vector3.Dot(canonLocalVel - canonPeerVel, normTowardUs);
            if (relAlongN >= 0f) return; // already separating, no impulse

            float impulseMag = -0.5f * (1f + Restitution) * relAlongN;
            Vector3 deltaV = impulseMag * normTowardUs;

            // Apply to LOCAL rigidbody. The peer will do the same on theirs
            // (with their averaged inputs, which equal ours because the
            // averaging is symmetric in both directions).
            ApplyDeltaToLocal(mine.LocalCar, deltaV);
        }

        private void ApplyPhantomImpulse(int peerNetworkId,
            Vector3 peerMyPos, Vector3 peerMyVel,
            Vector3 peerSelfPos, Vector3 peerSelfVel, Vector3 peerNormal)
        {
            var localCar = NetworkController.InstanceGame?.LocalPlayer?.userCar;
            if (localCar?.carX == null || localCar.carX.getRigidbody == null) return;
            Rigidbody rb = localCar.carX.getRigidbody;

            // Peer's frame — accept their view since we didn't detect anything.
            // Normal pointed from them to us, in their frame. Use as-is.
            Vector3 normTowardUs = peerNormal.normalized;
            Vector3 relVel = rb.velocity - peerSelfVel;
            float relAlongN = Vector3.Dot(relVel, normTowardUs);
            if (relAlongN >= 0f) return;

            float impulseMag = -0.5f * (1f + Restitution) * relAlongN * PhantomImpulseScale;
            Vector3 deltaV = impulseMag * normTowardUs;
            ApplyDeltaToLocal(localCar, deltaV);
        }

        private static void ApplyDeltaToLocal(RaceCar car, Vector3 deltaV)
        {
            if (car?.carX?.getRigidbody == null) return;
            Rigidbody rb = car.carX.getRigidbody;
            rb.velocity = rb.velocity + deltaV;
        }

        // ── Pending-expiry sweep ───────────────────────────────────────────
        // Any pending assertion older than PendingTimeoutSecs without a peer
        // response gets applied as a phantom-on-the-local-side: we saw it,
        // they didn't. Use softened impulse.
        private readonly List<long> _expiredSeqs = new List<long>();
        private void SweepExpiredPending()
        {
            float now = Time.fixedUnscaledTime;
            _expiredSeqs.Clear();
            foreach (var kv in _pending)
            {
                if (now - kv.Value.CreatedAt > PendingTimeoutSecs)
                    _expiredSeqs.Add(kv.Key);
            }
            foreach (long seq in _expiredSeqs)
            {
                var p = _pending[seq];
                _pending.Remove(seq);
                ContactsTimedOut++;

                // Apply softened from our own view (mirror of phantom path).
                float relAlongN = Vector3.Dot(p.LocalVel - p.RemoteVelLocal, p.Normal);
                if (relAlongN <= 0f) continue;
                float impulseMag = 0.5f * (1f + Restitution) * relAlongN * PhantomImpulseScale;
                Vector3 deltaV = -impulseMag * p.Normal;
                ApplyDeltaToLocal(p.LocalCar, deltaV);
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────
        private static NetworkPlayer FindNetworkPlayerById(string playerIdStr)
        {
            // PeerPresence keys peers by NetworkPlayer.PlayerId.ToString().
            // Walk the players list and match.
            var game = NetworkController.InstanceGame;
            if (game == null) return null;
            foreach (var p in game.Players)
            {
                try { if (p.PlayerId.ToString() == playerIdStr) return p; }
                catch { }
            }
            return null;
        }

        private static void WriteVec3(IPacket p, Vector3 v)
        {
            p.WriteFloat(v.x); p.WriteFloat(v.y); p.WriteFloat(v.z);
        }
        private static Vector3 ReadVec3(IPacket p)
            => new Vector3(p.ReadFloat(), p.ReadFloat(), p.ReadFloat());

        // Prefix on CollisionResolver.Process. Returns false (skip) when
        // Tier 3 is currently active, deferring all inter-player collision
        // physics to our arbitration. Returns true (run vanilla) otherwise.
        public static bool ResolverPrefix(JobHandle dependency, ref JobHandle __result)
        {
            if (_suppressVanillaResolver)
            {
                __result = dependency;
                return false;
            }
            return true;
        }

        // ── Pending ────────────────────────────────────────────────────────
        private sealed class Pending
        {
            public long      Sequence;
            public RaceCar   LocalCar;
            public int       PeerNetworkId;
            public long      Timestamp;
            public Vector3   LocalPos;
            public Vector3   LocalVel;
            public Vector3   RemotePosLocal;
            public Vector3   RemoteVelLocal;
            public Vector3   Normal;
            public float     Closing;
            public float     CreatedAt;
        }
    }
}
