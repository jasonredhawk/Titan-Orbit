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
    /// When the owning ship explodes, parked meshes burst and fly outward
    /// instead of being destroyed in place. Launch uses the death word already
    /// ghosted on the ship (<see cref="ShipDeathVfxState.Packed"/>), same as hull
    /// chunks — no extra RPC or ghost field. Sim still drops the seats.
    /// </summary>
    [DefaultExecutionOrder(67020)]
    public sealed class PeopleTransportEscortPresenter : MonoBehaviour
    {
        const float RemoteVisualRange = 48f;
        const float LiftY = 0.28f;
        /// <summary>At most 12 orbs, and only when the parked layout changes.</summary>
        const int MaxSpawnsPerFrame = 12;
        /// <summary>Cosmetic coast. Hull chunks live until respawn; escorts are small and leave sooner.</summary>
        const float ScatterLifetime = 2.4f;

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

        struct ScatteringEscort
        {
            public GameObject Instance;
            public float3 Position;
            public float3 Velocity;
            public float3 SpinDegPerSec;
            public Vector3 BaseScale;
            public float Age;
            public float LinearDrag;
            public float AngularDrag;
            public bool Planar;
            public bool Wrap;
        }

        sealed class ShipEscortGroup
        {
            public int NetworkId;
            /// <summary>Ship team. Cosmetic tracers skip same-team orbs (server escort scan does too).</summary>
            public byte Team;
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
        readonly List<ScatteringEscort> _scattering = new List<ScatteringEscort>(16);

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

            // Dying escorts keep flying even when ship queries are skipped this frame.
            ToroidalMapEcs.TryGetMapSize(out float scatterMapW, out float scatterMapH);
            TickScattering(
                Time.deltaTime,
                ToroidalMapEcs.IsValidMapSize(scatterMapW, scatterMapH),
                scatterMapW,
                scatterMapH);
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
                int netId = em.GetComponentData<GhostOwner>(ship).NetworkId;
                if (netId == 0)
                    continue;

                // Death: burst parked meshes with the hull. Do this before the moon-dock
                // and range culls, which still delete escorts that are simply not drawn.
                if (shipState.IsDead)
                {
                    ExplodeParkedEscorts(ship, em);
                    continue;
                }

                if (shipState.AwaitingTeamSelection)
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
            CollectParkedSeats(em, ship, netId, out var seats, out int fingerprint);
            if (!_groups.TryGetValue(netId, out var group))
            {
                group = new ShipEscortGroup { NetworkId = netId };
                _groups[netId] = group;
            }

            group.Team = (byte)team;

            PeopleTransportEscortLogic.GetEscortHullExtents(em, ship, scale, out float extX, out float extZ);
            float3 heading = float3.zero;
            if (em.HasComponent<ShipKinematics>(ship))
                heading = em.GetComponentData<ShipKinematics>(ship).FormationHeading;

            if (group.LayoutFingerprint != fingerprint)
                SyncVisuals(group, seats, fingerprint, team, heading, rot);

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
                    timeSeconds, mapW, mapH, heading);
                Vector3 offset = DroneSwarmLogic.ToroidalOffsetXZ(
                    new Vector3(shipPos.x, 0f, shipPos.z),
                    new Vector3(home.x, 0f, home.z),
                    mapW, mapH);
                Vector3 display = pos + offset;
                display.y = pos.y + LiftY;
                vis.Instance.transform.position = display;
                float3 face = PeopleTransportMath.ResolveFormationForward(shipRot, heading);
                vis.Instance.transform.rotation = DroneSwarmPositioning.SlerpIdleFacing(
                    vis.Instance.transform.rotation,
                    new Vector3(face.x, 0f, face.z),
                    Time.deltaTime);
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
            int networkId,
            out List<PackedSeat> seats,
            out int fingerprint)
        {
            _seatsScratch.Clear();
            seats = _seatsScratch;

            int people = 0;
            int chunk = 1;
            bool haveShip = em.HasComponent<ShipState>(ship);
            if (haveShip)
            {
                var shipState = em.GetComponentData<ShipState>(ship);
                people = shipState.CurrentPeople;
                chunk = ResolveDisplayChunk(em, ship, in shipState, networkId);
            }

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

            // Ghost Amount can lag CurrentPeople on a dedicated client. When the parked
            // count still matches the pack, raise a stale amount so the orb grows as
            // people land. Never shrink a seat the server already packed larger.
            if (haveShip && _seatsScratch.Count > 0)
            {
                int expect = PeopleTransportMath.GetEscortSlotCount(people, chunk);
                if (expect == _seatsScratch.Count)
                {
                    for (int i = 0; i < _seatsScratch.Count; i++)
                    {
                        var seat = _seatsScratch[i];
                        int amount = PeopleTransportMath.GetEscortSlotAmount(people, chunk, i);
                        if (amount <= seat.Amount)
                            continue;
                        seat.Amount = amount;
                        _seatsScratch[i] = seat;
                    }
                }
            }

            // Predicted local ships used to omit escort vitals. CurrentPeople is always
            // ghosted — pack the same ≤12 growing orbs so cargo still shows.
            if (_seatsScratch.Count == 0 && haveShip && people > 0)
            {
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
        /// Pack size the parked orbs should grow toward. Server latches
        /// <c>shipLevel × planetLevel</c> (troop-commander bonus included). Clients
        /// recompute that from the orbit planet because the latch is not ghosted.
        /// </summary>
        static int ResolveDisplayChunk(
            EntityManager em,
            Entity ship,
            in ShipState shipState,
            int networkId)
        {
            int shipLevel = math.max(1, shipState.ShipLevel);
            int planetLevel = 1;
            if (em.HasComponent<ShipOrbitState>(ship))
            {
                int planetId = em.GetComponentData<ShipOrbitState>(ship).OrbitPlanetId;
                if (planetId != 0 &&
                    EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out _, out _, out var planet))
                    planetLevel = math.max(1, planet.PlanetLevel);
            }

            int raw = PeopleTransportMath.GetTransferChunk(shipLevel, planetLevel);
            bool topTroop = ShipTopOfTeamRoles.IsTransporter(shipState.Team, networkId);
            return TeamCommandRoleRules.ScaleTransferChunk(raw, topTroop);
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
            float3 formationHeading,
            Quaternion shipRot)
        {
            _visualsScratch.Clear();
            bool spawnedAll = true;
            Quaternion seedFacing = SeedEscortFacing(formationHeading, shipRot);

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

        static Quaternion SeedEscortFacing(float3 formationHeading, Quaternion shipRot)
        {
            float3 face = PeopleTransportMath.ResolveFormationForward(shipRot, formationHeading);
            if (math.lengthsq(face) < 1e-4f)
                return Quaternion.identity;
            return Quaternion.LookRotation(new Vector3(face.x, 0f, face.z), Vector3.up);
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

        /// <summary>
        /// Join-safe parked escort spheres for cosmetic tracers.
        /// Team and owner must be set — a zero team makes the cannon beam and tracers
        /// treat the orb as hostile and stop on the shooter's own troop drones.
        /// </summary>
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
                        TeamOrOwnership = group.Team,
                        OwnerNetworkId = group.NetworkId,
                    });
                }
            }
        }

        /// <summary>
        /// Range cull, moon dock, and team select. Ship death does not use this —
        /// <see cref="ExplodeParkedEscorts"/> bursts those meshes.
        /// </summary>
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

        /// <summary>
        /// Launch every parked escort mesh on a dying ship. Idempotent: the group is
        /// removed on the first call, so the hull breakup and this presenter can both
        /// ask in the same frame. In-flight hops are not in this group.
        /// Motion is <see cref="ShipDeathDebrisMath.ComputeClusterLaunch"/> from the
        /// existing packed death word. Nothing is replicated for the scatter.
        /// </summary>
        public static void ExplodeParkedEscorts(Entity ship, EntityManager em)
        {
            if (s_Instance == null || ship == Entity.Null || !em.Exists(ship))
                return;
            if (!em.HasComponent<ShipState>(ship) || !em.GetComponentData<ShipState>(ship).IsDead)
                return;
            if (!em.HasComponent<GhostOwner>(ship))
                return;

            int netId = em.GetComponentData<GhostOwner>(ship).NetworkId;
            if (netId == 0)
                return;
            s_Instance.ExplodeGroup(netId, ship, em);
        }

        void ExplodeGroup(int netId, Entity ship, EntityManager em)
        {
            if (!_groups.TryGetValue(netId, out var group) || group.Visuals.Count == 0)
            {
                _groups.Remove(netId);
                return;
            }

            // Take the group off the live set before spawning bursts so a second call
            // this frame cannot launch the same meshes again.
            _groups.Remove(netId);

            bool haveMap = ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH)
                           && ToroidalMapEcs.IsValidMapSize(mapW, mapH);
            bool haveShipPos = TryGetShipPresentation(
                em, ship, netId, out Vector3 shipPos, out _, out float shipScale);
            if (!haveShipPos)
                shipPos = CentroidOf(group);

            // Same 4-byte death word the hull chunks unpack. If it has not arrived yet,
            // the ship network id is a stable fallback — still no extra egress.
            // Ship velocity is left at zero: death recording already clears it, and a
            // predicted leftover would make only the local client diverge.
            uint packed = 0;
            if (em.HasComponent<ShipDeathVfxState>(ship))
                packed = em.GetComponentData<ShipDeathVfxState>(ship).Packed;
            if (packed == 0)
                packed = ShipDeathVfxState.Pack((uint)netId, float2.zero, 0f);

            float hullRadius = BodyCollisionMath.GetShipHullRadiusWorld(math.max(0.25f, shipScale));
            TeamId team = em.HasComponent<ShipState>(ship)
                ? em.GetComponentData<ShipState>(ship).Team
                : TeamId.None;

            for (int i = 0; i < group.Visuals.Count; i++)
            {
                SlotVisual vis = group.Visuals[i];
                if (vis.Instance == null)
                    continue;

                var plate = vis.Instance.GetComponent<PeopleTransportNameplate>();
                if (plate != null)
                    plate.enabled = false;

                Vector3 world = vis.Instance.transform.position;
                float3 offset = EscortOffsetFromShip(shipPos, world, haveMap, mapW, mapH);
                bool launched = ShipDeathDebrisDriver.TryLaunchTroopTransport(
                    packed,
                    vis.SeatId,
                    offset,
                    hullRadius,
                    float3.zero,
                    out float3 velocity,
                    out float3 spin,
                    out float linearDrag,
                    out float angularDrag,
                    out bool planar);
                if (!launched)
                    FallbackLaunch(vis.SeatId, offset, out velocity, out spin, out linearDrag, out angularDrag, out planar);

                Vector3 lossy = vis.Instance.transform.lossyScale;
                float extent = math.max(math.abs(lossy.x), math.max(math.abs(lossy.y), math.abs(lossy.z)));
                float burstScale = math.clamp(extent * 4.2f, 0.7f, 1.75f);
                ShipDeathDebrisDriver.PlayTroopTransportBurst(world, team, burstScale);

                bool wrap = haveMap
                            && math.abs(world.x) <= mapW * 0.5f + 0.01f
                            && math.abs(world.z) <= mapH * 0.5f + 0.01f;
                _scattering.Add(new ScatteringEscort
                {
                    Instance = vis.Instance,
                    Position = world,
                    Velocity = velocity,
                    SpinDegPerSec = spin,
                    BaseScale = vis.Instance.transform.localScale,
                    Age = 0f,
                    LinearDrag = linearDrag,
                    AngularDrag = angularDrag,
                    Planar = planar,
                    Wrap = wrap,
                });
            }

            group.Visuals.Clear();
        }

        static Vector3 CentroidOf(ShipEscortGroup group)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int i = 0; i < group.Visuals.Count; i++)
            {
                if (group.Visuals[i].Instance == null)
                    continue;
                sum += group.Visuals[i].Instance.transform.position;
                count++;
            }

            return count > 0 ? sum / count : Vector3.zero;
        }

        /// <summary>Shortest XZ offset from the ship to this escort. Y is the visual height.</summary>
        static float3 EscortOffsetFromShip(
            Vector3 shipPos,
            Vector3 world,
            bool haveMap,
            float mapW,
            float mapH)
        {
            Vector3 raw = haveMap
                ? DroneSwarmLogic.ToroidalOffsetXZ(
                    new Vector3(shipPos.x, 0f, shipPos.z),
                    new Vector3(world.x, 0f, world.z),
                    mapW,
                    mapH)
                : new Vector3(world.x - shipPos.x, 0f, world.z - shipPos.z);
            return new float3(raw.x, world.y - shipPos.y, raw.z);
        }

        /// <summary>
        /// Used only when the debris driver is not up. Seat index picks a heading so
        /// the transports still leave on different rays. No clock, no network.
        /// </summary>
        static void FallbackLaunch(
            int seatId,
            float3 offset,
            out float3 velocity,
            out float3 spinDegPerSec,
            out float linearDrag,
            out float angularDrag,
            out bool planar)
        {
            float3 dir = new float3(offset.x, 0f, offset.z);
            if (math.lengthsq(dir) < 0.02f)
            {
                float ang = (seatId + 0.5f) * (math.PI * 2f / 12f);
                math.sincos(ang, out float s, out float c);
                dir = new float3(c, 0f, s);
            }
            else
                dir = math.normalize(dir);

            velocity = dir * 12f;
            spinDegPerSec = new float3(0f, seatId % 2 == 0 ? 180f : -180f, 0f);
            linearDrag = 0.72f;
            angularDrag = 0.35f;
            planar = false;
        }

        void TickScattering(float dt, bool haveMap, float mapW, float mapH)
        {
            if (dt <= 0f || _scattering.Count == 0)
                return;

            for (int i = _scattering.Count - 1; i >= 0; i--)
            {
                ScatteringEscort fly = _scattering[i];
                if (fly.Instance == null)
                {
                    _scattering.RemoveAt(i);
                    continue;
                }

                fly.Age += dt;
                if (fly.Age >= ScatterLifetime)
                {
                    Destroy(fly.Instance);
                    _scattering.RemoveAt(i);
                    continue;
                }

                // Same damping as hull chunks. Planar deaths stay on the map.
                ShipDeathDebrisMath.IntegrateDrag(
                    ref fly.Velocity,
                    ref fly.SpinDegPerSec,
                    dt,
                    fly.LinearDrag,
                    fly.AngularDrag);
                fly.Position += fly.Velocity * dt;
                if (fly.Planar)
                    fly.Position.y = fly.Instance.transform.position.y;
                if (fly.Wrap && haveMap)
                    fly.Position = ToroidalMapEcs.Wrap(fly.Position, mapW, mapH);

                float remain = 1f - fly.Age / ScatterLifetime;
                float shrink = remain < 0.28f ? math.lerp(0.2f, 1f, remain / 0.28f) : 1f;
                fly.Instance.transform.position = fly.Position;
                fly.Instance.transform.localScale = fly.BaseScale * shrink;
                fly.Instance.transform.rotation = Quaternion.Euler(
                    fly.SpinDegPerSec.x * dt,
                    fly.SpinDegPerSec.y * dt,
                    fly.SpinDegPerSec.z * dt) * fly.Instance.transform.rotation;
                _scattering[i] = fly;
            }
        }

        void ClearAll()
        {
            _removeIds.Clear();
            foreach (var key in _groups.Keys)
                _removeIds.Add(key);
            for (int i = 0; i < _removeIds.Count; i++)
                DestroyGroup(_removeIds[i]);
            ClearScattering();
        }

        void ClearScattering()
        {
            for (int i = 0; i < _scattering.Count; i++)
            {
                if (_scattering[i].Instance != null)
                    Destroy(_scattering[i].Instance);
            }

            _scattering.Clear();
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
