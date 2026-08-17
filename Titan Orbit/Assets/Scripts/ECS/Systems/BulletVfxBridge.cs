using System.Collections.Concurrent;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Cross-world bullet VFX queues (spawn + hit).
    /// <para>
    /// Server enqueues on fire / hit (local host). Client RPC handlers enqueue for dedicated
    /// clients. <see cref="Game.BulletVfxDriver"/> is the sole consumer — Instantiates muzzle /
    /// tracer / impact GameObjects. Sequence dedupe prevents host double-spawn and double-hit
    /// (in-process bridge + RPC). Local anticipation uses <see cref="SpawnRequest.IsAnticipation"/>
    /// with Sequence 0 until a server spawn adopts it.
    /// </para>
    /// </summary>
    public static class BulletVfxBridge
    {
        /// <summary>One cosmetic tracer spawn (server-authoritative or local anticipation).</summary>
        public struct SpawnRequest
        {
            public uint Sequence;
            public float3 SpawnPosition;
            public float3 Velocity;
            /// <summary>
            /// Tracer age budget in seconds. ≤ 0 = distance-only (planetary defense);
            /// <see cref="Game.BulletVfxDriver"/> maps that to +∞ RemainingLifetime.
            /// </summary>
            public float Lifetime;
            public float MaxDistance;
            public float Damage;
            public byte OwnerTeam;
            public int OwnerNetworkId;
            public int BankIndex;
            public float ScaleMultiplier;
            /// <summary>
            /// [TITAN-ORBIT] Weapon mount index for muzzle reproject (matches server volley order).
            /// </summary>
            public int MountIndex;
            /// <summary>True when client fired locally before the server RPC arrived.</summary>
            public bool IsAnticipation;
            /// <summary>True when positions are already in display/world space (skip toroidal unwrap).</summary>
            public bool IsDisplaySpace;
            /// <summary>
            /// [TITAN-ORBIT] Matches server <c>BulletElement.DamageFilter</c> for cosmetic pass-through.
            /// </summary>
            public byte DamageFilter;

            /// <summary>1 = homing rocket — client tracer steers toward the closest enemy.</summary>
            public byte Homing;

            /// <summary>Max yaw rate in degrees per second while Homing is set.</summary>
            public float TurnSpeedDeg;

            /// <summary>Toroidal search radius. Empty bubble = fly straight (never whole-map at 0).</summary>
            public float AcquireRange;
        }

        /// <summary>Authoritative impact — destroy matching tracer and play impact VFX.</summary>
        public struct HitRequest
        {
            public uint Sequence;
            public float3 HitPosition;
            public float Damage;
            public byte OwnerTeam;
            /// <summary>Shooter NetworkId — orphan anticipation reconcile when Sequence is unbound.</summary>
            public int OwnerNetworkId;
            /// <summary>Firing mount (−1 unknown). Prefer this over nearest-tracer fallback.</summary>
            public int MountIndex;
            public int BankIndex;
            public float ScaleMultiplier;
            /// <summary>
            /// Asteroid Health after this hit, or &lt; 0 when not an asteroid impact.
            /// Mirrors <see cref="BulletHitRpc.AsteroidHealthAfter"/>.
            /// </summary>
            public float AsteroidHealthAfter;

            /// <summary>
            /// <see cref="BulletHitRpc.PlanetaryDefensePlanetId"/> — 0 when not a PD hit.
            /// </summary>
            public int PlanetaryDefensePlanetId;

            /// <summary>
            /// <see cref="BulletHitRpc.PlanetaryDefenseSlotIndex"/> when PlanetId &gt; 0.
            /// </summary>
            public byte PlanetaryDefenseSlotIndex;

            /// <summary>
            /// <see cref="BulletHitRpc.PlanetaryDefenseHealthAfter"/> — remaining turret HP
            /// (0 = destroyed this hit). Ignored when PlanetId is 0.
            /// </summary>
            public float PlanetaryDefenseHealthAfter;
        }

        static readonly ConcurrentQueue<SpawnRequest> SpawnQueue = new ConcurrentQueue<SpawnRequest>();
        static readonly ConcurrentQueue<HitRequest> HitQueue = new ConcurrentQueue<HitRequest>();

        // --- Spawn dedupe (host bridge + BulletSpawnRpc) ---
        static readonly HashSet<uint> SeenSpawnSequences = new HashSet<uint>();
        static readonly Queue<uint> SeenSpawnOrder = new Queue<uint>(128);

        // --- Hit dedupe (host bridge + BulletHitRpc) — MUST be separate from spawn ---
        // [TITAN-ORBIT] Without this, Local Host processes each hit twice: first destroys the
        // correct tracer by Sequence; second misses the index and nearest-fallback kills a
        // different flying tracer → looks like bullets "go through" asteroids.
        static readonly HashSet<uint> SeenHitSequences = new HashSet<uint>();
        static readonly Queue<uint> SeenHitOrder = new Queue<uint>(128);

        static uint s_NextSequence = 1;
        const int MaxSeen = 512;

        /// <summary>
        /// Sequence=0 ram/grind fingerprints already enqueued this Unity frame.
        /// Host in-process + broadcast RPC would otherwise play each pulse twice.
        /// </summary>
        static readonly HashSet<uint> RamHitsThisFrame = new HashSet<uint>();

        /// <summary>[UNITY] Frame index that <see cref="RamHitsThisFrame"/> belongs to.</summary>
        static int s_RamHitFrame = -1;

        /// <summary>
        /// Max live Sequence=0 anticipation tracers for the local player.
        /// [TITAN-ORBIT] Enough for one multi-cannon volley (upgrade hulls up to ~8 weapons).
        /// Client fire still gates on FireCooldown + energy, so this is not an open RoF spam path.
        /// Older Cap=1 hid every muzzle after the first in a volley.
        /// </summary>
        public const int MaxLiveAnticipations = 32;

        /// <summary>Live anticipation tracers (driver maintains; used to gate local enqueue).</summary>
        public static int LiveAnticipationCount { get; private set; }

        /// <summary>Allocates the next shot sequence id (server only).</summary>
        public static uint NextSequence() => s_NextSequence++;

        /// <summary>True when the local client may enqueue another anticipation tracer.</summary>
        public static bool CanEnqueueAnticipation() => CanEnqueueAnticipation(1);

        /// <summary>
        /// True when the local client may enqueue <paramref name="count"/> anticipation tracers
        /// (one fire-tick volley across all weapon mounts).
        /// </summary>
        public static bool CanEnqueueAnticipation(int count)
        {
            int need = math.max(1, count);
            return LiveAnticipationCount + need <= MaxLiveAnticipations;
        }

        /// <summary>Driver: anticipation tracer Instantiated.</summary>
        public static void NotifyAnticipationCreated() => LiveAnticipationCount++;

        /// <summary>Driver: anticipation adopted (now sequenced) or destroyed while still orphan.</summary>
        public static void NotifyAnticipationConsumed()
        {
            if (LiveAnticipationCount > 0)
                LiveAnticipationCount--;
        }

        /// <summary>
        /// Enqueues a spawn. Server sequences (non-zero) are deduped against host queue + RPC.
        /// Anticipation (Sequence 0) always enqueues when the caller already gated the cap.
        /// </summary>
        public static bool TryEnqueueSpawn(in SpawnRequest request)
        {
            if (request.Sequence != 0 && !RememberSpawnSequence(request.Sequence))
                return false;
            SpawnQueue.Enqueue(request);
            return true;
        }

        /// <summary>Driver: take next pending spawn.</summary>
        public static bool TryDequeueSpawn(out SpawnRequest request) => SpawnQueue.TryDequeue(out request);

        /// <summary>
        /// Enqueues an impact. Non-zero Sequence is deduped so Local Host bridge + HitRpc do not
        /// double-apply. Sequence 0 is a ram/grind flash (no tracer) — those are deduped by pose
        /// on the same Unity frame instead, because every ram pulse shares Sequence 0.
        /// </summary>
        public static void EnqueueHit(in HitRequest request)
        {
            if (request.Sequence == 0)
            {
                // --- Ram / grind (no tracer) ---
                // [TITAN-ORBIT] Same pulse arrives twice on listen-server (in-process + RPC).
                if (!RememberRamHitThisFrame(in request))
                    return;
                HitQueue.Enqueue(request);
                return;
            }

            if (!RememberHitSequence(request.Sequence))
                return;
            HitQueue.Enqueue(request);
        }

        /// <summary>Driver: take next pending hit.</summary>
        public static bool TryDequeueHit(out HitRequest request) => HitQueue.TryDequeue(out request);

        /// <summary>Clears pending queues when leaving a match.</summary>
        public static void Clear()
        {
            while (SpawnQueue.TryDequeue(out _)) { }
            while (HitQueue.TryDequeue(out _)) { }
            SeenSpawnSequences.Clear();
            SeenSpawnOrder.Clear();
            SeenHitSequences.Clear();
            SeenHitOrder.Clear();
            LiveAnticipationCount = 0;
            RamHitsThisFrame.Clear();
            s_RamHitFrame = -1;
        }

        /// <summary>
        /// True when this Sequence=0 ram hit should play. False when the same pose/damage/team
        /// was already enqueued this Unity frame (listen-server in-process + RPC).
        /// </summary>
        static bool RememberRamHitThisFrame(in HitRequest request)
        {
            int frame = Time.frameCount;
            if (frame != s_RamHitFrame)
            {
                RamHitsThisFrame.Clear();
                s_RamHitFrame = frame;
            }

            uint fingerprint = math.hash(new float4(
                request.HitPosition.x,
                request.HitPosition.z,
                request.Damage,
                request.OwnerTeam));
            return RamHitsThisFrame.Add(fingerprint);
        }

        static bool RememberSpawnSequence(uint sequence)
        {
            if (!SeenSpawnSequences.Add(sequence))
                return false;
            SeenSpawnOrder.Enqueue(sequence);
            while (SeenSpawnOrder.Count > MaxSeen)
                SeenSpawnSequences.Remove(SeenSpawnOrder.Dequeue());
            return true;
        }

        static bool RememberHitSequence(uint sequence)
        {
            if (!SeenHitSequences.Add(sequence))
                return false;
            SeenHitOrder.Enqueue(sequence);
            while (SeenHitOrder.Count > MaxSeen)
                SeenHitSequences.Remove(SeenHitOrder.Dequeue());
            return true;
        }
    }
}
