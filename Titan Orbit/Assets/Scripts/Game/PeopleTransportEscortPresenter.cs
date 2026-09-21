using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only: Instantiates parked troop-escort meshes from ghosted
    /// <see cref="PeopleEscortVitalElement"/> + ship pose. Same cheap model as
    /// attack / defense / mining drones — no escort ghosts, pose is
    /// <see cref="PeopleTransportMath.EvaluateEscortSwarmPose"/>.
    /// In-flight seats are the hop VFX, not this presenter.
    /// </summary>
    [DefaultExecutionOrder(67020)]
    public sealed class PeopleTransportEscortPresenter : MonoBehaviour
    {
        const float RemoteVisualRange = 48f;
        const float LiftY = 0.28f;
        /// <summary>At most 12 orbs, and only when the parked layout changes.</summary>
        const int MaxSpawnsPerFrame = 12;

        struct PackedSeat
        {
            public byte SeatId;
            public float Amount;
            public float Health;
        }

        struct SlotVisual
        {
            public byte SeatId;
            public float Amount;
            public GameObject Instance;
        }

        sealed class ShipEscortGroup
        {
            public int NetworkId;
            public readonly List<SlotVisual> Visuals = new List<SlotVisual>(12);
            public int LayoutFingerprint = int.MinValue;
            /// <summary>Last presentation hull used so Instantiates-skip can carry orbs with the ship.</summary>
            public Vector3 LastShipPos;
            public bool HasLastShipPos;
        }

        static PeopleTransportEscortPresenter s_Instance;

        readonly Dictionary<int, ShipEscortGroup> _groups = new Dictionary<int, ShipEscortGroup>(8);
        readonly List<int> _aliveIds = new List<int>(8);
        readonly List<int> _removeIds = new List<int>(8);
        readonly List<SlotVisual> _visualsScratch = new List<SlotVisual>(12);

        World _cachedQueryWorld;
        EntityQuery _shipQuery;
        bool _queriesCreated;
        int _spawnsThisFrame;

        public static PeopleTransportEscortPresenter Active => s_Instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation() == false)
                return;
            if (FindAnyObjectByType<PeopleTransportEscortPresenter>() != null)
                return;
            var go = new GameObject("PeopleTransportEscortPresenter");
            DontDestroyOnLoad(go);
            go.AddComponent<PeopleTransportEscortPresenter>();
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
        }

        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
            DisposeQueries();
            ClearAll();
        }

        void LateUpdate()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            // [TITAN-ORBIT] Moon-dock land-prepare Instantiates can raise ShouldSkipShipEntityQueries.
            // Fully landed: drop parked orbs (orbit menu owns that state). Still approaching:
            // carry them with the hull so they do not freeze in orbit.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                if (IsLocalShipFullyMoonLanded())
                    HideLocalGroup();
                else
                    CarryExistingGroupsWithLocalHull();
                return;
            }
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return;

            if (PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double elapsed, includeTickFraction: true))
                DroneSwarmSimTime.Publish(elapsed);
            double timeSeconds = DroneSwarmSimTime.ResolveOrFallback(Time.time);

            World world = ResolveShipWorld();
            if (world == null || !world.IsCreated)
                return;
            EnsureQueries(world);
            if (!_queriesCreated)
                return;

            var em = world.EntityManager;
            _spawnsThisFrame = 0;
            _aliveIds.Clear();

            using var ships = _shipQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            int localId = EcsGameBridge.GetLocalNetworkId();
            Vector3 localPos = Vector3.zero;
            bool haveLocal = localId > 0 && TryGetShipPresentation(
                em, Entity.Null, localId, out localPos, out _, out _);

            for (int s = 0; s < ships.Length; s++)
            {
                Entity ship = ships[s];
                if (!em.HasComponent<ShipState>(ship) ||
                    !em.HasComponent<GhostOwner>(ship))
                    continue;

                var shipState = em.GetComponentData<ShipState>(ship);
                if (shipState.IsDead || shipState.AwaitingTeamSelection)
                    continue;

                int netId = em.GetComponentData<GhostOwner>(ship).NetworkId;
                if (netId == 0)
                    continue;

                // Parked troop orbs vanish with the hull once this ship is fully moon-docked.
                // In-flight hops stay on PeopleTransportVfxDriver. Other ships keep their escorts.
                if (ShipMoonDockState.IsFullyLandedOnMoon(em, ship))
                    continue;

                if (!TryGetShipPresentation(em, ship, netId, out Vector3 pos, out Quaternion rot, out float scale))
                    continue;

                if (haveLocal && netId != localId)
                {
                    float dist = DroneSwarmLogic.ToroidalDistanceXZ(
                        localPos.x, localPos.z, pos.x, pos.z, mapW, mapH);
                    if (dist > RemoteVisualRange)
                        continue;
                }

                _aliveIds.Add(netId);
                TickShipGroup(em, ship, netId, shipState.Team, pos, rot, scale, timeSeconds, mapW, mapH);
            }

            _removeIds.Clear();
            foreach (var kv in _groups)
            {
                if (!_aliveIds.Contains(kv.Key))
                    _removeIds.Add(kv.Key);
            }

            for (int i = 0; i < _removeIds.Count; i++)
                DestroyGroup(_removeIds[i]);
        }

        void TickShipGroup(
            EntityManager em,
            Entity ship,
            int netId,
            TeamId team,
            Vector3 pos,
            Quaternion rot,
            float scale,
            double timeSeconds,
            float mapW,
            float mapH)
        {
            CollectParkedSeats(em, ship, out var seats, out int fingerprint);
            if (!_groups.TryGetValue(netId, out var group))
            {
                group = new ShipEscortGroup { NetworkId = netId };
                _groups[netId] = group;
            }

            PeopleTransportEscortLogic.GetEscortHullExtents(em, ship, scale, out float extX, out float extZ);
            float3 vel = float3.zero;
            float3 heading = float3.zero;
            if (em.HasComponent<ShipKinematics>(ship))
            {
                var kin = em.GetComponentData<ShipKinematics>(ship);
                vel = kin.Velocity;
                heading = kin.FormationHeading;
            }

            if (group.LayoutFingerprint != fingerprint)
                SyncVisuals(group, seats, fingerprint, team, vel, heading, rot);

            float3 shipPos = new float3(pos.x, 0f, pos.z);
            quaternion shipRot = rot;
            for (int i = 0; i < group.Visuals.Count; i++)
            {
                var vis = group.Visuals[i];
                if (vis.Instance == null)
                    continue;

                // Closed-form home is wrapped sim. The hull pose is presentation (moon cinematic
                // may sit on the surface). Shortest toroidal offset onto that hull — never a
                // display-tile unwrap, and never flatten to world Y=0.28 (that parks orbs inside
                // the planet while the camera is on the moon).
                float3 home = PeopleTransportMath.EvaluateEscortSwarmPose(
                    shipPos, shipRot, extX, extZ, vis.SeatId, vis.Amount, netId,
                    vel, timeSeconds, mapW, mapH, heading);
                Vector3 offset = DroneSwarmLogic.ToroidalOffsetXZ(
                    new Vector3(shipPos.x, 0f, shipPos.z),
                    new Vector3(home.x, 0f, home.z),
                    mapW, mapH);
                Vector3 display = pos + offset;
                display.y = pos.y + LiftY;
                vis.Instance.transform.position = display;
                PeopleTransportVisualApplier.ApplyTravelFacing(vis.Instance.transform, vel);
                float hp = ReadSeatHealth(seats, vis.SeatId);
                PeopleTransportNameplate.Sync(vis.Instance, netId, vis.Amount, hp);
            }

            group.LastShipPos = pos;
            group.HasLastShipPos = true;
        }

        /// <summary>
        /// No ECS gather. Slides already-spawned orbs by the local hull delta so a moon-dock
        /// Instantiates hold cannot leave the swarm at last orbit pose.
        /// </summary>
        void CarryExistingGroupsWithLocalHull()
        {
            if (!ShipDisplayPose.HasLocalPose)
                return;

            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId <= 0 || !_groups.TryGetValue(localId, out var group) || !group.HasLastShipPos)
                return;

            Vector3 now = ShipDisplayPose.LocalPosition;
            Vector3 delta = now - group.LastShipPos;
            if (delta.sqrMagnitude < 0.0000001f)
                return;

            for (int i = 0; i < group.Visuals.Count; i++)
            {
                var vis = group.Visuals[i];
                if (vis.Instance == null)
                    continue;
                vis.Instance.transform.position += delta;
            }

            group.LastShipPos = now;
        }

        static bool IsLocalShipFullyMoonLanded()
        {
            return EcsGameBridge.TryGetLocalShipMoonDockState(out var dock) && dock.IsFullyLanded;
        }

        void HideLocalGroup()
        {
            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId > 0)
                DestroyGroup(localId);
        }

        readonly List<PackedSeat> _seatsScratch = new List<PackedSeat>(12);

        void CollectParkedSeats(
            EntityManager em,
            Entity ship,
            out List<PackedSeat> seats,
            out int fingerprint)
        {
            _seatsScratch.Clear();
            seats = _seatsScratch;

            if (em.HasBuffer<PeopleEscortVitalElement>(ship))
            {
                var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
                for (int i = 0; i < buf.Length; i++)
                {
                    var v = buf[i];
                    if (v.InFlight != 0 || v.Amount <= 0)
                        continue;
                    _seatsScratch.Add(new PackedSeat
                    {
                        SeatId = v.SeatId,
                        Amount = v.Amount,
                        Health = v.Health,
                    });
                }
            }

            // Predicted local ships used to omit escort vitals. CurrentPeople is always
            // ghosted — pack the same ≤12 growing orbs so cargo still shows.
            if (_seatsScratch.Count == 0 &&
                em.HasComponent<ShipState>(ship))
            {
                var shipState = em.GetComponentData<ShipState>(ship);
                int people = shipState.CurrentPeople;
                int chunk = math.max(1, shipState.ShipLevel);
                int count = PeopleTransportMath.GetEscortSlotCount(people, chunk);
                for (int i = 0; i < count; i++)
                {
                    int amount = PeopleTransportMath.GetEscortSlotAmount(people, chunk, i);
                    if (amount <= 0)
                        continue;
                    _seatsScratch.Add(new PackedSeat
                    {
                        SeatId = (byte)i,
                        Amount = amount,
                        Health = PeopleTransportMath.ComputeMaxHealth(amount),
                    });
                }
            }

            fingerprint = Fingerprint(_seatsScratch);
        }

        /// <summary>
        /// Keep meshes that still have a seat. Destroy/create only the delta — a full
        /// wipe respawned every orb at the prefab's upright pose and they all spun back.
        /// </summary>
        void SyncVisuals(
            ShipEscortGroup group,
            List<PackedSeat> seats,
            int fingerprint,
            TeamId team,
            float3 shipVelocity,
            float3 formationHeading,
            Quaternion shipRot)
        {
            _visualsScratch.Clear();
            bool spawnedAll = true;
            Quaternion seedFacing = SeedEscortFacing(shipVelocity, formationHeading, shipRot);

            for (int i = 0; i < seats.Count; i++)
            {
                var seat = seats[i];
                int existing = FindVisualIndex(group.Visuals, seat.SeatId);
                if (existing >= 0)
                {
                    var vis = group.Visuals[existing];
                    if (vis.Instance != null && !Mathf.Approximately(vis.Amount, seat.Amount))
                    {
                        PeopleTransportVisualApplier.ApplyAmountScale(vis.Instance, seat.Amount);
                        vis.Amount = seat.Amount;
                    }

                    _visualsScratch.Add(vis);
                    if (existing != group.Visuals.Count - 1)
                    {
                        group.Visuals[existing] = group.Visuals[group.Visuals.Count - 1];
                    }

                    group.Visuals.RemoveAt(group.Visuals.Count - 1);
                    continue;
                }

                if (_spawnsThisFrame >= MaxSpawnsPerFrame)
                {
                    spawnedAll = false;
                    break;
                }

                var go = PeopleTransportVisualApplier.CreateVisual(null, seat.Amount, team);
                if (go == null)
                    continue;
                go.name = "PeopleTransportEscort";
                go.transform.rotation = seedFacing;
                _spawnsThisFrame++;
                _visualsScratch.Add(new SlotVisual
                {
                    SeatId = seat.SeatId,
                    Amount = seat.Amount,
                    Instance = go,
                });
            }

            for (int i = 0; i < group.Visuals.Count; i++)
            {
                if (group.Visuals[i].Instance != null)
                    Destroy(group.Visuals[i].Instance);
            }

            group.Visuals.Clear();
            for (int i = 0; i < _visualsScratch.Count; i++)
                group.Visuals.Add(_visualsScratch[i]);
            _visualsScratch.Clear();
            group.LayoutFingerprint = spawnedAll ? fingerprint : int.MinValue;
        }

        static int FindVisualIndex(List<SlotVisual> visuals, byte seatId)
        {
            for (int i = 0; i < visuals.Count; i++)
            {
                if (visuals[i].SeatId == seatId)
                    return i;
            }

            return -1;
        }

        static Quaternion SeedEscortFacing(
            float3 shipVelocity,
            float3 formationHeading,
            Quaternion shipRot)
        {
            shipVelocity.y = 0f;
            if (math.lengthsq(shipVelocity) >= 0.16f)
            {
                float3 dir = math.normalize(shipVelocity);
                return Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z), Vector3.up);
            }

            if (PeopleTransportMath.TryGetEscortFormationHeading(formationHeading, out float3 heading))
                return Quaternion.LookRotation(new Vector3(heading.x, 0f, heading.z), Vector3.up);

            float3 nose = (float3)(shipRot * Vector3.forward);
            nose.y = 0f;
            if (math.lengthsq(nose) < 1e-4f)
                return Quaternion.identity;
            nose = math.normalize(nose);
            return Quaternion.LookRotation(new Vector3(nose.x, 0f, nose.z), Vector3.up);
        }

        static int Fingerprint(List<PackedSeat> seats)
        {
            unchecked
            {
                int fp = 17;
                for (int i = 0; i < seats.Count; i++)
                {
                    fp = fp * 31 + seats[i].SeatId;
                    fp = fp * 31 + (int)seats[i].Amount;
                }

                return fp;
            }
        }

        static float ReadSeatHealth(List<PackedSeat> seats, byte seatId)
        {
            for (int i = 0; i < seats.Count; i++)
            {
                if (seats[i].SeatId == seatId)
                    return seats[i].Health;
            }

            return -1f;
        }

        bool TryGetShipPresentation(
            EntityManager em,
            Entity shipEntity,
            int networkId,
            out Vector3 position,
            out Quaternion rotation,
            out float scale)
        {
            position = default;
            rotation = Quaternion.identity;
            scale = 1f;

            int localId = EcsGameBridge.GetLocalNetworkId();
            bool isLocal = localId > 0 && networkId == localId;
            if (isLocal && ShipDisplayPose.HasLocalPose)
            {
                position = ShipDisplayPose.LocalPosition;
                rotation = ShipDisplayPose.LocalRotation;
            }
            else if (shipEntity != Entity.Null &&
                     GhostPresentationTransformCache.TryGetShip(shipEntity, out var snap))
            {
                position = (Vector3)snap.Position;
                rotation = (Quaternion)snap.Rotation;
            }
            else if (EcsGameBridge.TryGetShipSimTransformByNetworkId(networkId, out var lt))
            {
                position = (Vector3)lt.Position;
                rotation = (Quaternion)lt.Rotation;
                scale = lt.Scale > 0.01f ? lt.Scale : 1f;
                return true;
            }
            else
            {
                return false;
            }

            if (shipEntity != Entity.Null && em.HasComponent<LocalTransform>(shipEntity))
            {
                float s = em.GetComponentData<LocalTransform>(shipEntity).Scale;
                if (s > 0.01f)
                    scale = s;
            }

            return true;
        }

        /// <summary>Join-safe parked escort spheres for cosmetic tracers.</summary>
        public static void AppendBulletObstacles(List<BulletCosmeticHitQuery.Obstacle> into)
        {
            if (into == null || s_Instance == null)
                return;

            foreach (var kv in s_Instance._groups)
            {
                var group = kv.Value;
                for (int i = 0; i < group.Visuals.Count; i++)
                {
                    var vis = group.Visuals[i];
                    if (vis.Instance == null || vis.Amount <= 0.01f)
                        continue;
                    Vector3 p = vis.Instance.transform.position;
                    into.Add(new BulletCosmeticHitQuery.Obstacle
                    {
                        Kind = BulletCosmeticHitQuery.ObstacleKind.Transport,
                        SourceEntity = Entity.Null,
                        LogicalCenter = new float3(p.x, 0f, p.z),
                        Radius = PeopleTransportMath.GetEscortHitRadius(vis.Amount),
                        TeamOrOwnership = 0,
                    });
                }
            }
        }

        void DestroyGroup(int netId)
        {
            if (!_groups.TryGetValue(netId, out var group))
                return;
            for (int i = 0; i < group.Visuals.Count; i++)
            {
                if (group.Visuals[i].Instance != null)
                    Destroy(group.Visuals[i].Instance);
            }

            _groups.Remove(netId);
        }

        void ClearAll()
        {
            _removeIds.Clear();
            foreach (var key in _groups.Keys)
                _removeIds.Add(key);
            for (int i = 0; i < _removeIds.Count; i++)
                DestroyGroup(_removeIds[i]);
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
                ComponentType.ReadOnly<GhostOwner>());
            _cachedQueryWorld = world;
            _queriesCreated = true;
        }

        static World ResolveShipWorld()
        {
            World viz = EcsGameBridge.GetVisualizationWorld();
            if (viz != null && viz.IsCreated)
                return viz;
            if (EcsGameBridge.IsLocalHost() &&
                EcsGameBridge.ServerWorld != null &&
                EcsGameBridge.ServerWorld.IsCreated)
                return EcsGameBridge.ServerWorld;
            return EcsGameBridge.ClientWorld;
        }

        void DisposeQueries()
        {
            if (_queriesCreated && _cachedQueryWorld != null && _cachedQueryWorld.IsCreated)
            {
                if (_shipQuery != default)
                    _shipQuery.Dispose();
            }

            _shipQuery = default;
            _queriesCreated = false;
            _cachedQueryWorld = null;
        }
    }
}
