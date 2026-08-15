using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Client-side floating +/- popups driven by replicated ECS state deltas
    /// and immediate bullet-impact hooks.
    /// Compares per-frame snapshots of ship gems/people/health. Asteroid bullet HitRpc floats
    /// use <see cref="TryNotifyLocalAsteroidBulletHit"/> with server <c>AsteroidHealthAfter</c>
    /// (never ghost − damage). <see cref="PollAsteroids"/> remains a fallback for rams / missed RPCs.
    /// Delegates display to <see cref="WorldFloatingCountManager"/>. Runs on main thread in Update.
    /// <para>
    /// People load/unload floats are owned by <see cref="PeopleTransportVfxDriver"/> (sphere leave/consume).
    /// Asteroid health polling walks hybrid map-body proxies only — never a full asteroid
    /// <c>ToEntityArray</c> — so it still works under session-long <see cref="ClientJoinSettleCache.TransformQuarantine"/>.
    /// </para>
    /// <para>
    /// Gem-deposit audio follows the <b>server</b> metronome via ghosted
    /// <see cref="ShipDepositFeedback.BeatSequence"/>. Local beats use
    /// <see cref="TickLocalDepositMetronome"/> (tagged ship read). Remotes use
    /// <see cref="TickRemoteGemDepositMetronomes"/> with toroidal hear range. Each beat uses the
    /// server <see cref="ShipDepositFeedback.LastChunkAmount"/> for pitch and notifies Orbit Menu
    /// Ship/Bank so UI stays locked to real deposits.
    /// </para>
    /// </summary>
    public class EcsFloatingCountPresenter : MonoBehaviour
    {
        /// <summary>
        /// Live presenter instance — <see cref="BulletVfxDriver"/> calls
        /// <see cref="TryNotifyLocalAsteroidBulletHit"/> on HitRpc so mining floats use
        /// server <c>AsteroidHealthAfter</c> (not lagging ghost Health).
        /// </summary>
        public static EcsFloatingCountPresenter Active { get; private set; }

        /// <summary>Per-ship last-known values for delta detection — keyed by <see cref="GhostOwner.NetworkId"/>.</summary>
        struct ShipSnapshot
        {
            public int People;
            public float Gems;
            public float Health;
            public bool IsDead;
            public int ShipLevel;
            /// <summary>
            /// Last observed <see cref="ShipDepositFeedback.BeatSequence"/> for this remote ship.
            /// Advances → one remote deposit SFX tick (server-driven).
            /// </summary>
            public uint LastDepositBeatSequence;
            /// <summary>Last observed <see cref="ShipBurnOverTimeState.TickSequence"/> for DoT floats.</summary>
            public uint LastBurnTickSequence;
        }

        readonly Dictionary<int, ShipSnapshot> _ships = new Dictionary<int, ShipSnapshot>();
        readonly Dictionary<int, float> _planetGems = new Dictionary<int, float>();
        /// <summary>
        /// Tracked asteroid HP for floats — may be optimistic (below ghost Health) after local bullet hits.
        /// </summary>
        readonly Dictionary<Entity, float> _asteroidHealth = new Dictionary<Entity, float>();
        /// <summary>
        /// Unscaled-time deadline while optimistic HP may stay below ghost Health.
        /// After expiry, <see cref="PollAsteroids"/> snaps back up so a missed server hit cannot
        /// leave “HP Left: 0” forever on a living rock.
        /// </summary>
        readonly Dictionary<Entity, float> _asteroidOptimisticUntil = new Dictionary<Entity, float>();
        /// <summary>
        /// Scratch for <see cref="EcsWorldVisualizer.CopyLiveProxyEntities"/> — reused each frame to avoid GC.
        /// </summary>
        readonly List<Entity> _proxyEntityScratch = new List<Entity>(512);
        /// <summary>Asteroids seen this PollAsteroids pass — reused (was <c>new HashSet</c> every frame).</summary>
        readonly HashSet<Entity> _asteroidSeenScratch = new HashSet<Entity>();
        /// <summary>Stale asteroid keys to remove — reused (was <c>new List</c> on prune).</summary>
        readonly List<Entity> _asteroidStaleScratch = new List<Entity>(64);
        /// <summary>Skip delta popups on first frame after connect — avoids spurious +N from baseline.</summary>
        bool _primed;

        /// <summary>
        /// Last consumed <see cref="ShipDepositFeedback.BeatSequence"/> for the local ship.
        /// 0 means “not primed yet” — first observed sequence is latched without playing (join mid-deposit).
        /// </summary>
        uint _localDepositBeatSequence;

        /// <summary>True after the first local feedback sample this in-game session.</summary>
        bool _localDepositBeatPrimed;

        /// <summary>Last known local cargo — seeded from ghost; estimated down on each server beat.</summary>
        float _cachedLocalGems = -1f;

        /// <summary>Last known local team for deposit floating-count tint.</summary>
        TeamId _cachedLocalTeam = TeamId.None;

        /// <summary>
        /// How long optimistic bullet HP may lag under replicated Health before we trust the ghost again.
        /// Asteroid ghosts use a low MaxSendRate — keep this above one snapshot interval.
        /// </summary>
        const float AsteroidOptimisticHoldSeconds = 1.25f;

        void OnEnable()
        {
            Active = this;
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
        }

        /// <summary>
        /// [UNITY] Polls visualization world each frame when in-game; primes snapshots once on join.
        /// Local deposit metronome runs from <see cref="MoonOrbitClientState"/> even while ship
        /// entity queries are gated (GhostSpawnBacklog).
        /// </summary>
        void Update()
        {
            if (!EcsGameBridge.IsNetworkInGame())
            {
                _primed = false;
                _localDepositBeatSequence = 0;
                _localDepositBeatPrimed = false;
                _cachedLocalGems = -1f;
                return;
            }

            // --- Local deposit beats from ghosted ShipDepositFeedback (tagged ship read) ---
            TickLocalDepositMetronome();

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;

            // [TITAN-ORBIT] Ship ToComponentDataArray during GhostSpawn Instantiates Crash!!!
            // (TeamChoiceResult window — Settling OFF, backlog ON). Wait until ships are idle.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            // --- First frame: record baseline without showing popups ---
            if (!_primed)
            {
                PrimeSnapshots(em);
                _primed = true;
                return;
            }

            // --- Remote deposit metronome BEFORE PollShips (needs previous-frame gems for latch) ---
            TickRemoteGemDepositMetronomes(em);

            // Pickup / health floats need the popup manager; skip delta UI if it is not in the scene.
            if (WorldFloatingCountManager.Instance != null)
            {
                PollShips(em);

                // [TITAN-ORBIT] Asteroid mining floats must run under TransformQuarantine (session-long on
                // Windows). PollAsteroids walks hybrid proxies + per-entity reads — same pattern as
                // BulletCosmeticHitQuery / minimap. Skip only while Settling (map Instantiates storm).
                if (!ClientJoinSettleCache.Settling)
                    PollAsteroids(em);

                // Planet gem dictionary is reserved for future deposit popups. Full planet
                // ToComponentDataArray stays gated — unsafe under TransformQuarantine after Settling OFF.
                if (!ClientJoinSettleCache.TransformQuarantine)
                    PollPlanetGems(em);
            }
            else
            {
                // Keep cargo baselines fresh so the next metronome frame can detect remote drain.
                RefreshShipGemBaselines(em);
            }
        }

        /// <summary>
        /// Local deposit presentation driven by ghosted <see cref="ShipDepositFeedback"/>.
        /// Fires SFX + Orbit Menu Ship/Bank only when the server increments <c>BeatSequence</c>
        /// (real chunk transfer). Tagged ship reads stay safe during Instantiates backlog.
        /// </summary>
        void TickLocalDepositMetronome()
        {
            // --- Clear optimistic UI only when deposit intent turns off ---
            if (!MoonOrbitClientState.WantDepositGems)
            {
                if (MoonOrbitClientState.TryGetOptimisticDepositCargo(out _) ||
                    MoonOrbitClientState.TryGetOptimisticDepositBank(out _))
                    MoonOrbitClientState.ClearOptimisticDepositCargo();
                return;
            }

            // --- Seed cargo / team from tagged ShipState when available ---
            if (EcsGameBridge.TryGetLocalShipState(out ShipState ship))
            {
                if (ship.IsDead || ship.AwaitingTeamSelection)
                    return;

                // Seed once from ghost. While depositing, only follow ghost down when it is clearly
                // behind our estimate — never Min(cache, 0) from a stale empty snapshot (that stuck
                // the Orbit Menu Ship column at 0).
                if (_cachedLocalGems < 0f)
                    _cachedLocalGems = ship.CurrentGems;
                else if (ship.CurrentGems + 0.51f < _cachedLocalGems)
                    _cachedLocalGems = ship.CurrentGems;

                MoonOrbitClientState.EnsureOptimisticDepositCargoSeed(_cachedLocalGems);
                _cachedLocalTeam = ship.Team;
            }

            // --- Server beat feedback (tagged LocalPlayerShipTag) ---
            if (!EcsGameBridge.TryGetLocalShipDepositFeedback(out ShipDepositFeedback feedback))
                return;

            // First sample this session: latch without playing (avoid join mid-deposit burst).
            if (!_localDepositBeatPrimed)
            {
                _localDepositBeatPrimed = true;
                _localDepositBeatSequence = feedback.BeatSequence;
                return;
            }

            if (feedback.BeatSequence == _localDepositBeatSequence)
                return;

            uint missed = feedback.BeatSequence - _localDepositBeatSequence;
            _localDepositBeatSequence = feedback.BeatSequence;

            float chunkAmount = feedback.LastChunkAmount;
            if (chunkAmount <= 0.001f)
                return;

            // Multi-beat hitch: prefer authoritative ghost cargo for the UI estimate.
            if (missed > 1u && EcsGameBridge.TryGetLocalShipState(out ship))
                _cachedLocalGems = ship.CurrentGems;

            // --- Atomic beat: SFX + optimistic Ship/Bank + Orbit Menu ---
            TryGetLocalShipAnchor(out Transform anchor);
            EmitGemDepositBeat(anchor, chunkAmount, _cachedLocalTeam, 1f);

            if (_cachedLocalGems >= 0f)
            {
                float cargoAfter = Mathf.Max(0f, _cachedLocalGems - chunkAmount);
                _cachedLocalGems = cargoAfter;
                MoonOrbitClientState.NotifyLocalDepositBeat(chunkAmount, cargoAfter, updateCargo: true);
            }
            else
            {
                // No cargo baseline yet — bump Bank only; Ship column keeps reading ghost CurrentGems.
                MoonOrbitClientState.NotifyLocalDepositBeat(chunkAmount, 0f, updateCargo: false);
            }
        }

        /// <summary>
        /// Lightweight gem cargo snapshot sync used when floating-count UI is unavailable so the
        /// deposit metronome still has a previous-frame <c>CurrentGems</c> baseline for remotes.
        /// </summary>
        void RefreshShipGemBaselines(EntityManager em)
        {
            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var owners = shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var shipEntities = shipQuery.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < shipStates.Length; i++)
            {
                int networkId = owners[i].NetworkId;
                if (networkId == 0)
                    continue;

                var state = shipStates[i];
                if (_ships.TryGetValue(networkId, out ShipSnapshot snap))
                {
                    snap.Gems = state.CurrentGems;
                    snap.People = state.CurrentPeople;
                    snap.Health = state.Health;
                    snap.IsDead = state.IsDead;
                    snap.ShipLevel = state.ShipLevel;
                    _ships[networkId] = snap;
                }
                else
                {
                    _ships[networkId] = new ShipSnapshot
                    {
                        People = state.CurrentPeople,
                        Gems = state.CurrentGems,
                        Health = state.Health,
                        IsDead = state.IsDead,
                        ShipLevel = state.ShipLevel,
                        LastBurnTickSequence = ReadBurnTickSequence(em, shipEntities[i]),
                    };
                }
            }
        }

        /// <summary>
        /// Captures initial ship/planet/asteroid state into snapshot dictionaries so the first
        /// real delta frame does not treat the baseline as a +N popup.
        /// </summary>
        void PrimeSnapshots(EntityManager em)
        {
            _ships.Clear();
            _planetGems.Clear();
            _asteroidHealth.Clear();
            _asteroidOptimisticUntil.Clear();

            // --- Ships (tiny query — safe after ShouldSkipShipEntityQueries clears) ---
            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var owners = shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var shipEntities = shipQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < shipStates.Length; i++)
            {
                int networkId = owners[i].NetworkId;
                if (networkId == 0)
                    continue;

                var state = shipStates[i];
                _ships[networkId] = new ShipSnapshot
                {
                    People = state.CurrentPeople,
                    Gems = state.CurrentGems,
                    Health = state.Health,
                    IsDead = state.IsDead,
                    ShipLevel = state.ShipLevel,
                    LastBurnTickSequence = ReadBurnTickSequence(em, shipEntities[i]),
                };
            }

            // --- Asteroids via hybrid proxies (quarantine-safe) ---
            // First-sight in PollAsteroids also baselines Health; priming here avoids a one-frame lag
            // before mining feedback after Join Team.
            PrimeAsteroidHealthFromProxies(em);

            // Skip planet baseline under TransformQuarantine (same Crash!!! pattern as minimap).
            if (ClientJoinSettleCache.Settling || ClientJoinSettleCache.TransformQuarantine)
                return;

            using var planetQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>());
            using var planetStates = planetQuery.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < planetStates.Length; i++)
                _planetGems[planetStates[i].PlanetId] = planetStates[i].CurrentGems;
        }

        /// <summary>
        /// Seeds <see cref="_asteroidHealth"/> from live hybrid map-body proxies.
        /// Walks the managed proxy dictionary only — never <c>ToEntityArray</c> over all asteroids.
        /// </summary>
        void PrimeAsteroidHealthFromProxies(EntityManager em)
        {
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
                return;

            visualizer.CopyLiveProxyEntities(_proxyEntityScratch);
            for (int i = 0; i < _proxyEntityScratch.Count; i++)
            {
                Entity entity = _proxyEntityScratch[i];
                if (!em.Exists(entity) ||
                    !em.HasComponent<AsteroidTag>(entity) ||
                    !em.HasComponent<AsteroidState>(entity))
                    continue;

                var state = em.GetComponentData<AsteroidState>(entity);
                _asteroidHealth[entity] = state.Health;
            }
        }

        /// <summary>
        /// Detects ship gem/health deltas and shows floating popups at hull proxy anchor.
        /// People load/unload popups are driven by <see cref="PeopleTransportVfxDriver"/> instead.
        /// </summary>
        void PollShips(EntityManager em)
        {
            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var owners = shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var shipEntities = shipQuery.ToEntityArray(Allocator.Temp);

            int localNetworkId = EcsGameBridge.GetLocalNetworkId();
            bool hasLocalNetworkId = localNetworkId > 0;
            double elapsed = em.World.Time.ElapsedTime;

            for (int i = 0; i < shipStates.Length; i++)
            {
                int networkId = owners[i].NetworkId;
                if (networkId == 0)
                    continue;

                var state = shipStates[i];
                Entity shipEntity = shipEntities[i];
                if (!TryGetShipAnchor(networkId, out Transform anchor))
                    continue;

                ReadBurnTick(em, shipEntity, out uint burnSeq, out float burnTickDamage, out bool burnActive, elapsed);

                if (!_ships.TryGetValue(networkId, out ShipSnapshot last))
                {
                    _ships[networkId] = new ShipSnapshot
                    {
                        People = state.CurrentPeople,
                        Gems = state.CurrentGems,
                        Health = state.Health,
                        IsDead = state.IsDead,
                        ShipLevel = state.ShipLevel,
                        LastBurnTickSequence = burnSeq,
                    };
                    continue;
                }

                var snap = last;
                snap.ShipLevel = state.ShipLevel;

                bool justDied = !last.IsDead && state.IsDead;
                bool justRespawned = last.IsDead && !state.IsDead;

                if (!state.IsDead && !justDied && !justRespawned)
                {
                    // [TITAN-ORBIT] People ±N floats are owned by PeopleTransportVfxDriver at the
                    // transport sphere (leave / consume) — not by CurrentPeople deltas on the hull.

                    // --- Gem pickup only (deposit audio is the metronome in TickGemDepositMetronomes) ---
                    // Positive cargo jumps = mined / collected gems. Deposit drains are intentionally
                    // ignored here so bursty ghost updates cannot stutter or pitch-jump the beat.
                    float gemsDelta = state.CurrentGems - last.Gems;
                    if (gemsDelta > 0.01f)
                    {
                        AudioManager.Instance?.PlayGemCollectSound(gemsDelta);
                        WorldFloatingCountManager.Instance.ShowFloatingCount(
                            anchor,
                            FloatingCountChannel.GemPickup,
                            gemsDelta,
                            state.Team);
                    }
                }

                bool showedBurnDamage = false;
                if (!justRespawned &&
                    !TitanOrbitDebugFlags.IsolateDisableFloatingCounts &&
                    burnSeq > last.LastBurnTickSequence &&
                    burnTickDamage > 0.01f)
                {
                    uint skipped = burnSeq - last.LastBurnTickSequence;
                    float shown = burnTickDamage * skipped;
                    WorldFloatingCountManager.Instance.ShowFloatingCount(
                        anchor,
                        FloatingCountChannel.DamageShipOrDrone,
                        -shown,
                        state.Team);
                    showedBurnDamage = true;
                }

                if (hasLocalNetworkId && networkId == localNetworkId && !state.IsDead && !justDied && !justRespawned)
                {
                    float healthDelta = state.Health - last.Health;
                    // Burn ticks already spawned Damage floats — skip the overlapping Health line.
                    bool skipBurnHealth = (showedBurnDamage || burnActive) && healthDelta < 0f;
                    if (!skipBurnHealth && Mathf.Abs(healthDelta) >= 1f)
                    {
                        WorldFloatingCountManager.Instance.ShowFloatingCount(
                            anchor,
                            FloatingCountChannel.HealthChange,
                            healthDelta,
                            state.Team);
                    }
                }

                // Preserve remote deposit BeatSequence across cargo snapshot writes.
                _ships[networkId] = new ShipSnapshot
                {
                    People = state.CurrentPeople,
                    Gems = state.CurrentGems,
                    Health = state.Health,
                    IsDead = state.IsDead,
                    ShipLevel = state.ShipLevel,
                    LastDepositBeatSequence = snap.LastDepositBeatSequence,
                    LastBurnTickSequence = burnSeq,
                };
            }
        }

        /// <summary>Tracks planet gem totals — reserved for future planet deposit popups.</summary>
        void PollPlanetGems(EntityManager em)
        {
            using var planetQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>());
            using var planetStates = planetQuery.ToComponentDataArray<PlanetState>(Allocator.Temp);

            for (int i = 0; i < planetStates.Length; i++)
            {
                var state = planetStates[i];
                _planetGems[state.PlanetId] = state.CurrentGems;
            }
        }

        /// <summary>
        /// Immediate asteroid mining float for the local player's bullet impact.
        /// Called from <see cref="BulletVfxDriver"/> on HitRpc (impact VFX may already have
        /// played from client-predicted cosmetic collide).
        /// </summary>
        /// <param name="asteroidEntity">Hybrid-proxy asteroid ghost that was hit.</param>
        /// <param name="damage">Bullet damage from the tracer / HitRpc (server-authored amount).</param>
        /// <param name="ownerTeam">Shooter team for tint colors.</param>
        /// <param name="authoritativeRemainingHealth">
        /// When set (HitRpc <c>AsteroidHealthAfter</c>), show that “HP Left” — never ghost − damage.
        /// Null = +Damage only (legacy / non-authoritative path).
        /// </param>
        /// <returns>True when a popup was spawned.</returns>
        public static bool TryNotifyLocalAsteroidBulletHit(
            Entity asteroidEntity,
            float damage,
            TeamId ownerTeam,
            float? authoritativeRemainingHealth = null)
        {
            // [TITAN-ORBIT] Isolation F2 — skip floats to see if Instantiates/UI drives the step.
            if (TitanOrbitDebugFlags.IsolateDisableFloatingCounts)
                return false;

            // --- Resolve live presenter ---
            var presenter = Active;
            if (presenter == null || WorldFloatingCountManager.Instance == null)
                return false;
            if (asteroidEntity == Entity.Null || damage <= 0.01f)
                return false;

            // --- Hull anchor (stack rises above local ship, same as PollAsteroids) ---
            if (!TryGetLocalShipAnchor(out Transform localAnchor))
                return false;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!em.Exists(asteroidEntity) ||
                !em.HasComponent<AsteroidState>(asteroidEntity))
                return false;

            var state = em.GetComponentData<AsteroidState>(asteroidEntity);
            // Authoritative HitRpc may report HP Left: 0 while ghost still looks alive — allow it.
            if (!authoritativeRemainingHealth.HasValue &&
                (state.IsDestroyed || state.Health <= 0f))
                return false;

            // --- RemainingHealth from server HitRpc only ---
            // [TITAN-ORBIT] Logs proved ghost Health can stay at ~16 while server already killed
            // (healthAfter=0). Never compute remaining as ghost − damage on the HitRpc path.
            float? remainingHealth = null;
            if (authoritativeRemainingHealth.HasValue)
            {
                remainingHealth = Mathf.Max(0f, authoritativeRemainingHealth.Value);

                // Align PollAsteroids baseline so a late ghost snapshot does not double-popup.
                presenter._asteroidHealth[asteroidEntity] = remainingHealth.Value;
                presenter._asteroidOptimisticUntil[asteroidEntity] =
                    Time.unscaledTime + AsteroidOptimisticHoldSeconds;
            }

            // [TITAN-ORBIT] Same overlap rule as world tint: prefer shooter/viewer team when in mask.
            byte mask = state.TerritoryTeamsMask;
            if (mask == 0 && state.TerritoryTeam != TeamId.None)
                mask = PlanetConnectionGraphLogic.TeamToMaskBit(state.TerritoryTeam);
            TeamId tintTeam = PlanetConnectionGraphLogic.ResolveAsteroidTintTeam(
                mask, state.TerritoryTeam, ownerTeam);
            if (tintTeam == TeamId.None)
                tintTeam = ownerTeam;

            WorldFloatingCountManager.Instance.ShowAsteroidFeedback(
                localAnchor,
                new AsteroidFloatingFeedback
                {
                    Team = tintTeam,
                    Damage = damage,
                    RemainingHealth = remainingHealth,
                    RemainingGems = null,
                });

            return true;
        }

        /// <summary>
        /// Fallback asteroid mining feedback from replicated Health deltas (ramming, or when
        /// cosmetic hit prediction did not run). Bullet hits should prefer
        /// <see cref="TryNotifyLocalAsteroidBulletHit"/> for zero-latency floats.
        /// <para>
        /// [TITAN-ORBIT] Under session-long TransformQuarantine we must not
        /// <c>ToEntityArray</c> / <c>ToComponentDataArray</c> all asteroids (Windows late-join Crash!!!).
        /// Instead walk <see cref="EcsWorldVisualizer"/> hybrid proxy keys and read
        /// <see cref="AsteroidState"/> per entity — same quarantine-safe pattern as
        /// <see cref="BulletCosmeticHitQuery"/>.
        /// </para>
        /// </summary>
        void PollAsteroids(EntityManager em)
        {
            // --- Need a hull anchor so the stack rises above the local ship ---
            if (!TryGetLocalShipAnchor(out Transform localAnchor))
                return;

            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
                return;

            // Managed dictionary of live GameObject proxies — no Burst gather over asteroids.
            visualizer.CopyLiveProxyEntities(_proxyEntityScratch);

            // [TITAN-ORBIT] Reuse scratch — allocating HashSet/List every Update caused GC spikes
            // while flying (move-probe session 74383c).
            var seen = _asteroidSeenScratch;
            seen.Clear();

            for (int i = 0; i < _proxyEntityScratch.Count; i++)
            {
                Entity entity = _proxyEntityScratch[i];
                // Per-entity HasComponent — never GatherEntitiesWithoutFilter over AsteroidTag.
                if (!em.Exists(entity) ||
                    !em.HasComponent<AsteroidTag>(entity) ||
                    !em.HasComponent<AsteroidState>(entity))
                    continue;

                seen.Add(entity);
                var state = em.GetComponentData<AsteroidState>(entity);

                // First sight of this ghost: baseline Health so we do not flash a fake +Damage.
                if (!_asteroidHealth.TryGetValue(entity, out float lastHealth))
                    lastHealth = state.Health;

                float damage = lastHealth - state.Health;

                // --- Reconcile tracked HP with ghost ---
                // [TITAN-ORBIT] While optimistic hold is active, keep the lower predicted value so
                // lagging snapshots do not wipe the stack / double-popup. After the hold expires,
                // snap UP to ghost Health — otherwise a tunneled / missed server hit leaves
                // “HP Left: 0” forever on a rock that is still alive.
                float tracked = lastHealth;
                bool holdOptimistic =
                    _asteroidOptimisticUntil.TryGetValue(entity, out float until) &&
                    Time.unscaledTime < until;

                if (state.Health < tracked - 0.01f)
                {
                    // Ghost caught up (or mining/ram damage) — trust the lower server value.
                    tracked = state.Health;
                    _asteroidOptimisticUntil.Remove(entity);
                }
                else if (!holdOptimistic && state.Health > tracked + 0.01f)
                {
                    // Prediction was wrong / expired — restore authoritative HP for future floats.
                    tracked = state.Health;
                    _asteroidOptimisticUntil.Remove(entity);
                }
                else
                {
                    tracked = Mathf.Min(tracked, state.Health);
                }

                _asteroidHealth[entity] = tracked;

                // Only show when replicated Health dropped below our prior tracked baseline.
                // HitRpc path already showed floats and left lastHealth <= ghost Health.
                if (damage <= 0.01f || state.IsDestroyed)
                    continue;

                byte mask = state.TerritoryTeamsMask;
                if (mask == 0 && state.TerritoryTeam != TeamId.None)
                    mask = PlanetConnectionGraphLogic.TeamToMaskBit(state.TerritoryTeam);
                TeamId tintTeam = PlanetConnectionGraphLogic.ResolveAsteroidTintTeam(
                    mask, state.TerritoryTeam, _cachedLocalTeam);

                WorldFloatingCountManager.Instance.ShowAsteroidFeedback(
                    localAnchor,
                    new AsteroidFloatingFeedback
                    {
                        Team = tintTeam,
                        Damage = damage,
                        RemainingHealth = state.Health,
                        RemainingGems = state.RemainingGems,
                    });
            }

            // --- Drop snapshots for despawned / recycled entities ---
            if (_asteroidHealth.Count > seen.Count)
            {
                var stale = _asteroidStaleScratch;
                stale.Clear();
                foreach (var kv in _asteroidHealth)
                {
                    if (!seen.Contains(kv.Key))
                        stale.Add(kv.Key);
                }

                for (int i = 0; i < stale.Count; i++)
                {
                    _asteroidHealth.Remove(stale[i]);
                    _asteroidOptimisticUntil.Remove(stale[i]);
                }
            }
        }

        /// <summary>
        /// Nearby <b>remote</b> deposit SFX driven by ghosted <see cref="ShipDepositFeedback"/>.
        /// Local beats are owned by <see cref="TickLocalDepositMetronome"/> (skips local owner).
        /// </summary>
        void TickRemoteGemDepositMetronomes(EntityManager em)
        {
            float hearRange = GemEconomyConstants.GemDepositHearRange;
            float fullVolumeRange = GemEconomyConstants.GemDepositHearFullVolumeRange;
            // Missing map period → skip remote deposit SFX distance (never invent 1000).
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return;

            // --- Listener pose for remote proximity ---
            bool hasListener =
                EcsGameBridge.TryGetLocalShipPresentationPosition(out Vector3 listenerPos) ||
                EcsGameBridge.TryGetLocalShipPosition(out listenerPos);
            if (!hasListener)
                return;

            // Throttle: ship gather is fine after ShouldSkipShipEntityQueries; keep cheap when idle.
            if ((Time.frameCount % 3) != 0)
                return;

            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<ShipDepositFeedback>());
            using var entities = shipQuery.ToEntityArray(Allocator.Temp);
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var owners = shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var feedbacks = shipQuery.ToComponentDataArray<ShipDepositFeedback>(Allocator.Temp);

            for (int i = 0; i < shipStates.Length; i++)
            {
                int networkId = owners[i].NetworkId;
                if (networkId == 0)
                    continue;

                Entity shipEntity = entities[i];

                // Local owner is handled by TickLocalDepositMetronome — avoid double beats.
                if (em.HasComponent<GhostOwnerIsLocal>(shipEntity))
                    continue;

                var state = shipStates[i];
                var feedback = feedbacks[i];

                if (!_ships.TryGetValue(networkId, out ShipSnapshot snap))
                {
                    snap = new ShipSnapshot
                    {
                        People = state.CurrentPeople,
                        Gems = state.CurrentGems,
                        Health = state.Health,
                        IsDead = state.IsDead,
                        ShipLevel = state.ShipLevel,
                        LastDepositBeatSequence = feedback.BeatSequence,
                        LastBurnTickSequence = ReadBurnTickSequence(em, shipEntity),
                    };
                    _ships[networkId] = snap;
                    continue;
                }

                if (feedback.BeatSequence == snap.LastDepositBeatSequence)
                {
                    snap.ShipLevel = state.ShipLevel;
                    _ships[networkId] = snap;
                    continue;
                }

                snap.LastDepositBeatSequence = feedback.BeatSequence;
                float gemValue = feedback.LastChunkAmount;
                if (gemValue <= 0.001f || state.IsDead)
                {
                    _ships[networkId] = snap;
                    continue;
                }

                TryGetShipAnchor(networkId, out Transform anchor);

                Vector3 depositorPos;
                if (anchor != null)
                    depositorPos = anchor.position;
                else if (em.HasComponent<LocalTransform>(shipEntity))
                    depositorPos = em.GetComponentData<LocalTransform>(shipEntity).Position;
                else
                {
                    _ships[networkId] = snap;
                    continue;
                }

                float3 listener = new float3(listenerPos.x, listenerPos.y, listenerPos.z);
                float3 depositor = new float3(depositorPos.x, depositorPos.y, depositorPos.z);
                float dist = ToroidalMapEcs.ToroidalDistance(listener, depositor, mapW, mapH);
                if (dist > hearRange)
                {
                    _ships[networkId] = snap;
                    continue;
                }

                float volumeScale = dist <= fullVolumeRange
                    ? 1f
                    : 1f - Mathf.InverseLerp(fullVolumeRange, hearRange, dist);

                EmitGemDepositBeat(anchor, gemValue, state.Team, volumeScale);
                snap.ShipLevel = state.ShipLevel;
                _ships[networkId] = snap;
            }
        }

        /// <summary>
        /// Single deposit metronome tick — pitch matches the actual chunk amount (full ship-level
        /// load or leftover cargo), plus floating count when a hull proxy anchor exists.
        /// </summary>
        /// <param name="anchor">Optional ship hull proxy (null = sound only).</param>
        /// <param name="gemValue">Actual gems this beat (ship level, or leftover — drives pitch).</param>
        /// <param name="team">Team tint for the floating count.</param>
        /// <param name="volumeScale">Proximity volume 0–1 from toroidal hear range.</param>
        static void EmitGemDepositBeat(Transform anchor, float gemValue, TeamId team, float volumeScale)
        {
            // Sound does not require a hull proxy — local deposit must tick even if GO sync lags.
            // [TITAN-ORBIT] Use GetOrFind so Windows player builds still hear deposits if Awake
            // order left Instance unset for a frame (Editor often had the singleton already hot).
            AudioManager.GetOrFind()?.PlayGemDepositSound(gemValue, volumeScale);

            if (anchor == null || WorldFloatingCountManager.Instance == null)
                return;

            WorldFloatingCountManager.Instance.ShowFloatingCount(
                anchor,
                FloatingCountChannel.GemDeposit,
                gemValue,
                team);
        }

        /// <summary>Ghosted burn tick counter, or 0 when the ship has no burn component.</summary>
        static uint ReadBurnTickSequence(EntityManager em, Entity shipEntity)
        {
            if (shipEntity == Entity.Null || !em.HasComponent<ShipBurnOverTimeState>(shipEntity))
                return 0;
            return em.GetComponentData<ShipBurnOverTimeState>(shipEntity).TickSequence;
        }

        /// <summary>Reads ghosted burn tick fields used for DoT floating damage.</summary>
        static void ReadBurnTick(
            EntityManager em,
            Entity shipEntity,
            out uint sequence,
            out float lastTickDamage,
            out bool isActive,
            double elapsed)
        {
            sequence = 0;
            lastTickDamage = 0f;
            isActive = false;
            if (shipEntity == Entity.Null || !em.HasComponent<ShipBurnOverTimeState>(shipEntity))
                return;

            var burn = em.GetComponentData<ShipBurnOverTimeState>(shipEntity);
            sequence = burn.TickSequence;
            lastTickDamage = burn.LastTickDamage;
            isActive = burn.IsActive(elapsed);
        }

        /// <summary>[HYBRID] Popup anchor is ship hull proxy transform from ShipWeaponProxyRegistry.</summary>
        static bool TryGetShipAnchor(int networkId, out Transform anchor) =>
            ShipWeaponProxyRegistry.TryGetHull(networkId, out anchor);

        /// <summary>Local player hull proxy — asteroid feedback attaches near own ship.</summary>
        static bool TryGetLocalShipAnchor(out Transform anchor)
        {
            anchor = null;
            int localNetworkId = EcsGameBridge.GetLocalNetworkId();
            if (localNetworkId <= 0)
                return false;
            return ShipWeaponProxyRegistry.TryGetHull(localNetworkId, out anchor);
        }
    }
}
