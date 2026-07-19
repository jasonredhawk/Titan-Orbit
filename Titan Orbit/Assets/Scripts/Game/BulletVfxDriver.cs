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
    /// <para>
    /// [TITAN-ORBIT] Starblast local fire: anticipation + reproject use predicted/presentation muzzle
    /// and <c>shipVel + aim * BulletSpeed</c>. Reproject is best-effort — if it fails (empty client
    /// mounts), server SpawnPosition/Velocity still Instantiates so the player never sees silent fire.
    /// Remotes keep server SpawnPosition.
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

        /// <summary>
        /// Cosmetic Instantiates per frame (not GhostSpawn). Allow a few so high fire-rate
        /// anticipation does not sit in the queue with stale muzzle poses while flying.
        /// </summary>
        const int MaxSpawnsPerFrame = 8;

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
        /// Runs after <see cref="ClientLocalBulletVfxBridge"/> (66100) so anticipation is queued first.
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
                // --- Adopt local anticipation when server spawn arrives (no pose snap) ---
                if (!req.IsAnticipation && req.Sequence != 0 &&
                    TryAdoptAnticipation(req))
                {
                    spawned++;
                    continue;
                }

                // --- Local owner: drop redundant anticipation if a presentation tracer already flies ---
                if (req.IsAnticipation &&
                    BulletMuzzlePresentation.IsLocalOwner(req.OwnerNetworkId) &&
                    HasFreshLocalPresentationTracer(req.OwnerNetworkId))
                    continue;

                // --- Starblast: reproject local muzzle/velocity at CreateTracer time (best-effort) ---
                // [TITAN-ORBIT] Never drop a server spawn when reproject fails — client ECS mounts are
                // often empty under hybrid/TransformQuarantine while the server still fires (energy +
                // hits work). Falling back to server pose/vel restores visible tracers; reproject
                // (ECS or GO mounts) still corrects feel when it succeeds.
                bool localShot = req.IsAnticipation ||
                                 BulletMuzzlePresentation.IsLocalOwner(req.OwnerNetworkId);
                if (localShot)
                    BulletMuzzlePresentation.TryReprojectLocalOwnerSpawn(ref req);

                CreateTracer(req);
                spawned++;
            }
        }

        /// <summary>
        /// True when a non-anticipation local tracer for this owner just spawned (presentation-locked).
        /// Used to drop duplicate anticipation after a reprojected server spawn.
        /// </summary>
        bool HasFreshLocalPresentationTracer(int ownerNetworkId)
        {
            for (int i = 0; i < _tracers.Count; i++)
            {
                var t = _tracers[i];
                if (t.IsAnticipation || t.OwnerNetworkId != ownerNetworkId)
                    continue;
                if (t.IsDisplaySpace && t.Traveled < 2f)
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
        /// Binds server Sequence / lifetime / bank onto an existing anticipation tracer.
        /// [TITAN-ORBIT] Does <b>not</b> relocate to lagged server SpawnPosition — keeps presentation muzzle flight.
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

                // --- Bind authority metadata only ---
                t.Sequence = req.Sequence;
                t.IsAnticipation = false;
                // Keep IsDisplaySpace / LogicalPos / SpawnPos / Velocity / Traveled / GO pose.
                t.BankIndex = req.BankIndex;
                t.ScaleMultiplier = req.ScaleMultiplier > 0f ? req.ScaleMultiplier : t.ScaleMultiplier;
                t.Damage = req.Damage;
                t.RemainingLifetime = math.max(0.05f, req.Lifetime);
                t.MaxDistance = math.max(0.5f, req.MaxDistance);
                // Do not reset Traveled — stretch/trail continue smoothly.

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
