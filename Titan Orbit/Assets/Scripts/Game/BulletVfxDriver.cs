using System.Collections.Generic;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Owns bullet muzzle / tracer / impact GameObjects for all ships.
    /// <para>
    /// Server remains authoritative (<see cref="BulletSimulationSystem"/>). This driver only
    /// Instantiates cosmetics from <see cref="BulletVfxBridge"/> (host in-process +
    /// <see cref="BulletSpawnRpc"/> / <see cref="BulletHitRpc"/>). Windows-safe: no map-body
    /// <c>ToEntityArray</c>; Instantiates gated while Settling / GhostSpawnBacklog.
    /// TransformQuarantine is OK (session-long) — same pattern as <see cref="PeopleTransportVfxDriver"/>.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(66150)]
    public class BulletVfxDriver : MonoBehaviour
    {
        /// <summary>One active cosmetic tracer keyed by server Sequence (or anticipation slot).</summary>
        struct Tracer
        {
            public GameObject Go;
            public uint Sequence;
            public int OwnerNetworkId;
            public float3 LogicalPos;
            public float3 SpawnPos;
            public float3 Velocity;
            public float RemainingLifetime;
            public float MaxDistance;
            public float Traveled;
            public float Damage;
            public byte OwnerTeam;
            public int BankIndex;
            public float ScaleMultiplier;
            public bool IsDisplaySpace;
            public bool IsAnticipation;
            public ClientBulletStretchVisual Stretch;
        }

        /// <summary>Windows: Instantiates 1/frame — same discipline as GhostSpawn Instantiates cap.</summary>
        const int MaxSpawnsPerFrame = 1;

        readonly List<Tracer> _tracers = new List<Tracer>(64);
        readonly Dictionary<uint, int> _indexBySequence = new Dictionary<uint, int>(64);

        BulletVfxBank _bank;
        int _lastTickFrame = -1;

        /// <summary>[UNITY] Attach to session manager when the scene loads.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
            if (FindAnyObjectByType<BulletVfxDriver>() != null)
                return;

            var session = FindAnyObjectByType<TitanOrbitSessionManager>();
            if (session != null)
            {
                session.gameObject.AddComponent<BulletVfxDriver>();
                return;
            }

            var go = new GameObject("BulletVfxDriver");
            DontDestroyOnLoad(go);
            go.AddComponent<BulletVfxDriver>();
        }

        void OnDisable()
        {
            ClearAllTracers();
            BulletVfxBridge.Clear();
            _indexBySequence.Clear();
        }

        /// <summary>
        /// LateUpdate: drain spawns/hits, dead-reckon, place GOs. Never Instantiates in onBeforeRender.
        /// </summary>
        void LateUpdate()
        {
            if (_lastTickFrame == Time.frameCount)
                return;
            _lastTickFrame = Time.frameCount;

            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            // [TITAN-ORBIT] Skip Instantiates during join Instantiates storm / post–Join Team backlog.
            // TransformQuarantine stays on for the session — that alone must NOT block bullets.
            bool blockInstantiates = ClientJoinSettleCache.Settling || ClientJoinSettleCache.GhostSpawnBacklog;

            if (!blockInstantiates)
                DrainSpawns();

            DrainHits();

            if (_tracers.Count == 0)
                return;

            float dt = math.min(0.05f, math.max(0f, Time.deltaTime));
            bool hasRef = ToroidalDisplay.TryGetReferencePosition(out Vector3 reference);

            for (int i = _tracers.Count - 1; i >= 0; i--)
            {
                var t = _tracers[i];
                if (t.Go == null)
                {
                    RemoveAtSwap(i);
                    continue;
                }

                t.RemainingLifetime -= dt;
                float step = math.length(t.Velocity) * dt;
                t.Traveled += step;
                t.LogicalPos += t.Velocity * dt;

                if (t.RemainingLifetime <= 0f || t.Traveled >= math.max(0.5f, t.MaxDistance))
                {
                    DestroyTracerGo(t);
                    RemoveAtSwap(i);
                    continue;
                }

                // --- Toroidal display unwrap (logical sim → nearest tile to local ship) ---
                Vector3 displayPos;
                if (t.IsDisplaySpace)
                    displayPos = t.LogicalPos;
                else if (hasRef)
                {
                    int stableKey = unchecked((int)t.Sequence) ^ (t.OwnerNetworkId * 397);
                    displayPos = ToroidalDisplay.ToDisplayPositionWithHysteresis(
                        stableKey, t.LogicalPos, reference);
                    // Seam retile can yank the GO — clear TrailRenderer to avoid stretched spikes.
                    Vector3 prevDisplay = t.Go.transform.position;
                    if ((displayPos - prevDisplay).sqrMagnitude > 40f * 40f)
                        ResetTrail(t.Go);
                }
                else
                    displayPos = t.LogicalPos;

                displayPos.y = 0f;
                t.Go.transform.position = displayPos;
                if (math.lengthsq(t.Velocity) > 0.0001f)
                    t.Go.transform.rotation = Quaternion.LookRotation(((Vector3)t.Velocity).normalized, Vector3.up);

                if (t.Stretch != null)
                {
                    float progress = t.Traveled / math.max(0.5f, t.MaxDistance);
                    t.Stretch.ApplyTravelProgress(progress);
                }

                _tracers[i] = t;
            }
        }

        /// <summary>Creates tracers from the bridge (budgeted Instantiates).</summary>
        void DrainSpawns()
        {
            EnsureBank();
            int spawned = 0;
            while (spawned < MaxSpawnsPerFrame && BulletVfxBridge.TryDequeueSpawn(out var req))
            {
                // --- Adopt local anticipation when server spawn arrives ---
                if (!req.IsAnticipation && req.Sequence != 0 &&
                    TryAdoptAnticipation(req))
                {
                    spawned++;
                    continue;
                }

                // --- Host: server bridge often wins the race — drop redundant anticipation ---
                if (req.IsAnticipation && HasFreshServerTracerForOwner(req.OwnerNetworkId))
                    continue;

                CreateTracer(req);
                spawned++;
            }
        }

        /// <summary>
        /// True when a server-authored tracer for this owner just spawned (Traveled near zero).
        /// Prevents host double-muzzle when sim enqueue beats LateUpdate anticipation.
        /// </summary>
        bool HasFreshServerTracerForOwner(int ownerNetworkId)
        {
            for (int i = 0; i < _tracers.Count; i++)
            {
                var t = _tracers[i];
                if (t.IsAnticipation || t.OwnerNetworkId != ownerNetworkId)
                    continue;
                if (t.Traveled < 2f)
                    return true;
            }

            return false;
        }

        /// <summary>Impact VFX + destroy matching tracer by Sequence.</summary>
        void DrainHits()
        {
            EnsureBank();
            while (BulletVfxBridge.TryDequeueHit(out var hit))
            {
                Vector3 hitPos = hit.HitPosition;
                if (ToroidalDisplay.TryGetReferencePosition(out var reference))
                    hitPos = ToroidalDisplay.ToDisplayPosition(hitPos, reference);
                hitPos.y = 0f;

                var team = (TeamId)hit.OwnerTeam;
                int bankIndex = math.max(0, hit.BankIndex);
                float scaleMul = hit.ScaleMultiplier > 0f ? hit.ScaleMultiplier : 1f;
                BulletVisualFactory.SpawnBulletImpactVfx(hitPos, _bank, bankIndex, team, hit.Damage, scaleMul);

                if (_indexBySequence.TryGetValue(hit.Sequence, out int idx) &&
                    idx >= 0 && idx < _tracers.Count)
                {
                    DestroyTracerGo(_tracers[idx]);
                    RemoveAtSwap(idx);
                }
            }
        }

        /// <summary>
        /// If local anticipation fired recently for the same owner, bind the server Sequence to it
        /// instead of spawning a second tracer.
        /// </summary>
        bool TryAdoptAnticipation(in BulletVfxBridge.SpawnRequest req)
        {
            for (int i = 0; i < _tracers.Count; i++)
            {
                var t = _tracers[i];
                if (!t.IsAnticipation || t.Sequence != 0)
                    continue;
                if (t.OwnerNetworkId != req.OwnerNetworkId)
                    continue;

                t.Sequence = req.Sequence;
                t.IsAnticipation = false;
                t.IsDisplaySpace = false;
                t.BankIndex = req.BankIndex;
                t.ScaleMultiplier = req.ScaleMultiplier > 0f ? req.ScaleMultiplier : t.ScaleMultiplier;
                t.Damage = req.Damage;
                // Keep current visual pose; snap logical to server for remaining flight.
                t.LogicalPos = req.SpawnPosition;
                t.SpawnPos = req.SpawnPosition;
                t.Velocity = req.Velocity;
                t.RemainingLifetime = req.Lifetime;
                t.MaxDistance = req.MaxDistance;
                t.Traveled = 0f;
                _tracers[i] = t;
                _indexBySequence[req.Sequence] = i;
                return true;
            }

            return false;
        }

        void CreateTracer(in BulletVfxBridge.SpawnRequest req)
        {
            EnsureBank();
            var team = (TeamId)req.OwnerTeam;
            int bankIndex = math.max(0, req.BankIndex);
            float scaleMul = req.ScaleMultiplier > 0f ? req.ScaleMultiplier : 1f;
            float bulletSpeed = math.length(req.Velocity);

            Vector3 spawnDisplay = req.SpawnPosition;
            if (!req.IsDisplaySpace && ToroidalDisplay.TryGetReferencePosition(out var reference))
                spawnDisplay = ToroidalDisplay.ToDisplayPosition(req.SpawnPosition, reference);
            spawnDisplay.y = 0f;

            // --- Muzzle flash at fire origin ---
            BulletVisualFactory.PlayMuzzleVfx(
                spawnDisplay,
                req.Velocity,
                _bank,
                bankIndex,
                team,
                scaleMul,
                bulletSpeed);
            AudioManager.Instance?.PlayWeaponShootSound(
                BulletVisualFactory.GetProjectileSoundPitchBySpeed(bulletSpeed));

            var go = new GameObject("BulletTracer");
            go.transform.position = spawnDisplay;
            if (math.lengthsq(req.Velocity) > 0.0001f)
                go.transform.rotation = Quaternion.LookRotation(((Vector3)req.Velocity).normalized, Vector3.up);

            GameObject visual = BulletVisualFactory.BuildVisual(
                go.transform,
                _bank,
                bankIndex,
                team,
                BulletShape.Sphere,
                scaleMul,
                bulletSpeed,
                noTrail: false);

            ClientBulletStretchVisual stretch = null;
            if (_bank != null
                && _bank.TryGetProfile(bankIndex, out var profile)
                && profile != null
                && profile.TryGetStretchLengthFactors(out float startFactor, out float endFactor)
                && ClientBulletStretchVisual.TryAttach(go.transform, visual, startFactor, endFactor))
            {
                stretch = go.GetComponent<ClientBulletStretchVisual>();
            }

            var tracer = new Tracer
            {
                Go = go,
                Sequence = req.Sequence,
                OwnerNetworkId = req.OwnerNetworkId,
                LogicalPos = req.SpawnPosition,
                SpawnPos = req.SpawnPosition,
                Velocity = req.Velocity,
                RemainingLifetime = math.max(0.1f, req.Lifetime),
                MaxDistance = math.max(0.5f, req.MaxDistance),
                Traveled = 0f,
                Damage = req.Damage,
                OwnerTeam = req.OwnerTeam,
                BankIndex = bankIndex,
                ScaleMultiplier = scaleMul,
                IsDisplaySpace = req.IsDisplaySpace,
                IsAnticipation = req.IsAnticipation,
                Stretch = stretch,
            };

            _tracers.Add(tracer);
            if (req.Sequence != 0)
                _indexBySequence[req.Sequence] = _tracers.Count - 1;
        }

        void EnsureBank()
        {
            if (_bank == null)
                _bank = BulletVfxBank.LoadDefault();
        }

        static void ResetTrail(GameObject go)
        {
            if (go == null) return;
            var trails = go.GetComponentsInChildren<TrailRenderer>(true);
            for (int i = 0; i < trails.Length; i++)
            {
                if (trails[i] != null)
                    trails[i].Clear();
            }
        }

        static void DestroyTracerGo(in Tracer t)
        {
            if (t.Go != null)
                Object.Destroy(t.Go);
        }

        void RemoveAtSwap(int index)
        {
            var removed = _tracers[index];
            if (removed.Sequence != 0)
                _indexBySequence.Remove(removed.Sequence);

            int last = _tracers.Count - 1;
            if (index != last)
            {
                var moved = _tracers[last];
                _tracers[index] = moved;
                if (moved.Sequence != 0)
                    _indexBySequence[moved.Sequence] = index;
            }

            _tracers.RemoveAt(last);
        }

        void ClearAllTracers()
        {
            for (int i = 0; i < _tracers.Count; i++)
                DestroyTracerGo(_tracers[i]);
            _tracers.Clear();
        }
    }
}
