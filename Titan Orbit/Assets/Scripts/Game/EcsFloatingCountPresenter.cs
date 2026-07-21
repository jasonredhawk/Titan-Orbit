using System.Collections.Generic;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
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
    /// </summary>
    public class EcsFloatingCountPresenter : MonoBehaviour
    {
        /// <summary>Minimum seconds between gem-deposit sound bursts during continuous deposit.</summary>
        const float DepositGemSoundInterval = 0.5f;

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
            /// <summary>Accumulated gem value since last deposit sound — throttles SFX.</summary>
            public float DepositSoundAccumulator;
            public float LastDepositSoundTime;
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
        /// </summary>
        void Update()
        {
            if (!EcsGameBridge.IsNetworkInGame())
            {
                _primed = false;
                return;
            }

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            if (WorldFloatingCountManager.Instance == null)
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
                    else if (gemsDelta < -0.01f)
                    {
                        ProcessGemDepositFeedback(anchor, ref snap, state, -gemsDelta, state.Team);
                    }
                    else if (snap.DepositSoundAccumulator > 0.001f)
                    {
                        snap.DepositSoundAccumulator = 0f;
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

                _ships[networkId] = new ShipSnapshot
                {
                    People = state.CurrentPeople,
                    Gems = state.CurrentGems,
                    Health = state.Health,
                    IsDead = state.IsDead,
                    ShipLevel = state.ShipLevel,
                    DepositSoundAccumulator = snap.DepositSoundAccumulator,
                    LastDepositSoundTime = snap.LastDepositSoundTime,
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

        /// <summary>Throttles gem-deposit SFX and popup by ship level gem value and time interval.</summary>
        static void ProcessGemDepositFeedback(
            Transform anchor,
            ref ShipSnapshot snap,
            in ShipState state,
            float depositedAmount,
            TeamId team)
        {
            snap.DepositSoundAccumulator += depositedAmount;
            float gemValue = Mathf.Max(1f, state.ShipLevel);
            float now = Time.time;

            while (snap.DepositSoundAccumulator >= gemValue
                   && now - snap.LastDepositSoundTime >= DepositGemSoundInterval)
            {
                EmitGemDepositFeedback(anchor, gemValue, team);
                snap.DepositSoundAccumulator -= gemValue;
                snap.LastDepositSoundTime = now;
            }

            if (state.CurrentGems <= 0.001f && snap.DepositSoundAccumulator > 0.001f)
            {
                EmitGemDepositFeedback(anchor, snap.DepositSoundAccumulator, team);
                snap.DepositSoundAccumulator = 0f;
                snap.LastDepositSoundTime = now;
            }
        }

        /// <summary>Single deposit feedback burst — sound + floating count at anchor.</summary>
        static void EmitGemDepositFeedback(Transform anchor, float amount, TeamId team)
        {
            AudioManager.Instance?.PlayGemDepositSound(amount);
            WorldFloatingCountManager.Instance.ShowFloatingCount(
                anchor,
                FloatingCountChannel.GemDeposit,
                amount,
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
