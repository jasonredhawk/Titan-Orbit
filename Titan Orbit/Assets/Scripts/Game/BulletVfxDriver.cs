using System.Collections.Generic;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using Unity.Entities;
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
    /// spheres (<see cref="BulletCosmeticHitQuery"/>) so cosmetics stop at the rock / ship /
    /// planetary-defense turret surface immediately (no visual tunnel waiting for RTT).
    /// <see cref="BulletHitRpc"/> then reconciles (skip duplicate impact flash / destroy late
    /// misses) and applies authoritative mining floats via <c>AsteroidHealthAfter</c> — cosmetics
    /// never write damage / HP.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Sequence 0 HitRpcs are ram/grind explosions (no tracer). They must play
    /// impact VFX and must not adopt/destroy a nearby flying tracer.
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
            /// <summary>
            /// Seconds left before cosmetic despawn. When the spawn request Lifetime is ≤ 0
            /// (planetary defense), this is set to +∞ so only <see cref="MaxDistance"/> culls.
            /// </summary>
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
            /// <summary>[TITAN-ORBIT] Cosmetic collide filter (mining / fighter pass-through).</summary>
            public byte DamageFilter;
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

        /// <summary>
        /// Recent cosmetic impact used to suppress a late HitRpc double-flash.
        /// Stores OwnerNetworkId so mining floats can still apply after the tracer is gone.
        /// </summary>
        struct RecentPredictedImpact
        {
            public float3 DisplayPos;
            public byte OwnerTeam;
            /// <summary>Shooter NetworkId — used for HitRpc mining floats after the tracer is gone.</summary>
            public int OwnerNetworkId;
            public float ExpireTime;
        }

        /// <summary>
        /// Cosmetic Instantiates per frame (not GhostSpawn). Kept modest so fire-while-flying
        /// does not Instantiates a whole volley of tracer meshes in one LateUpdate
        /// (session 74383c: spawnMs ~16 ms at MaxSpawnsPerFrame=8 before muzzle pool warm).
        /// </summary>
        const int MaxSpawnsPerFrame = 3;

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
        /// <summary>OwnerNetworkId for each predicted Sequence (mining float after tracer destroy).</summary>
        readonly Dictionary<uint, int> _clientPredictedHitOwners = new Dictionary<uint, int>(64);
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
            _clientPredictedHitOwners.Clear();
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
            bool blockInstantiates = ClientJoinSettleCache.ShouldSkipShipEntityQueries;

            PrunePredictedReconcileState();

            // --- Resolve bank + queue prewarm jobs before budgeted Instantiates ---
            EnsureBank();

            // --- Budgeted VFX prewarm (spread Instantiates; keep combat warm) ---
            // [TITAN-ORBIT] 4 Sci-Fi VFX Instantiates/frame keeps load hitch off the kill path
            // (kill-impact-fix: sync 1204 Instantiates = spawnMs 531 ms).
            if (!BulletOneShotVfxPool.PrewarmComplete)
                BulletOneShotVfxPool.TickPrewarm(4);

            // --- Drain spawn Instantiates (muzzle + tracer visual) ---
            if (!blockInstantiates)
                DrainSpawns();

            // --- Drain HitRpc impacts (pooled one-shot VFX) + mining floats / gem burst ---
            DrainHits();

            // Return expired muzzle/impact shells so the next shot can Rent without Instantiates.
            BulletOneShotVfxPool.TickReturns();

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
                // [TITAN-ORBIT] Destroy tracer at the surface now so bullets do not visually tunnel
                // while waiting for BulletHitRpc RTT. Obstacles come only from hybrid proxies
                // (never a full asteroid gather — Windows late-join Crash!!!). Mining floats stay
                // on HitRpc (AsteroidHealthAfter) so optimistic HP cannot drift from authority.
                // Safe again after ShipWeaponPose presentation-scale fix aligned server muzzles
                // with client tracers / proxy rocks.
                if (canPredictHits &&
                    TryPredictCosmeticHit(
                        in t, prevPos, t.LogicalPos,
                        out float3 hitPoint,
                        out _,
                        out _))
                {
                    ApplyPredictedHit(i, in t, hitPoint);
                    continue;
                }

                // RemainingLifetime is +∞ for distance-only shots (PD Lifetime = 0).
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
        /// Skips duplicate impact flash when client already predicted this Sequence / nearby impact.
        /// Mining floats always use HitRpc <c>AsteroidHealthAfter</c> (never cosmetic-predicted HP).
        /// Planetary-defense HP bars use <c>PlanetaryDefenseHealthAfter</c> (ghost MaxSendRate lag).
        /// Sequence 0 (ram/grind) plays VFX only — never adopts a tracer.
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

                // --- Ram / grind: Sequence 0 means there is no tracer ---
                // [TITAN-ORBIT] Reusing BulletHitRpc so every client gets the ship's bullet
                // explosion scaled by ram damage. Nearest-tracer fallback would eat a live shot
                // if you ram while firing. Predicted-bullet suppress would hide the ram boom.
                if (hit.Sequence == 0)
                {
                    var ramTeam = (TeamId)hit.OwnerTeam;
                    int ramBank = math.max(0, hit.BankIndex);
                    float ramScale = hit.ScaleMultiplier > 0f ? hit.ScaleMultiplier : 1f;
                    BulletVisualFactory.SpawnBulletImpactVfx(
                        hitPos, _bank, ramBank, ramTeam, hit.Damage, ramScale);

                    var ramSynth = new Tracer { OwnerNetworkId = 0, IsAnticipation = false };
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, ramTeam,
                        hit.AsteroidHealthAfter, in ramSynth);
                    continue;
                }

                // --- Reconcile: client already showed impact for this Sequence ---
                // Tracer + impact VFX already done; still apply authoritative mining float / PD bar.
                if (hit.Sequence != 0 && _clientPredictedHitSequences.Remove(hit.Sequence))
                {
                    int ownerNetworkId = 0;
                    if (_clientPredictedHitOwners.TryGetValue(hit.Sequence, out int storedOwner))
                    {
                        ownerNetworkId = storedOwner;
                        _clientPredictedHitOwners.Remove(hit.Sequence);
                    }

                    var synth = new Tracer
                    {
                        OwnerNetworkId = ownerNetworkId,
                        IsAnticipation = false,
                    };
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, in synth);
                    TryNotifyPlanetaryDefenseHitRpc(in hit);
                    ClearStaleAnticipationTracers(hit.OwnerTeam);
                    continue;
                }

                // --- Reconcile: anticipation predicted-hit before Sequence was bound ---
                // Suppresses duplicate flash only — mining floats still run below.
                bool suppressImpactVfx = TryConsumeRecentPredictedImpact(
                    hitPos, hit.OwnerTeam, out int recentOwnerNetworkId);

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
                    var tracer = _tracers[idx];
                    // Always show float on HitRpc — even when VFX was client-predicted.
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, in tracer);
                    TryNotifyPlanetaryDefenseHitRpc(in hit);

                    DestroyTracerGo(tracer);
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
                    var nearTracer = _tracers[nearIdx];
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, in nearTracer);
                    TryNotifyPlanetaryDefenseHitRpc(in hit);

                    DestroyTracerGo(nearTracer);
                    RemoveAtSwap(nearIdx);
                }
                else if (suppressImpactVfx)
                {
                    // Anticipation already gone — still apply mining float with stored owner.
                    var synth = new Tracer
                    {
                        OwnerNetworkId = recentOwnerNetworkId,
                        IsAnticipation = true,
                    };
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, in synth);
                    TryNotifyPlanetaryDefenseHitRpc(in hit);
                    ClearStaleAnticipationTracers(hit.OwnerTeam);
                }
                else
                {
                    // No tracer to destroy — still punch PD HP bar / asteroid teardown from HitRpc.
                    var synth = new Tracer { OwnerNetworkId = 0, IsAnticipation = false };
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, in synth);
                    TryNotifyPlanetaryDefenseHitRpc(in hit);
                }
            }
        }

        /// <summary>
        /// Forwards server PD Health-after from <see cref="BulletHitRpc"/> into the hybrid
        /// turret HP bar so it dips immediately (planet ghost MaxSendRate otherwise lags).
        /// No-op when PlanetId is 0 (not a planetary-defense impact).
        /// </summary>
        /// <param name="hit">Dequeued HitRequest (already display-space for VFX position).</param>
        static void TryNotifyPlanetaryDefenseHitRpc(in BulletVfxBridge.HitRequest hit)
        {
            if (hit.PlanetaryDefensePlanetId <= 0)
                return;

            PlanetaryDefenseVisualDriver.NotifyAuthoritativeHit(
                hit.PlanetaryDefensePlanetId,
                hit.PlanetaryDefenseSlotIndex,
                hit.PlanetaryDefenseHealthAfter);
        }

        /// <summary>
        /// HitRpc path: local asteroid impact → +Damage and server-authored HP Left.
        /// <para>
        /// [TITAN-ORBIT] <paramref name="asteroidHealthAfter"/> comes from the server on
        /// <see cref="BulletHitRpc"/> — do not subtract from lagging ghost Health. Seed-hydrate
        /// asteroids are not ghosts: <see cref="BulletHitRpcClientSystem"/> writes Health /
        /// IsDestroyed; this path shows floats (local shots) and hides/tears down the hybrid GO
        /// on kill. Do not DestroyEntity here — sim-group soft-destroy owns ECS teardown.
        /// Non-asteroid hits pass &lt; 0 and skip mining floats entirely.
        /// </para>
        /// </summary>
        static void TryShowAsteroidFloatForHitRpc(
            Vector3 hitDisplayPos,
            float3 hitLogicalPos,
            float damage,
            TeamId ownerTeam,
            float asteroidHealthAfter,
            in Tracer tracer)
        {
            // Not an asteroid impact — do not attribute mining floats to a nearby rock.
            if (asteroidHealthAfter < 0f)
                return;

            bool localShot = tracer.IsAnticipation ||
                             BulletMuzzlePresentation.IsLocalOwner(tracer.OwnerNetworkId);

            // Impact must land on this rock’s hit sphere (cluster-safe surface fit).
            // Kill hits also match just-culled seed-hydrated rocks (HP already written in ECS).
            BulletCosmeticHitQuery.TryFindAsteroidAtImpact(
                hitDisplayPos, out Entity asteroidEntity, asteroidHealthAfter);

            // --- Mining floats (local shots only — cosmetics) ---
            if (localShot && asteroidEntity != Entity.Null)
            {
                EcsFloatingCountPresenter.TryNotifyLocalAsteroidBulletHit(
                    asteroidEntity,
                    damage,
                    ownerTeam,
                    authoritativeRemainingHealth: asteroidHealthAfter);
            }

            // --- Kill: do not hide from HitRpc ---
            // [TITAN-ORBIT] Surface-fit on a packed belt can hide a neighbor while DestroyRpc
            // culls the rock the server actually killed (two client hides, one server destroy,
            // leftover invisible hull). Authoritative teardown is AsteroidDestroyedRpc only.
            if (asteroidHealthAfter <= 0.01f)
                return;
        }

        /// <summary>
        /// Binds server Sequence / lifetime / bank onto an existing anticipation tracer.
        /// [TITAN-ORBIT] Does <b>not</b> relocate to lagged server SpawnPosition — keeps presentation muzzle flight.
        /// Owner match is loose so NetworkId=0 anticipation still adopts local server spawns.
        /// </summary>
        bool TryAdoptAnticipation(in BulletVfxBridge.SpawnRequest req)
        {
            // --- World / planetary-defense spawns (MountIndex < 0) ---
            // [TITAN-ORBIT] Never adopt ship-mount anticipation. Local Fire while piloting a pad
            // used to leave ship-gun tracers that stole PD SpawnRpcs and kept the wrong velocity
            // (turret aimed at mouse, bullets flew hull-forward).
            if (req.MountIndex < 0)
                return false;

            // --- FIFO adopt (oldest AnticipationOrder for this owner + mount) ---
            // [TITAN-ORBIT] Prefer matching MountIndex so a 4-gun volley binds each server Sequence
            // onto the anticipation that left that barrel. Fall back to any mount if none match
            // (older clients / missing MountIndex on RPC).
            if (!TryFindAdoptIndex(req, preferMountMatch: true, out int bestIndex))
                TryFindAdoptIndex(req, preferMountMatch: false, out bestIndex);

            if (bestIndex < 0)
                return false;

            var adopted = _tracers[bestIndex];
            // --- Bind authority metadata — keep presentation flight ---
            // [TITAN-ORBIT] Do not overwrite Velocity with lagged server aim. Server mounts often
            // bake LocalRotation ≈ identity (hull-forward) while the live weapon component faces
            // a different way — adopting that mid-flight made sequential shots look mis-aimed.
            // Position was already correct from the live muzzle; keep that aim too.
            adopted.Sequence = req.Sequence;
            adopted.IsAnticipation = false;
            adopted.OwnerNetworkId = req.OwnerNetworkId > 0 ? req.OwnerNetworkId : adopted.OwnerNetworkId;
            adopted.MountIndex = req.MountIndex;
            adopted.BankIndex = req.BankIndex;
            adopted.ScaleMultiplier = req.ScaleMultiplier > 0f ? req.ScaleMultiplier : adopted.ScaleMultiplier;
            adopted.Damage = req.Damage;
            // [TITAN-ORBIT] Lifetime <= 0 = distance-only (PD turrets); do not clamp to 0.05s.
            adopted.RemainingLifetime = ResolveTracerLifetime(req.Lifetime);
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

            // --- Pooled tracer shell (destroy-probe: spawnMs ~14 ms was Instantiates here) ---
            GameObject projectilePrefab = null;
            if (!Application.isMobilePlatform && _bank != null)
                projectilePrefab = _bank.GetProjectileVisualPrefab(bankIndex, team);

            if (!BulletTracerPool.TryRent(projectilePrefab, out GameObject go, out _) || go == null)
            {
                // Last resort — should be rare (pool CreateShell failed).
                go = new GameObject("BulletTracer");
                BulletVisualFactory.BuildVisual(
                    go.transform, _bank, bankIndex, team, BulletShape.Sphere,
                    scaleMul, bulletSpeed, noTrail: false);
            }

            Quaternion rot = math.lengthsq(req.Velocity) > 0.0001f
                ? Quaternion.LookRotation(((Vector3)req.Velocity).normalized, Vector3.up)
                : Quaternion.identity;
            go.transform.SetPositionAndRotation(spawnDisplay, rot);

            // Refresh tint / scale / particles on a recycled shell.
            GameObject visual = go.transform.childCount > 0
                ? go.transform.GetChild(0).gameObject
                : go;
            float visualScale = BulletVisualFactory.GetBulletVisualScale(_bank, scaleMul, bankIndex);
            BulletVisualFactory.ApplyColorToVisual(visual, BulletVisualFactory.GetTeamBulletColor(team));
            VfxUrpCompat.ApplyImpactVisualScale(visual, visualScale);
            VfxUrpCompat.PrepareVfxInstance(go);
            BulletVisualFactory.SetAudioPitchInHierarchy(
                go, BulletVisualFactory.GetProjectileSoundPitchBySpeed(bulletSpeed));

            ClientBulletStretchVisual stretch = go.GetComponent<ClientBulletStretchVisual>();
            if (stretch == null
                && _bank != null
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
                // [TITAN-ORBIT] Lifetime <= 0 = distance-only (PD); ship guns keep a positive timer.
                RemainingLifetime = ResolveTracerLifetime(req.Lifetime),
                MaxDistance = math.max(0.5f, req.MaxDistance),
                Traveled = 0f,
                Damage = req.Damage,
                OwnerTeam = req.OwnerTeam,
                BankIndex = bankIndex,
                ScaleMultiplier = scaleMul,
                MountIndex = req.MountIndex,
                IsDisplaySpace = req.IsDisplaySpace,
                IsAnticipation = req.IsAnticipation,
                // Only anticipations need order; server-only tracers keep 0.
                AnticipationOrder = req.IsAnticipation ? _nextAnticipationOrder++ : 0,
                DamageFilter = req.DamageFilter,
                Stretch = stretch,
            };

            _tracers.Add(tracer);
            if (req.Sequence != 0)
                _indexBySequence[req.Sequence] = _tracers.Count - 1;
            else if (req.IsAnticipation)
                BulletVfxBridge.NotifyAnticipationCreated();
        }

        bool _oneShotPoolPrewarmQueued;

        /// <summary>
        /// Maps server/RPC Lifetime onto cosmetic RemainingLifetime.
        /// Positive values keep the ship-gun timer (clamped so tiny floats do not despawn instantly).
        /// Lifetime ≤ 0 means distance-only cull (planetary defense) — RemainingLifetime = +∞ so
        /// the age check never fires and MaxDistance alone ends the tracer.
        /// </summary>
        /// <param name="lifetimeSeconds">Authoritative Lifetime from the spawn request / RPC.</param>
        /// <returns>Seconds for RemainingLifetime, or <see cref="float.PositiveInfinity"/> when unused.</returns>
        static float ResolveTracerLifetime(float lifetimeSeconds)
        {
            // [TITAN-ORBIT] Mirror BulletSimulationSystem: Lifetime <= 0 skips the age timer.
            if (lifetimeSeconds <= 0f)
                return float.PositiveInfinity;
            return math.max(0.1f, lifetimeSeconds);
        }

        void EnsureBank()
        {
            if (_bank == null)
                _bank = BulletVfxBank.LoadDefault();
            if (_bank != null)
                BulletVisualScale.ActiveUpgradeVisualScaleMultiplier = _bank.UpgradeVisualScaleMultiplier;

            // --- Queue muzzle/impact prewarm (drained a few Instantiates/frame) ---
            // [TITAN-ORBIT] Sync Prewarm of 17×5×12 shells cost spawnMs 531 ms (kill-impact-fix).
            // Enqueue + TickPrewarm keeps combat warm without one giant hitch.
            if (!_oneShotPoolPrewarmQueued && _bank != null)
            {
                _oneShotPoolPrewarmQueued = true;
                EnqueueOneShotVfxPoolPrewarm(_bank);
                BulletTracerPool.PrewarmFromBank(_bank, categoryCap: 4, perPrefab: 6);
            }
        }

        /// <summary>
        /// Enqueues unique muzzle/impact prefabs for budgeted Instantiates.
        /// Depth 4 is enough once PrepareVfxInstance is paid at create (cold Instantiates avoided on kill).
        /// </summary>
        static void EnqueueOneShotVfxPoolPrewarm(BulletVfxBank bank)
        {
            if (bank == null || Application.isMobilePlatform)
                return;

            int catCount = bank.CategoryCount;
            for (int i = 0; i < catCount; i++)
            {
                for (int t = (int)TeamId.TeamA; t <= (int)TeamId.TeamE; t++)
                {
                    var team = (TeamId)t;
                    BulletOneShotVfxPool.EnqueuePrewarm(bank.GetMuzzlePrefab(i, team), 3);
                    BulletOneShotVfxPool.EnqueuePrewarm(bank.GetImpactPrefab(i, team), 4);
                }
            }
        }

        /// <summary>
        /// Swept cosmetic collide for one tracer step using <see cref="BulletCosmeticHitQuery"/>.
        /// Hit kind / entity are available for future use; mining floats stay on HitRpc.
        /// Passes <see cref="Tracer.ScaleMultiplier"/> so turret spheres match server
        /// <c>ExpandRadiusForBulletScale</c> (heavy bolts connect like hull hits).
        /// </summary>
        static bool TryPredictCosmeticHit(
            in Tracer t,
            float3 from,
            float3 to,
            out float3 hitPoint,
            out BulletCosmeticHitQuery.ObstacleKind hitKind,
            out Entity hitEntity)
        {
            // [HYBRID] Same obstacle set as server TryResolveBulletHit, including derived
            // planetary-defense pad spheres. Without those, tracers tunnel through guns
            // while BulletHitRpc still punches the HP bar.
            return BulletCosmeticHitQuery.TryHitSegment(
                from,
                to,
                t.OwnerTeam,
                t.OwnerNetworkId,
                t.IsDisplaySpace,
                out hitPoint,
                out hitKind,
                out hitEntity,
                t.DamageFilter,
                t.ScaleMultiplier);
        }

        /// <summary>
        /// Plays impact VFX, records reconcile keys, destroys the tracer.
        /// Does not write damage / HP — server HitRpc still owns mining floats
        /// (<c>AsteroidHealthAfter</c>) so optimistic “HP Left: 0” cannot drift from authority.
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
            // Mining floats intentionally wait for HitRpc (authoritative AsteroidHealthAfter).
            float now = Time.unscaledTime;
            if (t.Sequence != 0)
                RememberPredictedSequence(t.Sequence, t.OwnerNetworkId, now);
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
                OwnerNetworkId = t.OwnerNetworkId,
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
                RememberPredictedSequence(req.Sequence, skip.OwnerNetworkId, now);
                // Drop the spatial recent-impact entry — Sequence owns reconcile from here.
                TryConsumeRecentPredictedImpact(skip.HitDisplayPos, skip.OwnerTeam, out _);
                _pendingPredictedAdoptSkips.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>Records a sequenced predicted hit for HitRpc dedupe (+ owner for mining floats).</summary>
        void RememberPredictedSequence(uint sequence, int ownerNetworkId, float now)
        {
            if (sequence == 0)
                return;
            if (_clientPredictedHitSequences.Add(sequence))
            {
                _clientPredictedHitOwners[sequence] = ownerNetworkId;
                _clientPredictedHitExpiry.Enqueue((sequence, now + PredictedHitTtlSeconds));
            }
        }

        /// <summary>
        /// True when a recent predicted impact sits near this HitRpc display position
        /// (anticipation predicted before Sequence was known).
        /// </summary>
        /// <param name="ownerNetworkId">Shooter id from the matched prediction (0 if none).</param>
        bool TryConsumeRecentPredictedImpact(
            Vector3 hitDisplayPos,
            byte ownerTeam,
            out int ownerNetworkId)
        {
            ownerNetworkId = 0;
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

                ownerNetworkId = recent.OwnerNetworkId;
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
                _clientPredictedHitOwners.Remove(head.Sequence);
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

        /// <summary>
        /// Returns the tracer shell to <see cref="BulletTracerPool"/> (or Destroy if foreign).
        /// </summary>
        static void DestroyTracerGo(in Tracer t)
        {
            if (t.Go != null)
                BulletTracerPool.Return(t.Go);
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
