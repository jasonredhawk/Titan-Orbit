using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
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
    /// Client-only troop escorts: intact load capsules derived from ghosted
    /// <see cref="ShipState.CurrentPeople"/> + ship display pose. No extra network.
    /// Sits on a spaced ring outside the hull. Unload launches gathered capsules on the old cadence.
    /// Does not require <see cref="ShipOrbitState.IsTransferringPeople"/> (client prediction can clear it).
    /// </summary>
    [DefaultExecutionOrder(66210)]
    public sealed class PeopleTransportEscortPresenter : MonoBehaviour
    {
        const float LiftY = 1.0f;
        const int MaxSpawnsPerFrame = 1;

        struct SlotVisual
        {
            public GameObject Go;
            public float Amount;
            public float3 LogicalPos;
            public float3 Velocity;
            public float CruiseSpeed;
            /// <summary>True when spawned from cargo count (not a load-flight handoff).</summary>
            public bool Synthetic;
            /// <summary>True while this capsule is flying one-way to the planet.</summary>
            public bool Flying;
            /// <summary>True while called to ship center (not launched yet).</summary>
            public bool Ready;
            /// <summary>Planet this launched capsule is committed to.</summary>
            public int TargetPlanetId;

            /// <summary>True after this capsule has caught its seat and should ride the hull.</summary>
            public bool Riding;

            /// <summary>Persistent toroidal display tile so PlaceDisplay does not flicker.</summary>
            public int DisplayTileK;

            /// <summary>Persistent toroidal display tile so PlaceDisplay does not flicker.</summary>
            public int DisplayTileM;

            /// <summary>Aft jet — shown while this capsule is moving.</summary>
            public PeopleTransportThruster Thruster;

            /// <summary>Live HP from <see cref="ShipEscortVitals"/>; negative until first sync.</summary>
            public float Health;

            /// <summary>Seconds since this capsule launched (client land gate).</summary>
            public float FlightElapsed;

            /// <summary>Launch pose for <see cref="PeopleTransportMath.CanCompleteEscortUnload"/>.</summary>
            public float3 SpawnPosition;
        }

        sealed class ShipEscortGroup
        {
            public int NetworkId;
            public byte Team;
            public bool LandingActive;
            public float DispatchAcc;
            public float LastHullRadius;
            public float LastExtX;
            public float LastExtZ;
            public float3 LastHullPos;
            public bool HasLastHull;
            public readonly List<SlotVisual> Slots = new List<SlotVisual>(8);
        }

        struct Adopted
        {
            public int NetworkId;
            public GameObject Go;
            public float Amount;
            public byte Team;
            public float3 LogicalPos;
        }

        static PeopleTransportEscortPresenter s_Instance;

        struct TransferDwell
        {
            public float Seconds;
            public int PlanetId;
            public byte Direction;
        }

        readonly Dictionary<int, ShipEscortGroup> _groups = new Dictionary<int, ShipEscortGroup>(8);
        readonly Dictionary<int, TransferDwell> _orbitDwell = new Dictionary<int, TransferDwell>(8);
        readonly List<int> _aliveNetIds = new List<int>(8);
        readonly List<int> _removeNetIds = new List<int>(8);
        readonly List<Adopted> _adopted = new List<Adopted>(4);

        World _cachedQueryWorld;
        EntityQuery _shipQuery;
        bool _queriesCreated;
        int _spawnsThisFrame;

        /// <summary>Live presenter, or null on dedicated server.</summary>
        public static PeopleTransportEscortPresenter Active => s_Instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation() == false)
                return;
            if (s_Instance != null)
                return;

            var go = new GameObject("PeopleTransportEscortPresenter");
            DontDestroyOnLoad(go);
            s_Instance = go.AddComponent<PeopleTransportEscortPresenter>();
#endif
        }

        void Awake()
        {
            if (s_Instance != null && s_Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_Instance = this;
            Application.onBeforeRender += PinRidingEscortsToLatestHull;
        }

        void OnDestroy()
        {
            Application.onBeforeRender -= PinRidingEscortsToLatestHull;
            if (s_Instance == this)
                s_Instance = null;
            DisposeQueries();
            ClearAll();
        }

        /// <summary>
        /// Load-flight handoff: keep the arriving GO as an escort instead of destroying it.
        /// </summary>
        public static bool TryAdopt(int shipNetworkId, GameObject go, float amount, byte team, float3 logicalPos)
        {
            if (s_Instance == null || go == null || shipNetworkId <= 0)
                return false;
            logicalPos.y = 0f;
            s_Instance._adopted.Add(new Adopted
            {
                NetworkId = shipNetworkId,
                Go = go,
                Amount = math.max(1f, amount),
                Team = team,
                LogicalPos = logicalPos,
            });
            return true;
        }

        /// <summary>Cosmetic tracer spheres — same poses the presenter draws.</summary>
        public static void AppendBulletObstacles(List<BulletCosmeticHitQuery.Obstacle> into)
        {
            if (into == null || s_Instance == null)
                return;

            foreach (var kv in s_Instance._groups)
            {
                var group = kv.Value;
                for (int i = 0; i < group.Slots.Count; i++)
                {
                    var slot = group.Slots[i];
                    if (slot.Go == null || slot.Amount <= 0.01f)
                        continue;
                    into.Add(new BulletCosmeticHitQuery.Obstacle
                    {
                        Kind = BulletCosmeticHitQuery.ObstacleKind.Transport,
                        SourceEntity = Entity.Null,
                        LogicalCenter = slot.LogicalPos,
                        Radius = PeopleTransportMath.GetEscortHitRadius(slot.Amount),
                        TeamOrOwnership = group.Team,
                    });
                }
            }
        }

        /// <summary>Lead-aim samples for planetary-defense cosmetics (display-space).</summary>
        public static void CopyAimEscorts(List<PeopleTransportVfxDriver.AimFlightSample> dst)
        {
            if (dst == null || s_Instance == null)
                return;

            foreach (var kv in s_Instance._groups)
            {
                var group = kv.Value;
                for (int i = 0; i < group.Slots.Count; i++)
                {
                    var slot = group.Slots[i];
                    if (slot.Go == null)
                        continue;
                    float3 display = (float3)slot.Go.transform.position;
                    display.y = 0f;
                    dst.Add(new PeopleTransportVfxDriver.AimFlightSample
                    {
                        DisplayPos = display,
                        Velocity = slot.Velocity,
                        Team = group.Team,
                    });
                }
            }
        }

        void LateUpdate()
        {
            _spawnsThisFrame = 0;
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return;

            World world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
            {
                ClearAll();
                DisposeQueries();
                return;
            }

            EnsureQueries(world);
            if (!_queriesCreated)
                return;

            var em = world.EntityManager;
            using var ships = _shipQuery.ToEntityArray(Allocator.Temp);
            float dt = math.min(0.05f, math.max(0f, Time.deltaTime));
            _aliveNetIds.Clear();

            for (int s = 0; s < ships.Length; s++)
            {
                Entity e = ships[s];
                if (!em.HasComponent<ShipState>(e) || !em.HasComponent<GhostOwner>(e) ||
                    !em.HasComponent<LocalTransform>(e))
                    continue;

                var ship = em.GetComponentData<ShipState>(e);
                var owner = em.GetComponentData<GhostOwner>(e);
                if (owner.NetworkId <= 0 || ship.IsDead || ship.AwaitingTeamSelection)
                    continue;
                if (ship.CurrentPeople <= 0 && !_groups.ContainsKey(owner.NetworkId))
                    continue;

                var xf = em.GetComponentData<LocalTransform>(e);
                var orbit = em.HasComponent<ShipOrbitState>(e)
                    ? em.GetComponentData<ShipOrbitState>(e)
                    : default;

                float3 hullPos = xf.Position;
                quaternion hullRot = xf.Rotation;
                if (ShipWeaponProxyRegistry.TryGetHull(owner.NetworkId, out Transform hull) && hull != null)
                {
                    hullPos = (float3)hull.position;
                    hullRot = hull.rotation;
                }

                hullPos.y = 0f;
                bool thrusting = em.HasComponent<ShipInput>(e) && em.GetComponentData<ShipInput>(e).Thrust;
                bool moonLanding = em.HasComponent<ShipMoonDockState>(e) &&
                                  em.GetComponentData<ShipMoonDockState>(e).LandingProgress > 0.01f;
                bool parkedForTransfer = orbit.InOrbitRing && orbit.OrbitPlanetId != 0 && !thrusting && !moonLanding;
                bool landing = false;
                if (parkedForTransfer)
                {
                    if (!_orbitDwell.TryGetValue(owner.NetworkId, out var dwell) ||
                        dwell.PlanetId != orbit.OrbitPlanetId)
                    {
                        dwell = new TransferDwell { PlanetId = orbit.OrbitPlanetId };
                    }

                    dwell.Seconds += dt;
                    if (dwell.Seconds >= PeopleTransportConstants.OrbitDwellBeforeTransferSeconds &&
                        dwell.Direction != PeopleTransferDirection.Load)
                    {
                        dwell.Direction = IsClientUnloadWave(in ship, in orbit)
                            ? PeopleTransferDirection.Unload
                            : PeopleTransferDirection.Load;
                    }

                    _orbitDwell[owner.NetworkId] = dwell;
                    landing = dwell.Direction == PeopleTransferDirection.Unload;
                }
                else
                {
                    _orbitDwell.Remove(owner.NetworkId);
                }
                int planetLevel = 1;
                if (orbit.OrbitPlanetId != 0 &&
                    EcsGameBridge.TryGetPlanetPoseByPlanetId(
                        orbit.OrbitPlanetId, out _, out _, out var orbitPlanet))
                    planetLevel = math.max(1, orbitPlanet.PlanetLevel);
                int combineMax = PeopleTransportMath.GetTransferChunk(
                    math.max(1, ship.ShipLevel), planetLevel);
                PeopleTransportEscortLogic.ResolveEscortShipExtents(
                    em, e, in xf, out float extX, out float extZ);
                var vitals = em.HasComponent<ShipEscortVitals>(e)
                    ? em.GetComponentData<ShipEscortVitals>(e)
                    : default;
                UpdateGroup(
                    owner.NetworkId, (byte)ship.Team, ship.CurrentPeople, combineMax,
                    hullPos, hullRot, extX, extZ,
                    landing, orbit.OrbitPlanetId, dt, mapW, mapH, in vitals);
                if (_groups.TryGetValue(owner.NetworkId, out var keep) && keep.Slots.Count > 0)
                    _aliveNetIds.Add(owner.NetworkId);
            }

            _removeNetIds.Clear();
            foreach (var kv in _groups)
            {
                if (!_aliveNetIds.Contains(kv.Key))
                    _removeNetIds.Add(kv.Key);
            }

            for (int i = 0; i < _removeNetIds.Count; i++)
            {
                if (_groups.TryGetValue(_removeNetIds[i], out var group))
                {
                    DestroyGroup(group);
                    _groups.Remove(_removeNetIds[i]);
                    _orbitDwell.Remove(_removeNetIds[i]);
                }
            }
        }

        bool ConsumeAdopted(ShipEscortGroup group, int combineMax)
        {
            bool adopted = false;
            for (int i = _adopted.Count - 1; i >= 0; i--)
            {
                var a = _adopted[i];
                if (a.NetworkId != group.NetworkId)
                    continue;
                if (a.Go == null)
                {
                    _adopted.RemoveAt(i);
                    continue;
                }

                group.Team = a.Team;
                if (TryCombineIntoExisting(group, combineMax))
                {
                    Destroy(a.Go);
                    _adopted.RemoveAt(i);
                    adopted = true;
                    continue;
                }

                a.Go.name = "PeopleTransportProxy_Escort";
                if (!TryTakeOverSynthetic(group, in a))
                {
                    group.Slots.Add(new SlotVisual
                    {
                        Go = a.Go,
                        Amount = a.Amount,
                        LogicalPos = a.LogicalPos,
                        Velocity = float3.zero,
                        CruiseSpeed = 0f,
                        Synthetic = false,
                        Riding = false,
                        DisplayTileK = int.MinValue,
                        DisplayTileM = int.MinValue,
                        Thruster = PeopleTransportVisualApplier.EnsureThruster(a.Go),
                        Health = -1f,
                    });
                }

                _adopted.RemoveAt(i);
                adopted = true;
            }

            return adopted;
        }

        void UpdateGroup(
            int networkId,
            byte team,
            int people,
            int combineMax,
            float3 hullPos,
            quaternion hullRot,
            float extX,
            float extZ,
            bool landing,
            int orbitPlanetId,
            float dt,
            float mapW,
            float mapH,
            in ShipEscortVitals vitals)
        {
            if (!_groups.TryGetValue(networkId, out var group))
            {
                group = new ShipEscortGroup { NetworkId = networkId, Team = team };
                _groups[networkId] = group;
            }

            group.Team = team;
            bool adopted = ConsumeAdopted(group, combineMax);
            float hullRadius = math.max(extX, extZ);
            ApplyEscortVitals(group, in vitals);
            SyncVisualAmounts(
                group, people, landing, orbitPlanetId, team,
                hullPos, hullRot, extX, extZ, networkId, combineMax, mapW, mapH, allowShrink: !adopted);

            bool drop = landing && orbitPlanetId != 0;
            if (!drop)
            {
                group.DispatchAcc = 0f;
                for (int c = 0; c < group.Slots.Count; c++)
                {
                    var clear = group.Slots[c];
                    if (!clear.Ready)
                        continue;
                    clear.Ready = false;
                    clear.Riding = false;
                    group.Slots[c] = clear;
                }
            }
            else
            {
                TryPromoteReadyVisual(group, hullPos, hullRot, extX, extZ, networkId, mapW, mapH);
                if (IsReadyVisualParkedAtCenter(group, hullPos, hullRadius, mapW, mapH))
                    group.DispatchAcc += dt;
                if (group.DispatchAcc >= PeopleTransportConstants.UnloadDispatchIntervalSeconds &&
                    TryLaunchReadyVisual(group, hullPos, hullRadius, orbitPlanetId, mapW, mapH))
                {
                    group.DispatchAcc = 0f;
                    TryPromoteReadyVisual(group, hullPos, hullRot, extX, extZ, networkId, mapW, mapH);
                }
            }

            group.LandingActive = drop;
            group.LastHullRadius = hullRadius;
            group.LastExtX = extX;
            group.LastExtZ = extZ;
            float3 shipDelta = float3.zero;
            if (group.HasLastHull)
                shipDelta = PeopleTransportMath.GetEscortShipCarryDelta(
                    group.LastHullPos, hullPos, mapW, mapH);
            if (math.lengthsq(shipDelta) > 64f)
                shipDelta = float3.zero;
            group.LastHullPos = hullPos;
            group.HasLastHull = true;
            int count = group.Slots.Count;

            for (int i = 0; i < count; i++)
            {
                var slot = group.Slots[i];
                if (slot.Go == null)
                    continue;

                if (slot.Flying)
                {
                    int dest = slot.TargetPlanetId != 0 ? slot.TargetPlanetId : orbitPlanetId;
                    if (!EcsGameBridge.TryGetPlanetPoseByPlanetId(
                            dest, out float3 flyPlanetPos, out float flyScale, out _))
                    {
                        PlaceDisplay(slot.Go.transform, slot.LogicalPos, slot.Velocity, mapW, mapH,
                            ref slot.DisplayTileK, ref slot.DisplayTileM);
                        ApplyThruster(ref slot);
                        PeopleTransportNameplate.Sync(slot.Go, networkId, slot.Amount, slot.Health);
                        group.Slots[i] = slot;
                        continue;
                    }

                    float flySize = math.max(0.5f, flyScale);
                    float3 target = PeopleTransportMath.GetPlanetSurfaceToward(
                        flyPlanetPos, flySize, slot.LogicalPos, mapW, mapH);
                    if (slot.CruiseSpeed < 0.12f)
                    {
                        slot.CruiseSpeed = PeopleTransportMath.GetEscortCruise(
                            slot.Amount,
                            PeopleTransportMath.ComputeCruiseSpeed(
                                slot.LogicalPos, target, isLoad: false, mapW, mapH));
                    }

                    slot.Velocity = PeopleTransportMath.SteerEscortVelocity(
                        slot.LogicalPos, target, slot.Velocity, dt,
                        slot.CruiseSpeed, slot.Amount, mapW, mapH);
                    slot.LogicalPos += slot.Velocity * dt;
                    slot.LogicalPos.y = 0f;
                    if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                        slot.LogicalPos = ToroidalMapEcs.Wrap(slot.LogicalPos, mapW, mapH);
                    slot.FlightElapsed += dt;
                }
                else
                {
                    bool ready = slot.Ready;
                    float3 home = ready
                        ? hullPos
                        : PeopleTransportMath.EvaluateEscortSlotPose(
                            hullPos, hullRot, extX, extZ, i, math.max(1, count),
                            slot.Amount, networkId, mapW, mapH);
                    float3 pos = slot.LogicalPos;
                    float3 vel = slot.Velocity;
                    bool riding = slot.Riding;
                    PeopleTransportMath.IntegrateEscortFollow(
                        ref pos, ref vel, ref riding, home,
                        ready ? shipDelta : float3.zero,
                        slot.Amount, dt, mapW, mapH, ready);
                    slot.LogicalPos = pos;
                    slot.Velocity = vel;
                    slot.Riding = riding;
                    slot.CruiseSpeed = PeopleTransportMath.GetEscortFollowCruise(slot.Amount);
                }

                PlaceDisplay(slot.Go.transform, slot.LogicalPos, slot.Velocity, mapW, mapH,
                    ref slot.DisplayTileK, ref slot.DisplayTileM);
                ApplyThruster(ref slot);
                PeopleTransportNameplate.Sync(slot.Go, networkId, slot.Amount, slot.Health);
                group.Slots[i] = slot;
            }

            FinishFlyingSlots(group, team, mapW, mapH);
        }

        static bool TryPromoteReadyVisual(
            ShipEscortGroup group,
            float3 hullPos,
            quaternion hullRot,
            float extX,
            float extZ,
            int shipNetworkId,
            float mapW,
            float mapH)
        {
            int count = group.Slots.Count;
            for (int i = 0; i < count; i++)
            {
                if (group.Slots[i].Ready && !group.Slots[i].Flying)
                    return false;
            }

            for (int i = count - 1; i >= 0; i--)
            {
                var slot = group.Slots[i];
                if (slot.Flying || slot.Ready || slot.Go == null)
                    continue;
                if (!PeopleTransportMath.IsEscortGatheredAtShip(
                        slot.LogicalPos, hullPos, hullRot, extX, extZ,
                        i, count, slot.Amount, shipNetworkId, mapW, mapH))
                    continue;
                slot.Ready = true;
                slot.Riding = false;
                slot.CruiseSpeed = 0f;
                group.Slots[i] = slot;
                return true;
            }

            return false;
        }

        static bool IsReadyVisualParkedAtCenter(
            ShipEscortGroup group,
            float3 hullPos,
            float hullRadius,
            float mapW,
            float mapH)
        {
            for (int i = 0; i < group.Slots.Count; i++)
            {
                var slot = group.Slots[i];
                if (slot.Flying || !slot.Ready || slot.Go == null)
                    continue;
                return PeopleTransportMath.IsEscortParkedAtShipCenter(
                    slot.LogicalPos, slot.Velocity, hullPos, hullRadius, mapW, mapH);
            }

            return false;
        }

        static bool TryLaunchReadyVisual(
            ShipEscortGroup group,
            float3 hullPos,
            float hullRadius,
            int planetId,
            float mapW,
            float mapH)
        {
            if (planetId == 0)
                return false;
            for (int i = 0; i < group.Slots.Count; i++)
            {
                var slot = group.Slots[i];
                if (slot.Flying || !slot.Ready || slot.Go == null)
                    continue;
                if (!PeopleTransportMath.IsEscortParkedAtShipCenter(
                        slot.LogicalPos, slot.Velocity, hullPos, hullRadius, mapW, mapH))
                    return false;

                slot.Flying = true;
                slot.Ready = false;
                slot.Riding = false;
                slot.TargetPlanetId = planetId;
                slot.Velocity = float3.zero;
                slot.CruiseSpeed = 0f;
                slot.FlightElapsed = 0f;
                slot.SpawnPosition = slot.LogicalPos;
                group.Slots[i] = slot;
                return true;
            }

            return false;
        }

        static bool TryCombineIntoExisting(ShipEscortGroup group, int combineMax)
        {
            combineMax = math.max(1, combineMax);
            for (int i = 0; i < group.Slots.Count; i++)
            {
                var slot = group.Slots[i];
                if (slot.Flying || slot.Go == null)
                    continue;
                if (slot.Amount + 0.01f < combineMax)
                    return true;
            }

            return false;
        }

        static bool TryTakeOverSynthetic(ShipEscortGroup group, in Adopted a)
        {
            for (int i = group.Slots.Count - 1; i >= 0; i--)
            {
                var slot = group.Slots[i];
                if (!slot.Synthetic || slot.Go == null)
                    continue;
                if (math.abs(slot.Amount - a.Amount) > 0.01f)
                    continue;
                Destroy(slot.Go);
                slot.Go = a.Go;
                slot.LogicalPos = a.LogicalPos;
                slot.Velocity = float3.zero;
                slot.CruiseSpeed = 0f;
                slot.Synthetic = false;
                slot.Riding = false;
                slot.Thruster = PeopleTransportVisualApplier.EnsureThruster(slot.Go);
                group.Slots[i] = slot;
                return true;
            }

            return false;
        }

        void SyncVisualAmounts(
            ShipEscortGroup group,
            int people,
            bool landing,
            int planetId,
            byte team,
            float3 hullPos,
            quaternion hullRot,
            float extX,
            float extZ,
            int shipNetworkId,
            int combineMax,
            float mapW,
            float mapH,
            bool allowShrink)
        {
            people = math.max(0, people);
            int cargo = SumCargoAmounts(group);
            int flying = SumFlyingAmounts(group);
            if (people <= 0)
            {
                // During an unload wave the last capsule may still be cargo here
                // (CurrentPeople already dropped). Do not destroy it — launch owns it.
                if (!landing && flying == 0)
                    ShrinkCargoOnly(group, cargo);
                return;
            }

            // Never shrink followers to match a launch debit. That resized healthy
            // capsules and collapsed their health bars. Flying slots already hold
            // the missing people; a live wave will launch the rest.
            if (allowShrink && cargo > people && !landing && flying == 0)
            {
                ShrinkCargoOnly(group, cargo - people);
                cargo = SumCargoAmounts(group);
            }

            if (cargo >= people)
                return;

            // Local launch marks a slot Flying before CurrentPeople ghosts down.
            // Those people are already shown — do not pour them into the remaining
            // capsules (that grew size and emptied the health bar).
            if (flying > 0 && cargo + flying >= people)
                return;

            float leftover = FillVisualSlots(group, people - cargo, combineMax);
            if (leftover <= 0.01f)
                return;

            if (group.Slots.Count >= PeopleTransportMath.MaxEscortVisualSlots)
            {
                int last = group.Slots.Count - 1;
                var merge = group.Slots[last];
                GrowVisualAmount(ref merge, merge.Amount + leftover);
                group.Slots[last] = merge;
                return;
            }

            if (_spawnsThisFrame >= MaxSpawnsPerFrame)
                return;

            var go = PeopleTransportVisualApplier.CreateVisual(null, leftover, (TeamId)team);
            if (go == null)
                return;
            go.name = "PeopleTransportProxy_Escort";
            int idx = group.Slots.Count;
            float3 pos = PeopleTransportMath.EvaluateEscortSlotPose(
                hullPos, hullRot, extX, extZ, idx, idx + 1, leftover, shipNetworkId, mapW, mapH);
            int tileK = int.MinValue;
            int tileM = int.MinValue;
            PlaceDisplay(go.transform, pos, float3.zero, mapW, mapH, ref tileK, ref tileM);
            group.Slots.Add(new SlotVisual
            {
                Go = go,
                Amount = leftover,
                LogicalPos = pos,
                Velocity = float3.zero,
                CruiseSpeed = 0f,
                Synthetic = true,
                Riding = false,
                DisplayTileK = tileK,
                DisplayTileM = tileM,
                Thruster = PeopleTransportVisualApplier.EnsureThruster(go),
                Health = -1f,
            });
            _spawnsThisFrame++;
        }

        static float FillVisualSlots(ShipEscortGroup group, float leftover, int combineMax)
        {
            combineMax = math.max(1, combineMax);
            for (int i = group.Slots.Count - 1; i >= 0 && leftover > 0.01f; i--)
            {
                var slot = group.Slots[i];
                if (slot.Flying)
                    continue;
                float room = combineMax - slot.Amount;
                if (room <= 0.01f)
                    continue;
                float add = math.min(leftover, room);
                GrowVisualAmount(ref slot, slot.Amount + add);
                group.Slots[i] = slot;
                leftover -= add;
            }

            return leftover;
        }

        static int SumCargoAmounts(ShipEscortGroup group)
        {
            int sum = 0;
            for (int i = 0; i < group.Slots.Count; i++)
            {
                if (group.Slots[i].Flying)
                    continue;
                sum += math.max(0, (int)group.Slots[i].Amount);
            }

            return sum;
        }

        static int SumFlyingAmounts(ShipEscortGroup group)
        {
            int sum = 0;
            for (int i = 0; i < group.Slots.Count; i++)
            {
                if (!group.Slots[i].Flying)
                    continue;
                sum += math.max(0, (int)group.Slots[i].Amount);
            }

            return sum;
        }

        /// <summary>
        /// Grows a capsule for a real pickup. Full / unknown HP stays full;
        /// existing damage is kept and clamped to the new max.
        /// </summary>
        static void GrowVisualAmount(ref SlotVisual slot, float newAmount)
        {
            float oldAmount = slot.Amount;
            newAmount = math.max(0.001f, newAmount);
            if (math.abs(newAmount - oldAmount) <= 0.01f)
                return;

            float oldMax = PeopleTransportMath.ComputeMaxHealth(math.max(0.001f, oldAmount));
            slot.Amount = newAmount;
            float newMax = PeopleTransportMath.ComputeMaxHealth(newAmount);
            if (slot.Health < 0f || slot.Health >= oldMax - 0.01f)
                slot.Health = newMax;
            else
                slot.Health = math.min(slot.Health, newMax);

            if (slot.Go != null)
                PeopleTransportVisualApplier.ApplyAmountScale(slot.Go, slot.Amount);
        }

        static void ShrinkCargoOnly(ShipEscortGroup group, int deficit)
        {
            for (int i = group.Slots.Count - 1; i >= 0 && deficit > 0; i--)
            {
                if (group.Slots[i].Flying)
                    continue;
                deficit = ConsumeVisualSlot(group, i, deficit, planetId: 0, team: 0);
            }
        }

        static void ApplyEscortVitals(ShipEscortGroup group, in ShipEscortVitals vitals)
        {
            int cargoCursor = 0;
            int flyCursor = 0;
            int n = math.min((int)vitals.Count, ShipEscortVitals.MaxSlots);
            for (int s = 0; s < n; s++)
            {
                bool flying = vitals.IsInFlight(s);
                float hp = vitals.GetHealth(s);
                int seen = 0;
                int want = flying ? flyCursor : cargoCursor;
                int found = -1;
                for (int i = 0; i < group.Slots.Count; i++)
                {
                    if (group.Slots[i].Flying != flying)
                        continue;
                    if (seen++ < want)
                        continue;
                    found = i;
                    break;
                }

                if (found < 0)
                    continue;
                if (flying)
                    flyCursor++;
                else
                    cargoCursor++;

                var slot = group.Slots[found];
                float amt = vitals.GetAmount(s);
                if (amt > 0.01f && math.abs(slot.Amount - amt) > 0.01f)
                {
                    slot.Amount = amt;
                    if (slot.Go != null)
                        PeopleTransportVisualApplier.ApplyAmountScale(slot.Go, slot.Amount);
                }

                // Skip a 0 HP write over "not yet synced" — empty vitals would
                // collapse the bar the first frame a wave starts.
                if (hp > 0.01f || slot.Health >= 0f)
                    slot.Health = hp;
                group.Slots[found] = slot;
            }
        }

        void FinishFlyingSlots(ShipEscortGroup group, byte team, float mapW, float mapH)
        {
            for (int i = group.Slots.Count - 1; i >= 0; i--)
            {
                var slot = group.Slots[i];
                if (slot.Go == null)
                {
                    group.Slots.RemoveAt(i);
                    continue;
                }

                if (slot.Health >= 0f && slot.Health <= 0.01f)
                {
                    Destroy(slot.Go);
                    group.Slots.RemoveAt(i);
                    continue;
                }

                if (!slot.Flying)
                    continue;

                int dest = slot.TargetPlanetId;
                if (dest == 0)
                    continue;
                if (!EcsGameBridge.TryGetPlanetPoseByPlanetId(
                        dest, out float3 planetPos, out float scale, out _))
                    continue;

                float planetSize = math.max(0.5f, scale);
                if (!PeopleTransportMath.CanCompleteEscortUnload(
                        slot.LogicalPos, slot.SpawnPosition, planetPos, planetSize,
                        slot.FlightElapsed, mapW, mapH))
                    continue;

                ShowLandPopup(slot.Amount, team, dest, slot.LogicalPos);
                Destroy(slot.Go);
                group.Slots.RemoveAt(i);
            }
        }

        void ShrinkLandedThenFollow(
            ShipEscortGroup group,
            int deficit,
            int planetId,
            byte team,
            float mapW,
            float mapH)
        {
            while (deficit > 0 && group.Slots.Count > 0)
            {
                int i = FindClosestFlyer(group, mapW, mapH);
                if (i < 0)
                {
                    for (int f = group.Slots.Count - 1; f >= 0 && deficit > 0; f--)
                    {
                        if (group.Slots[f].Flying)
                            continue;
                        deficit = ConsumeVisualSlot(group, f, deficit, planetId, team);
                    }

                    return;
                }

                deficit = ConsumeVisualSlot(group, i, deficit, planetId, team);
            }
        }

        static int FindClosestFlyer(ShipEscortGroup group, float mapW, float mapH)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < group.Slots.Count; i++)
            {
                var slot = group.Slots[i];
                if (!slot.Flying)
                    continue;
                int dest = slot.TargetPlanetId;
                float d = 0f;
                if (dest != 0 &&
                    EcsGameBridge.TryGetPlanetPoseByPlanetId(dest, out float3 planetPos, out float scale, out _))
                {
                    float3 surface = PeopleTransportMath.GetPlanetSurfaceToward(
                        planetPos, math.max(0.5f, scale), slot.LogicalPos, mapW, mapH);
                    d = ToroidalMapEcs.ToroidalDistance(slot.LogicalPos, surface, mapW, mapH);
                }

                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            return best;
        }

        static int ConsumeVisualSlot(ShipEscortGroup group, int index, int deficit, int planetId, byte team)
        {
            var slot = group.Slots[index];
            int have = math.max(0, (int)slot.Amount);
            int dest = slot.TargetPlanetId != 0 ? slot.TargetPlanetId : planetId;
            if (have <= deficit)
            {
                if (slot.Amount > 0.01f)
                    ShowLandPopup(slot.Amount, team, dest, slot.LogicalPos);
                if (slot.Go != null)
                    Object.Destroy(slot.Go);
                group.Slots.RemoveAt(index);
                return deficit - have;
            }

            slot.Amount = have - deficit;
            if (slot.Go != null)
                PeopleTransportVisualApplier.ApplyAmountScale(slot.Go, slot.Amount);
            group.Slots[index] = slot;
            return 0;
        }

        static void ShowLandPopup(float amount, byte team, int planetId, float3 hint)
        {
            if (WorldFloatingCountManager.Instance == null || planetId == 0)
                return;
            if (!EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out float3 planetPos, out float scale, out _))
                return;

            Vector3 avoid = new Vector3(planetPos.x, 0f, planetPos.z);
            float radius = math.max(0.5f, scale) * 0.55f;
            WorldFloatingCountManager.Instance.ShowFloatingCountAtWorldPosition(
                new Vector3(hint.x, LiftY, hint.z),
                FloatingCountChannel.PeopleUnload,
                amount,
                (TeamId)team,
                avoid,
                radius,
                WorldFloatingCountManager.TargetIdForPlanet(planetId));
        }

        static bool IsClientUnloadWave(in ShipState ship, in ShipOrbitState orbit)
        {
            if (!orbit.InOrbitRing || orbit.OrbitPlanetId == 0)
                return false;
            if (!EcsGameBridge.TryGetPlanetPoseByPlanetId(
                    orbit.OrbitPlanetId, out _, out _, out var planet))
                return false;
            return PeopleTransportEscortLogic.ShouldUnloadEscorts(in ship, in planet);
        }

        /// <summary>
        /// After hull proxies get their onBeforeRender pose, re-pin riding escorts so they
        /// do not sit on last LateUpdate slot while the ship moves again.
        /// </summary>
        void PinRidingEscortsToLatestHull()
        {
            if (s_Instance != this)
                return;
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (_groups.Count == 0)
                return;
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return;

            foreach (var kv in _groups)
            {
                var group = kv.Value;
                if (group == null || group.Slots.Count == 0)
                    continue;
                if (!ShipWeaponProxyRegistry.TryGetHull(group.NetworkId, out Transform hull) ||
                    hull == null)
                    continue;

                float3 hullPos = (float3)hull.position;
                hullPos.y = 0f;
                quaternion hullRot = hull.rotation;
                float3 carry = float3.zero;
                if (group.HasLastHull)
                    carry = PeopleTransportMath.GetEscortShipCarryDelta(
                        group.LastHullPos, hullPos, mapW, mapH);
                if (math.lengthsq(carry) > 64f)
                    carry = float3.zero;
                group.LastHullPos = hullPos;
                group.HasLastHull = true;
                int count = group.Slots.Count;
                for (int i = 0; i < count; i++)
                {
                    var slot = group.Slots[i];
                    if (slot.Go == null || slot.Flying)
                        continue;

                    if (slot.Ready)
                    {
                        float3 pos = slot.LogicalPos + carry;
                        pos.y = 0f;
                        if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                            pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);
                        slot.LogicalPos = pos;
                        PlaceDisplay(slot.Go.transform, slot.LogicalPos, slot.Velocity, mapW, mapH,
                            ref slot.DisplayTileK, ref slot.DisplayTileM);
                        ApplyThruster(ref slot);
                        group.Slots[i] = slot;
                        continue;
                    }

                    if (!slot.Riding)
                        continue;

                    float3 home = PeopleTransportMath.EvaluateEscortSlotPose(
                        hullPos, hullRot, group.LastExtX, group.LastExtZ, i, math.max(1, count),
                        slot.Amount, group.NetworkId, mapW, mapH);
                    slot.LogicalPos = home;
                    slot.Velocity = float3.zero;
                    PlaceDisplay(slot.Go.transform, slot.LogicalPos, slot.Velocity, mapW, mapH,
                        ref slot.DisplayTileK, ref slot.DisplayTileM);
                    ApplyThruster(ref slot);
                    group.Slots[i] = slot;
                }
            }
        }

        static void ApplyThruster(ref SlotVisual slot)
        {
            if (slot.Go == null)
                return;
            if (slot.Thruster == null)
                slot.Thruster = PeopleTransportVisualApplier.EnsureThruster(slot.Go);
            if (slot.Thruster != null)
            {
                float cruise = slot.CruiseSpeed > 0.12f
                    ? slot.CruiseSpeed
                    : PeopleTransportMath.GetEscortFollowCruise(slot.Amount);
                slot.Thruster.SetMotion(math.length(slot.Velocity), cruise);
            }
        }

        static void PlaceDisplay(
            Transform t,
            float3 logical,
            float3 velocity,
            float mapW,
            float mapH,
            ref int tileK,
            ref int tileM)
        {
            if (t == null)
                return;
            Vector3 display;
            if (ToroidalDisplay.TryGetReferencePosition(out Vector3 reference) &&
                ToroidalMapEcs.IsValidMapSize(mapW, mapH))
            {
                float3 unwrapped = ToroidalMapEcs.GetDisplayPositionWithHysteresis(
                    logical, (float3)reference, ref tileK, ref tileM);
                unwrapped.y = LiftY;
                display = unwrapped;
            }
            else
            {
                display = new Vector3(logical.x, LiftY, logical.z);
            }

            t.position = display;
            PeopleTransportVisualApplier.ApplyTravelFacing(t, velocity);
        }

        void EnsureQueries(World world)
        {
            if (world == null || !world.IsCreated)
                return;
            if (_queriesCreated && _cachedQueryWorld == world)
                return;

            DisposeQueries();
            _shipQuery = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>());
            _cachedQueryWorld = world;
            _queriesCreated = true;
        }

        void DisposeQueries()
        {
            if (_queriesCreated && _cachedQueryWorld != null && _cachedQueryWorld.IsCreated &&
                _shipQuery != default)
                _shipQuery.Dispose();
            _shipQuery = default;
            _queriesCreated = false;
            _cachedQueryWorld = null;
        }

        void DestroyGroup(ShipEscortGroup group)
        {
            for (int i = 0; i < group.Slots.Count; i++)
            {
                if (group.Slots[i].Go != null)
                    Destroy(group.Slots[i].Go);
            }

            group.Slots.Clear();
        }

        void ClearAll()
        {
            foreach (var kv in _groups)
                DestroyGroup(kv.Value);
            _groups.Clear();
            _orbitDwell.Clear();
            _adopted.Clear();
        }
    }
}
