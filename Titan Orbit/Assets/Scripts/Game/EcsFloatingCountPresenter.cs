using System.Collections.Generic;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side floating +/- popups driven by replicated ECS state deltas (gems, people, health, asteroid hits).
    /// </summary>
    public class EcsFloatingCountPresenter : MonoBehaviour
    {
        const float DepositGemSoundInterval = 0.5f;

        struct ShipSnapshot
        {
            public int People;
            public float Gems;
            public float Health;
            public bool IsDead;
            public int ShipLevel;
            public float DepositSoundAccumulator;
            public float LastDepositSoundTime;
        }

        readonly Dictionary<int, ShipSnapshot> _ships = new Dictionary<int, ShipSnapshot>();
        readonly Dictionary<int, float> _planetGems = new Dictionary<int, float>();
        readonly Dictionary<Entity, float> _asteroidHealth = new Dictionary<Entity, float>();
        bool _primed;

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
            if (!_primed)
            {
                PrimeSnapshots(em);
                _primed = true;
                return;
            }

            PollShips(em);
            PollPlanetGems(em);
            PollAsteroids(em);
        }

        void PrimeSnapshots(EntityManager em)
        {
            _ships.Clear();
            _planetGems.Clear();
            _asteroidHealth.Clear();

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

            using var planetQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>());
            using var planetStates = planetQuery.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < planetStates.Length; i++)
                _planetGems[planetStates[i].PlanetId] = planetStates[i].CurrentGems;

            using var asteroidQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadOnly<AsteroidState>());
            using var asteroidEntities = asteroidQuery.ToEntityArray(Allocator.Temp);
            using var asteroidStates = asteroidQuery.ToComponentDataArray<AsteroidState>(Allocator.Temp);
            for (int i = 0; i < asteroidEntities.Length; i++)
                _asteroidHealth[asteroidEntities[i]] = asteroidStates[i].Health;
        }

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
                    int peopleDelta = state.CurrentPeople - last.People;
                    if (peopleDelta != 0)
                    {
                        var channel = peopleDelta > 0 ? FloatingCountChannel.PeopleLoad : FloatingCountChannel.PeopleUnload;
                        WorldFloatingCountManager.Instance.ShowFloatingCount(anchor, channel, peopleDelta, state.Team);
                    }

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

        void PollAsteroids(EntityManager em)
        {
            if (!TryGetLocalShipAnchor(out Transform localAnchor))
                return;

            using var asteroidQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadOnly<AsteroidState>());
            using var entities = asteroidQuery.ToEntityArray(Allocator.Temp);
            using var states = asteroidQuery.ToComponentDataArray<AsteroidState>(Allocator.Temp);

            var seen = new HashSet<Entity>();

            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                seen.Add(entity);
                var state = states[i];

                if (!_asteroidHealth.TryGetValue(entity, out float lastHealth))
                    lastHealth = state.Health;

                float damage = lastHealth - state.Health;
                _asteroidHealth[entity] = state.Health;

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

            if (_asteroidHealth.Count > seen.Count)
            {
                var stale = new List<Entity>();
                foreach (var kv in _asteroidHealth)
                {
                    if (!seen.Contains(kv.Key))
                        stale.Add(kv.Key);
                }

                for (int i = 0; i < stale.Count; i++)
                    _asteroidHealth.Remove(stale[i]);
            }
        }

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

        static void EmitGemDepositFeedback(Transform anchor, float amount, TeamId team)
        {
            AudioManager.Instance?.PlayGemDepositSound(amount);
            WorldFloatingCountManager.Instance.ShowFloatingCount(
                anchor,
                FloatingCountChannel.GemDeposit,
                amount,
                team);
        }

        static bool TryGetShipAnchor(int networkId, out Transform anchor) =>
            ShipWeaponProxyRegistry.TryGetHull(networkId, out anchor);

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
