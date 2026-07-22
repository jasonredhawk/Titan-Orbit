using System.Collections.Generic;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
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
    /// Gem-deposit audio is a wall-clock <b>metronome</b>. Local beats use
    /// <see cref="TickLocalDepositMetronome"/> (<see cref="MoonOrbitClientState"/> only — no NetworkId
    /// / dock ghost gates). Remotes use <see cref="TickRemoteGemDepositMetronomes"/> with toroidal
    /// hear range. Deposit pitch stays in an audible band (not the pickup 0.01 curve).
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
            /// Unscaled <see cref="Time.time"/> of the last deposit metronome beat for this ship.
            /// Keeps each depositing hull on a steady cadence independent of NetCode snapshot jitter.
            /// </summary>
            public float LastDepositSoundTime;
            /// <summary>
            /// Keep playing remote deposit beats until this time after we observed cargo drain while
            /// docked. Bridges gaps between ghost <c>CurrentGems</c> snapshots so the metronome
            /// does not go silent between NetCode updates.
            /// </summary>
            public float DepositAudioLatchedUntil;
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
        /// <summary>Skip delta popups on first frame after connect — avoids spurious +N from baseline.</summary>
        bool _primed;

        /// <summary>
        /// Wall-clock time of the last <b>local</b> deposit metronome beat.
        /// Independent of the per-ship snapshot dictionary so local SFX cannot desync from NetworkId matching.
        /// </summary>
        float _localDepositBeatTime;

        /// <summary>Last known local cargo — refreshed from ECS when safe; estimated down between beats.</summary>
        float _cachedLocalGems = -1f;

        /// <summary>Last known local ship level for deposit pitch (defaults to 1).</summary>
        float _cachedLocalShipLevel = 1f;

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
                _localDepositBeatTime = 0f;
                _cachedLocalGems = -1f;
                return;
            }

            // --- Local deposit metronome (no ship ToEntityArray) ---
            // [TITAN-ORBIT] Must not wait on ShouldSkipShipEntityQueries — backlog would mute deposits.
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
        /// Steady local deposit beat driven only by <see cref="MoonOrbitClientState.WantDepositGems"/>
        /// and cached local cargo. Does not require GhostOwner NetworkId matching, moon-dock ghost
        /// reads, or a hull proxy — those were the main reasons local beats went mostly silent.
        /// </summary>
        void TickLocalDepositMetronome()
        {
            // --- Gate on immediate client deposit toggle ---
            if (!MoonOrbitClientState.WantDepositGems)
            {
                _localDepositBeatTime = 0f;
                return;
            }

            // --- Refresh cargo/level from ECS when ship queries are safe ---
            if (!ClientJoinSettleCache.ShouldSkipShipEntityQueries &&
                EcsGameBridge.TryGetLocalShipState(out ShipState ship))
            {
                if (ship.IsDead || ship.AwaitingTeamSelection)
                    return;

                _cachedLocalGems = ship.CurrentGems;
                _cachedLocalShipLevel = Mathf.Max(1f, ship.ShipLevel);
                _cachedLocalTeam = ship.Team;
            }
            else if (_cachedLocalGems < 0f)
            {
                // First beat before any cache: try a direct read anyway (tiny tagged lookup).
                if (!EcsGameBridge.TryGetLocalShipState(out ship))
                    return;
                if (ship.IsDead || ship.AwaitingTeamSelection)
                    return;
                _cachedLocalGems = ship.CurrentGems;
                _cachedLocalShipLevel = Mathf.Max(1f, ship.ShipLevel);
                _cachedLocalTeam = ship.Team;
            }

            if (_cachedLocalGems <= 0.001f)
                return;

            float now = Time.time;
            float beatInterval = GemEconomyConstants.GemDepositBeatIntervalSeconds;
            if (_localDepositBeatTime > 0f && now - _localDepositBeatTime < beatInterval)
                return;

            // --- Fire one audible metronome tick ---
            _localDepositBeatTime = now;
            float gemValue = Mathf.Max(1f, _cachedLocalShipLevel);
            TryGetLocalShipAnchor(out Transform anchor);
            EmitGemDepositBeat(anchor, gemValue, _cachedLocalTeam, 1f);

            // Estimate cargo drain between ECS refreshes so we stop soon after the hold empties
            // even if GhostSpawnBacklog blocks TryGetLocalShipState for a moment.
            _cachedLocalGems = Mathf.Max(
                0f,
                _cachedLocalGems - gemValue * GemEconomyConstants.DepositRatePerShipLevel * beatInterval);
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

            int localNetworkId = EcsGameBridge.GetLocalNetworkId();
            bool hasLocalNetworkId = localNetworkId > 0;

            for (int i = 0; i < shipStates.Length; i++)
            {
                int networkId = owners[i].NetworkId;
                if (networkId == 0)
                    continue;

                var state = shipStates[i];
                if (!TryGetShipAnchor(networkId, out Transform anchor))
                    continue;

                if (!_ships.TryGetValue(networkId, out ShipSnapshot last))
                {
                    _ships[networkId] = new ShipSnapshot
                    {
                        People = state.CurrentPeople,
                        Gems = state.CurrentGems,
                        Health = state.Health,
                        IsDead = state.IsDead,
                        ShipLevel = state.ShipLevel,
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

                if (hasLocalNetworkId && networkId == localNetworkId && !state.IsDead && !justDied && !justRespawned)
                {
                    float healthDelta = state.Health - last.Health;
                    if (Mathf.Abs(healthDelta) >= 1f)
                    {
                        WorldFloatingCountManager.Instance.ShowFloatingCount(
                            anchor,
                            FloatingCountChannel.HealthChange,
                            healthDelta,
                            state.Team);
                    }
                }

                // Preserve metronome phase + remote deposit latch across cargo snapshot writes.
                _ships[networkId] = new ShipSnapshot
                {
                    People = state.CurrentPeople,
                    Gems = state.CurrentGems,
                    Health = state.Health,
                    IsDead = state.IsDead,
                    ShipLevel = state.ShipLevel,
                    LastDepositSoundTime = snap.LastDepositSoundTime,
                    DepositAudioLatchedUntil = snap.DepositAudioLatchedUntil,
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

            TeamId tintTeam = state.TerritoryTeam != TeamId.None
                ? state.TerritoryTeam
                : ownerTeam;

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

            var seen = new HashSet<Entity>();

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

                WorldFloatingCountManager.Instance.ShowAsteroidFeedback(
                    localAnchor,
                    new AsteroidFloatingFeedback
                    {
                        Team = state.TerritoryTeam,
                        Damage = damage,
                        RemainingHealth = state.Health,
                        RemainingGems = state.RemainingGems,
                    });
            }

            // --- Drop snapshots for despawned / recycled entities ---
            if (_asteroidHealth.Count > seen.Count)
            {
                var stale = new List<Entity>();
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
        /// Nearby <b>remote</b> deposit metronome. Local beats are owned by
        /// <see cref="TickLocalDepositMetronome"/> so this loop skips <see cref="GhostOwnerIsLocal"/>.
        /// Remotes use ghost intent when present, otherwise a short cargo-drain latch while docked.
        /// </summary>
        void TickRemoteGemDepositMetronomes(EntityManager em)
        {
            float now = Time.time;
            float beatInterval = GemEconomyConstants.GemDepositBeatIntervalSeconds;
            float hearRange = GemEconomyConstants.GemDepositHearRange;
            float fullVolumeRange = GemEconomyConstants.GemDepositHearFullVolumeRange;
            float mapW = math.max(100f, ToroidalMapEcs.MapWidth);
            float mapH = math.max(100f, ToroidalMapEcs.MapHeight);

            // --- Listener pose for remote proximity ---
            bool hasListener =
                EcsGameBridge.TryGetLocalShipPresentationPosition(out Vector3 listenerPos) ||
                EcsGameBridge.TryGetLocalShipPosition(out listenerPos);
            if (!hasListener)
                return;

            // --- Tiny ship query (safe after ShouldSkipShipEntityQueries) ---
            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<ShipMoonDockState>());
            using var entities = shipQuery.ToEntityArray(Allocator.Temp);
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var owners = shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var moonDocks = shipQuery.ToComponentDataArray<ShipMoonDockState>(Allocator.Temp);

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
                var moonDock = moonDocks[i];

                // --- Snapshot row (previous gems still valid — PollShips has not overwritten yet) ---
                if (!_ships.TryGetValue(networkId, out ShipSnapshot snap))
                {
                    snap = new ShipSnapshot
                    {
                        People = state.CurrentPeople,
                        Gems = state.CurrentGems,
                        Health = state.Health,
                        IsDead = state.IsDead,
                        ShipLevel = state.ShipLevel,
                    };
                    _ships[networkId] = snap;
                    continue;
                }

                bool docked = moonDock.MoonPlanetId != 0 &&
                              moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold;
                bool canDeposit = !state.IsDead &&
                                  !state.AwaitingTeamSelection &&
                                  docked &&
                                  state.CurrentGems > 0.001f;

                if (!canDeposit)
                {
                    snap.DepositAudioLatchedUntil = 0f;
                    _ships[networkId] = snap;
                    continue;
                }

                // --- Actively depositing? Intent ghost, else cargo-drain latch ---
                bool wantDeposit =
                    em.HasComponent<ShipDepositIntent>(shipEntity) &&
                    em.GetComponentData<ShipDepositIntent>(shipEntity).WantDepositGems;

                float gemsDelta = state.CurrentGems - snap.Gems;
                if (gemsDelta < -0.01f)
                    snap.DepositAudioLatchedUntil = now + 1.0f;

                if (!wantDeposit && snap.DepositAudioLatchedUntil > now)
                    wantDeposit = true;

                if (!wantDeposit)
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

                if (snap.LastDepositSoundTime > 0f &&
                    now - snap.LastDepositSoundTime < beatInterval)
                {
                    _ships[networkId] = snap;
                    continue;
                }

                float gemValue = Mathf.Max(1f, state.ShipLevel);
                EmitGemDepositBeat(anchor, gemValue, state.Team, volumeScale);
                snap.LastDepositSoundTime = now;
                snap.ShipLevel = state.ShipLevel;
                _ships[networkId] = snap;
            }
        }

        /// <summary>
        /// Single deposit metronome tick — consistent-pitch sound, plus floating count when a hull
        /// proxy anchor exists.
        /// </summary>
        /// <param name="anchor">Optional ship hull proxy (null = sound only).</param>
        /// <param name="gemValue">Stable gem-value chunk for pitch/amount (usually ship level).</param>
        /// <param name="team">Team tint for the floating count.</param>
        /// <param name="volumeScale">Proximity volume 0–1 from toroidal hear range.</param>
        static void EmitGemDepositBeat(Transform anchor, float gemValue, TeamId team, float volumeScale)
        {
            // Sound does not require a hull proxy — local deposit must tick even if GO sync lags.
            AudioManager.Instance?.PlayGemDepositSound(gemValue, volumeScale);

            if (anchor == null || WorldFloatingCountManager.Instance == null)
                return;

            WorldFloatingCountManager.Instance.ShowFloatingCount(
                anchor,
                FloatingCountChannel.GemDeposit,
                gemValue,
                team);
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
