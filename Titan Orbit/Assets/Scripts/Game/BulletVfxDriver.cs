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
    /// <para>
    /// [TITAN-ORBIT] Client-predicted impact: while tracers fly, swept-collide against hybrid-proxy
    /// spheres (<see cref="BulletCosmeticHitQuery"/>) so cosmetics stop at the rock/ship surface
    /// immediately. <see cref="BulletHitRpc"/> then reconciles (skip duplicate impact / destroy late
    /// misses). Damage / HP are never written here.
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
            /// <summary>[TITAN-ORBIT] Weapon mount index — volley adopt / duplicate gate use this.</summary>
            public int MountIndex;
            public bool IsDisplaySpace;
            public bool IsAnticipation;
            /// <summary>Monotonic fire order for FIFO adopt (RemoveAtSwap shuffles list indices).</summary>
            public int AnticipationOrder;
            public ClientBulletStretchVisual Stretch;
        }

        /// <summary>
        /// Server spawn that should not create a new tracer because anticipation already
        /// predicted-hit and was destroyed before <see cref="BulletSpawnRpc"/> arrived.
        /// </summary>
        struct PendingPredictedAdoptSkip
        {
            public int OwnerNetworkId;
            public int MountIndex;
            public byte OwnerTeam;
            public float ExpireTime;
            public float3 HitDisplayPos;
            public float Damage;
            public int BankIndex;
            public float ScaleMultiplier;
        }

        /// <summary>Recent cosmetic impact used to suppress a late HitRpc double-flash.</summary>
        struct RecentPredictedImpact
        {
            public float3 DisplayPos;
            public byte OwnerTeam;
            public float ExpireTime;
        }

        /// <summary>
        /// Cosmetic Instantiates per frame (not GhostSpawn). Allow a few so high fire-rate
        /// anticipation does not sit in the queue with stale muzzle poses while flying.
        /// </summary>
        const int MaxSpawnsPerFrame = 8;

        /// <summary>How long a predicted Sequence suppresses a duplicate HitRpc impact.</summary>
        const float PredictedHitTtlSeconds = 2f;

        /// <summary>How long a mount skip waits for the matching server spawn after anticipation predicted-hit.</summary>
        const float PredictedAdoptSkipTtlSeconds = 1.25f;

        /// <summary>Display-space radius for matching HitRpc to a recent predicted impact (no Sequence yet).</summary>
        const float PredictedImpactMatchRadius = 14f;

        readonly List<Tracer> _tracers = new List<Tracer>(64);
        readonly Dictionary<uint, int> _indexBySequence = new Dictionary<uint, int>(64);

        /// <summary>Sequences that already played client-predicted impact — HitRpc must not flash again.</summary>
        readonly HashSet<uint> _clientPredictedHitSequences = new HashSet<uint>();
        readonly Queue<(uint Sequence, float ExpireTime)> _clientPredictedHitExpiry =
            new Queue<(uint, float)>(64);

        /// <summary>Anticipation predicted-hit before adopt — next SpawnRpc for this mount is consumed silently.</summary>
        readonly List<PendingPredictedAdoptSkip> _pendingPredictedAdoptSkips =
            new List<PendingPredictedAdoptSkip>(8);

        /// <summary>Display impacts for HitRpc dedupe when Sequence was never bound (anticipation-only).</summary>
        readonly List<RecentPredictedImpact> _recentPredictedImpacts = new List<RecentPredictedImpact>(16);

        BulletVfxBank _bank;
        int _lastTickFrame = -1;
        /// <summary>Increments per anticipation CreateTracer so FIFO adopt survives RemoveAtSwap.</summary>
        int _nextAnticipationOrder;

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
            BulletCosmeticHitQuery.Clear();
            _indexBySequence.Clear();
            _clientPredictedHitSequences.Clear();
            _clientPredictedHitExpiry.Clear();
            _pendingPredictedAdoptSkips.Clear();
            _recentPredictedImpacts.Clear();
        }

        /// <summary>
        /// LateUpdate: drain spawns/hits, dead-reckon, cosmetic collide, place GOs.
        /// Never Instantiates in onBeforeRender.
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

            PrunePredictedReconcileState();

            if (!blockInstantiates)
                DrainSpawns();

            DrainHits();

            if (_tracers.Count == 0)
                return;

            float dt = math.min(0.05f, math.max(0f, Time.deltaTime));
            bool hasRef = ToroidalDisplay.TryGetReferencePosition(out Vector3 reference);

            // --- Cosmetic hit prediction (hybrid-proxy spheres; no map ToEntityArray) ---
            // Skip while join Instantiates are incomplete — HitRpc still destroys tracers.
            bool canPredictHits = !blockInstantiates && BulletCosmeticHitQuery.TryRefresh();

            for (int i = _tracers.Count - 1; i >= 0; i--)
            {
                var t = _tracers[i];
                if (t.Go == null)
                {
                    RemoveAtSwap(i);
                    continue;
                }

                // --- Advance logical / display flight ---
                float3 prevPos = t.LogicalPos;
                t.RemainingLifetime -= dt;
                float step = math.length(t.Velocity) * dt;
                t.Traveled += step;
                t.LogicalPos += t.Velocity * dt;

                // --- Client-predicted impact before lifetime cull ---
                // [TITAN-ORBIT] Destroy tracer at the rock surface now; server HitRpc reconciles later.
                if (canPredictHits &&
                    TryPredictCosmeticHit(in t, prevPos, t.LogicalPos, out float3 hitPoint))
                {
                    ApplyPredictedHit(i, in t, hitPoint);
                    continue;
                }

                if (t.RemainingLifetime <= 0f || t.Traveled >= math.max(0.5f, t.MaxDistance))
                {
                    DestroyTracerGo(t);
                    RemoveAtSwap(i);
                    continue;
                }

                // --- Toroidal display unwrap (logical sim → nearest tile to local ship) ---
                // Keep mount-height Y — ToDisplayPosition helpers are XZ-only; restore after unwrap.
                float mountY = t.LogicalPos.y;
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

                displayPos.y = mountY;
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
                // --- Consume server spawn when anticipation already predicted-hit this mount ---
                // [TITAN-ORBIT] Without this, SpawnRpc Instantiates a twin that tunnels after the
                // early impact destroyed Sequence=0 — looks like a second bullet ghosting through.
                if (!req.IsAnticipation && req.Sequence != 0 &&
                    TryConsumePredictedAdoptSkip(in req))
                {
                    spawned++;
                    continue;
                }

                // --- Adopt local anticipation when server spawn arrives (no pose snap) ---
                // Prevents a second tracer: orphan Sequence=0 cosmetics keep flying through rocks
                // after HitRpc kills only the sequenced server twin.
                if (!req.IsAnticipation && req.Sequence != 0 &&
                    TryAdoptAnticipation(req))
                {
                    spawned++;
                    continue;
                }

                // --- Local owner: drop redundant anticipation if THAT muzzle already has a fresh tracer ---
                // [TITAN-ORBIT] Mount-aware — a flying bullet from mount 0 must not kill anticipations
                // for mounts 1–3 in the same multi-cannon volley.
                if (req.IsAnticipation &&
                    BulletMuzzlePresentation.IsLocalOwner(req.OwnerNetworkId) &&
                    HasFreshLocalPresentationTracer(req.OwnerNetworkId, req.MountIndex))
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
        /// True when a non-anticipation local tracer for this owner+mount just spawned (presentation-locked).
        /// Used to drop duplicate anticipation after a reprojected server spawn for the same barrel.
        /// </summary>
        bool HasFreshLocalPresentationTracer(int ownerNetworkId, int mountIndex)
        {
            for (int i = 0; i < _tracers.Count; i++)
            {
                var t = _tracers[i];
                if (t.IsAnticipation || t.OwnerNetworkId != ownerNetworkId)
                    continue;
                if (t.MountIndex != mountIndex)
                    continue;
                if (t.IsDisplaySpace && t.Traveled < 2f)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Impact VFX + destroy matching tracer by Sequence.
        /// Falls back to nearest same-team tracer (incl. Sequence=0 anticipation) so orphan
        /// cosmetics do not keep flying through the rock after a real server hit.
        /// Skips duplicate flash when client already predicted this Sequence / nearby impact.
        /// </summary>
        void DrainHits()
        {
            EnsureBank();
            while (BulletVfxBridge.TryDequeueHit(out var hit))
            {
                Vector3 hitPos = hit.HitPosition;
                if (ToroidalDisplay.TryGetReferencePosition(out var reference))
                    hitPos = ToroidalDisplay.ToDisplayPosition(hitPos, reference);
                hitPos.y = 0f;

                // --- Reconcile: client already showed impact for this Sequence ---
                if (hit.Sequence != 0 && _clientPredictedHitSequences.Remove(hit.Sequence))
                {
                    // Tracer already destroyed; still cull stale anticipation leftovers.
                    ClearStaleAnticipationTracers(hit.OwnerTeam);
                    continue;
                }

                // --- Reconcile: anticipation predicted-hit before Sequence was bound ---
                // Only suppresses the duplicate flash — still try to destroy any leftover tracer.
                bool suppressImpactVfx = TryConsumeRecentPredictedImpact(hitPos, hit.OwnerTeam);

                if (!suppressImpactVfx)
                {
                    var team = (TeamId)hit.OwnerTeam;
                    int bankIndex = math.max(0, hit.BankIndex);
                    float scaleMul = hit.ScaleMultiplier > 0f ? hit.ScaleMultiplier : 1f;
                    BulletVisualFactory.SpawnBulletImpactVfx(
                        hitPos, _bank, bankIndex, team, hit.Damage, scaleMul);
                }

                // --- Preferred: exact Sequence from server ---
                if (_indexBySequence.TryGetValue(hit.Sequence, out int idx) &&
                    idx >= 0 && idx < _tracers.Count)
                {
                    DestroyTracerGo(_tracers[idx]);
                    RemoveAtSwap(idx);
                    // [TITAN-ORBIT] Cull only far leftover anticipations (energy-lag overfire).
                    // Do NOT wipe the whole team — a multi-cannon volley has sibling tracers that
                    // are still valid when one muzzle's bullet hits first.
                    ClearStaleAnticipationTracers(hit.OwnerTeam);
                    continue;
                }

                // --- Fallback: nearest same-team tracer near the impact (orphan anticipation) ---
                // [TITAN-ORBIT] Adopt can miss when OwnerNetworkId was 0 on enqueue; HitRpc still
                // has a Sequence the client never bound — without this, the cosmetic tunnels.
                if (TryFindNearestTracerIndex(hitPos, hit.OwnerTeam, maxDistance: 12f, out int nearIdx))
                {
                    DestroyTracerGo(_tracers[nearIdx]);
                    RemoveAtSwap(nearIdx);
                }
                else if (suppressImpactVfx)
                {
                    // Anticipation already gone — nothing left to destroy.
                    ClearStaleAnticipationTracers(hit.OwnerTeam);
                }
            }
        }

        /// <summary>
        /// Binds server Sequence / lifetime / bank onto an existing anticipation tracer.
        /// [TITAN-ORBIT] Does <b>not</b> relocate to lagged server SpawnPosition — keeps presentation muzzle flight.
        /// Owner match is loose so NetworkId=0 anticipation still adopts local server spawns.
        /// </summary>
        bool TryAdoptAnticipation(in BulletVfxBridge.SpawnRequest req)
        {
            // --- FIFO adopt (oldest AnticipationOrder for this owner + mount) ---
            // [TITAN-ORBIT] Prefer matching MountIndex so a 4-gun volley binds each server Sequence
            // onto the anticipation that left that barrel. Fall back to any mount if none match
            // (older clients / missing MountIndex on RPC).
            if (!TryFindAdoptIndex(req, preferMountMatch: true, out int bestIndex))
                TryFindAdoptIndex(req, preferMountMatch: false, out bestIndex);

            if (bestIndex < 0)
                return false;

            var adopted = _tracers[bestIndex];
            // --- Bind authority metadata + server flight direction ---
            // [TITAN-ORBIT] Keep presentation position (no muzzle snap) but take server Velocity.
            adopted.Sequence = req.Sequence;
            adopted.IsAnticipation = false;
            adopted.OwnerNetworkId = req.OwnerNetworkId > 0 ? req.OwnerNetworkId : adopted.OwnerNetworkId;
            adopted.MountIndex = req.MountIndex;
            adopted.Velocity = req.Velocity;
            adopted.BankIndex = req.BankIndex;
            adopted.ScaleMultiplier = req.ScaleMultiplier > 0f ? req.ScaleMultiplier : adopted.ScaleMultiplier;
            adopted.Damage = req.Damage;
            adopted.RemainingLifetime = math.max(0.05f, req.Lifetime);
            adopted.MaxDistance = math.max(0.5f, req.MaxDistance);
            // Do not reset Traveled / LogicalPos — stretch/trail continue from presentation muzzle.

            _tracers[bestIndex] = adopted;
            _indexBySequence[req.Sequence] = bestIndex;
            // Anticipation slot consumed — frees a Cap for ClientLocalBulletVfxBridge.
            BulletVfxBridge.NotifyAnticipationConsumed();
            return true;
        }

        /// <summary>
        /// Finds the oldest anticipation tracer for adopt, optionally requiring MountIndex match.
        /// </summary>
        bool TryFindAdoptIndex(in BulletVfxBridge.SpawnRequest req, bool preferMountMatch, out int bestIndex)
        {
            bestIndex = -1;
            int bestOrder = int.MaxValue;
            for (int i = 0; i < _tracers.Count; i++)
            {
                var t = _tracers[i];
                if (!t.IsAnticipation || t.Sequence != 0)
                    continue;
                if (!OwnersMatchForAdopt(t.OwnerNetworkId, req.OwnerNetworkId))
                    continue;
                if (preferMountMatch && t.MountIndex != req.MountIndex)
                    continue;
                if (t.AnticipationOrder >= bestOrder)
                    continue;

                bestOrder = t.AnticipationOrder;
                bestIndex = i;
            }

            return bestIndex >= 0;
        }

        /// <summary>
        /// Destroys far-traveled still-pending anticipation tracers for a team.
        /// Fresh volley siblings (near the muzzle) are kept so multi-cannon cosmetics survive
        /// when one barrel scores a hit first.
        /// </summary>
        void ClearStaleAnticipationTracers(byte ownerTeam)
        {
            // Beyond this travel, an unadopted anticipation is almost certainly a leftover from
            // energy-lag overfire — safe to cull without killing a just-fired volley mate.
            const float StaleTravel = 8f;

            for (int i = _tracers.Count - 1; i >= 0; i--)
            {
                var t = _tracers[i];
                if (!t.IsAnticipation || t.Sequence != 0)
                    continue;
                if (t.OwnerTeam != ownerTeam)
                    continue;
                if (t.Traveled < StaleTravel)
                    continue;

                DestroyTracerGo(t);
                RemoveAtSwap(i);
            }
        }

        /// <summary>
        /// True when spawn/anticipation owners refer to the same ship (incl. id-not-ready edge cases).
        /// </summary>
        static bool OwnersMatchForAdopt(int anticipationOwnerId, int serverOwnerId)
        {
            // --- Both ids known ---
            if (anticipationOwnerId > 0 && serverOwnerId > 0)
                return anticipationOwnerId == serverOwnerId;

            // --- One id missing: only adopt when the known id is the local player ---
            // Prevents binding local Sequence=0 cosmetics onto a remote spawn with OwnerNetworkId=0.
            int known = anticipationOwnerId > 0 ? anticipationOwnerId : serverOwnerId;
            if (known > 0)
                return BulletMuzzlePresentation.IsLocalOwner(known);

            // Both missing — anticipation is local-only; allow adopt.
            return true;
        }

        /// <summary>
        /// Finds the tracer GameObject closest to a display-space impact for the firing team.
        /// Used when HitRpc Sequence was never bound (orphan anticipation).
        /// </summary>
        bool TryFindNearestTracerIndex(Vector3 hitDisplayPos, byte ownerTeam, float maxDistance, out int index)
        {
            index = -1;
            float bestDistSq = maxDistance * maxDistance;
            float3 hit = new float3(hitDisplayPos.x, 0f, hitDisplayPos.z);

            for (int i = 0; i < _tracers.Count; i++)
            {
                var t = _tracers[i];
                if (t.OwnerTeam != ownerTeam || t.Go == null)
                    continue;

                Vector3 p = t.Go.transform.position;
                float distSq = math.distancesq(new float3(p.x, 0f, p.z), hit);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    index = i;
                }
            }

            return index >= 0;
        }

        void CreateTracer(in BulletVfxBridge.SpawnRequest req)
        {
            EnsureBank();
            var team = (TeamId)req.OwnerTeam;
            int bankIndex = math.max(0, req.BankIndex);
            float scaleMul = req.ScaleMultiplier > 0f ? req.ScaleMultiplier : 1f;
            float bulletSpeed = math.length(req.Velocity);

            Vector3 spawnDisplay = req.SpawnPosition;
            float mountY = req.SpawnPosition.y;
            if (!req.IsDisplaySpace && ToroidalDisplay.TryGetReferencePosition(out var reference))
                spawnDisplay = ToroidalDisplay.ToDisplayPosition(req.SpawnPosition, reference);
            // Keep weapon-mount height (display unwrap is XZ-only).
            spawnDisplay.y = mountY;

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
                MountIndex = req.MountIndex < 0 ? 0 : req.MountIndex,
                IsDisplaySpace = req.IsDisplaySpace,
                IsAnticipation = req.IsAnticipation,
                // Only anticipations need order; server-only tracers keep 0.
                AnticipationOrder = req.IsAnticipation ? _nextAnticipationOrder++ : 0,
                Stretch = stretch,
            };

            _tracers.Add(tracer);
            if (req.Sequence != 0)
                _indexBySequence[req.Sequence] = _tracers.Count - 1;
            else if (req.IsAnticipation)
                BulletVfxBridge.NotifyAnticipationCreated();
        }

        void EnsureBank()
        {
            if (_bank == null)
                _bank = BulletVfxBank.LoadDefault();
        }

        /// <summary>
        /// Swept cosmetic collide for one tracer step using <see cref="BulletCosmeticHitQuery"/>.
        /// </summary>
        static bool TryPredictCosmeticHit(
            in Tracer t,
            float3 from,
            float3 to,
            out float3 hitPoint)
        {
            return BulletCosmeticHitQuery.TryHitSegment(
                from,
                to,
                t.OwnerTeam,
                t.OwnerNetworkId,
                t.IsDisplaySpace,
                out hitPoint);
        }

        /// <summary>
        /// Plays impact VFX, records reconcile keys, destroys the tracer.
        /// Does not write damage — server HitRpc / sim still owns HP.
        /// </summary>
        void ApplyPredictedHit(int tracerIndex, in Tracer t, float3 hitPoint)
        {
            EnsureBank();

            // --- Display-space impact position for the VFX prefab ---
            Vector3 hitDisplay = hitPoint;
            if (!t.IsDisplaySpace && ToroidalDisplay.TryGetReferencePosition(out var reference))
                hitDisplay = ToroidalDisplay.ToDisplayPosition(hitPoint, reference);
            hitDisplay.y = 0f;

            var team = (TeamId)t.OwnerTeam;
            int bankIndex = math.max(0, t.BankIndex);
            float scaleMul = t.ScaleMultiplier > 0f ? t.ScaleMultiplier : 1f;
            BulletVisualFactory.SpawnBulletImpactVfx(
                hitDisplay, _bank, bankIndex, team, t.Damage, scaleMul);

            // --- Remember for HitRpc / SpawnRpc reconcile ---
            float now = Time.unscaledTime;
            if (t.Sequence != 0)
                RememberPredictedSequence(t.Sequence, now);
            else if (t.IsAnticipation)
            {
                // Anticipation died before adopt — next SpawnRpc for this mount must not twin.
                _pendingPredictedAdoptSkips.Add(new PendingPredictedAdoptSkip
                {
                    OwnerNetworkId = t.OwnerNetworkId,
                    MountIndex = t.MountIndex,
                    OwnerTeam = t.OwnerTeam,
                    ExpireTime = now + PredictedAdoptSkipTtlSeconds,
                    HitDisplayPos = hitDisplay,
                    Damage = t.Damage,
                    BankIndex = bankIndex,
                    ScaleMultiplier = scaleMul,
                });
            }

            _recentPredictedImpacts.Add(new RecentPredictedImpact
            {
                DisplayPos = hitDisplay,
                OwnerTeam = t.OwnerTeam,
                ExpireTime = now + PredictedHitTtlSeconds,
            });

            DestroyTracerGo(t);
            RemoveAtSwap(tracerIndex);
        }

        /// <summary>
        /// When anticipation already predicted-hit, bind the arriving server Sequence into the
        /// predicted-hit set and skip CreateTracer (no twin).
        /// </summary>
        bool TryConsumePredictedAdoptSkip(in BulletVfxBridge.SpawnRequest req)
        {
            float now = Time.unscaledTime;
            for (int i = 0; i < _pendingPredictedAdoptSkips.Count; i++)
            {
                var skip = _pendingPredictedAdoptSkips[i];
                if (now > skip.ExpireTime)
                    continue;
                if (!OwnersMatchForAdopt(skip.OwnerNetworkId, req.OwnerNetworkId))
                    continue;
                if (skip.MountIndex != req.MountIndex)
                    continue;

                // Bind server Sequence so the later HitRpc reconciles cleanly.
                RememberPredictedSequence(req.Sequence, now);
                // Drop the spatial recent-impact entry — Sequence owns reconcile from here.
                TryConsumeRecentPredictedImpact(skip.HitDisplayPos, skip.OwnerTeam);
                _pendingPredictedAdoptSkips.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>Records a sequenced predicted hit for HitRpc dedupe.</summary>
        void RememberPredictedSequence(uint sequence, float now)
        {
            if (sequence == 0)
                return;
            if (_clientPredictedHitSequences.Add(sequence))
                _clientPredictedHitExpiry.Enqueue((sequence, now + PredictedHitTtlSeconds));
        }

        /// <summary>
        /// True when a recent predicted impact sits near this HitRpc display position
        /// (anticipation predicted before Sequence was known).
        /// </summary>
        bool TryConsumeRecentPredictedImpact(Vector3 hitDisplayPos, byte ownerTeam)
        {
            float now = Time.unscaledTime;
            float maxDistSq = PredictedImpactMatchRadius * PredictedImpactMatchRadius;
            float3 hit = new float3(hitDisplayPos.x, 0f, hitDisplayPos.z);

            for (int i = _recentPredictedImpacts.Count - 1; i >= 0; i--)
            {
                var recent = _recentPredictedImpacts[i];
                if (now > recent.ExpireTime)
                {
                    _recentPredictedImpacts.RemoveAt(i);
                    continue;
                }

                if (recent.OwnerTeam != ownerTeam)
                    continue;

                float distSq = math.distancesq(recent.DisplayPos, hit);
                if (distSq > maxDistSq)
                    continue;

                _recentPredictedImpacts.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>Expires stale predicted-hit reconcile entries so HashSets cannot grow forever.</summary>
        void PrunePredictedReconcileState()
        {
            float now = Time.unscaledTime;

            while (_clientPredictedHitExpiry.Count > 0)
            {
                var head = _clientPredictedHitExpiry.Peek();
                if (now <= head.ExpireTime)
                    break;
                _clientPredictedHitExpiry.Dequeue();
                _clientPredictedHitSequences.Remove(head.Sequence);
            }

            for (int i = _pendingPredictedAdoptSkips.Count - 1; i >= 0; i--)
            {
                if (now > _pendingPredictedAdoptSkips[i].ExpireTime)
                    _pendingPredictedAdoptSkips.RemoveAt(i);
            }

            for (int i = _recentPredictedImpacts.Count - 1; i >= 0; i--)
            {
                if (now > _recentPredictedImpacts[i].ExpireTime)
                    _recentPredictedImpacts.RemoveAt(i);
            }
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
            // Orphan anticipation destroyed without adopt — free the cap slot.
            else if (removed.IsAnticipation)
                BulletVfxBridge.NotifyAnticipationConsumed();

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
