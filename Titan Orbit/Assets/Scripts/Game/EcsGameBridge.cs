using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
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
    /// MonoBehaviour-safe read API for UI, camera, and bridges. Resolves the correct
    /// NetCode world (client vs host server) and exposes match/map/planet state.
    /// <para>
    /// Map-load static latches are cleared on play-mode / assembly reload and whenever
    /// NetworkStreamInGame drops — otherwise a second Play (especially with Domain Reload
    /// disabled) can skip the loading screen and jump to Join Team with no map GOs.
    /// </para>
    /// </summary>
    public static class EcsGameBridge
    {
        /// <summary>NetCode client simulation world — prediction and local input run here.</summary>
        public static World ClientWorld => ClientServerBootstrap.ClientWorld;

        /// <summary>NetCode server simulation world — authoritative on dedicated server and local host.</summary>
        public static World ServerWorld => ClientServerBootstrap.ServerWorld;

        /// <summary>
        /// [UNITY] Clears map-load statics when entering Play Mode without Domain Reload
        /// (Enter Play Mode Options). Without this, <c>s_MapLoadingLatchedComplete</c> from the
        /// previous Play stays true and Join Team appears before the new map builds.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsForPlayMode()
        {
            ResetRemoteMapLoadTracking();
            s_WasNetworkInGame = false;
            ClientJoinSettleCache.Clear();
            GemClientEntityRegistry.Clear();
            PlanetClientEntityRegistry.Clear();
            PlanetConnectionGraphCache.Clear();
            PlanetConnectionPresentationTriangles.Clear();
            s_PlanetStateByIdCache.Clear();
            s_PlanetStateCacheFrame = -1;
            InvalidateLocalPlayerShipFrameCache();
        }

        // --- Per-frame local ship cache (avoids N× CreateEntityQuery in HUD / dock / deposit) ---

        /// <summary>Frame stamp for <see cref="s_LocalPlayerShipEntity"/>.</summary>
        static int s_LocalPlayerShipCacheFrame = -1;

        /// <summary>Cached local ship entity for this frame (ClientWorld / prediction world).</summary>
        static Entity s_LocalPlayerShipEntity;

        /// <summary>World used when <see cref="s_LocalPlayerShipEntity"/> was resolved.</summary>
        static World s_LocalPlayerShipCacheWorld;

        /// <summary>Clears the per-frame local-ship entity cache (Play Mode / leave in-game).</summary>
        static void InvalidateLocalPlayerShipFrameCache()
        {
            s_LocalPlayerShipCacheFrame = -1;
            s_LocalPlayerShipEntity = Entity.Null;
            s_LocalPlayerShipCacheWorld = null;
        }

        /// <summary>
        /// Resolves the local player ship entity once per frame via <see cref="LocalPlayerShipTag"/>.
        /// Callers then <c>GetComponentData</c> — avoids CreateEntityQuery per HUD/dock/deposit read
        /// (MoonOrbitStationController + presenter were allocating several queries every Update).
        /// </summary>
        static bool TryGetCachedLocalPlayerShipEntity(out EntityManager em, out Entity shipEntity)
        {
            em = default;
            shipEntity = Entity.Null;

            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            em = world.EntityManager;

            if (s_LocalPlayerShipCacheFrame == Time.frameCount &&
                s_LocalPlayerShipCacheWorld == world &&
                s_LocalPlayerShipEntity != Entity.Null &&
                em.Exists(s_LocalPlayerShipEntity) &&
                em.HasComponent<LocalPlayerShipTag>(s_LocalPlayerShipEntity))
            {
                shipEntity = s_LocalPlayerShipEntity;
                return true;
            }

            s_LocalPlayerShipCacheFrame = Time.frameCount;
            s_LocalPlayerShipCacheWorld = world;
            s_LocalPlayerShipEntity = Entity.Null;

            using (var tagged = em.CreateEntityQuery(typeof(LocalPlayerShipTag), typeof(ShipTag)))
            {
                if (tagged.CalculateEntityCount() != 1)
                    return false;
                s_LocalPlayerShipEntity = tagged.GetSingletonEntity();
            }

            shipEntity = s_LocalPlayerShipEntity;
            return shipEntity != Entity.Null;
        }

        // --- World selection ---

        /// <summary>
        /// ECS world used for rendering, camera follow, and GameObject proxy sync.
        /// Host and dedicated clients both read ClientWorld so proxies use NetCode presentation
        /// (owner prediction + remote interpolation), not raw ServerWorld simulation ticks.
        /// </summary>
        public static World GetVisualizationWorld()
        {
            // --- Preferred: client presentation world once NetCode is in-game ---
            // [NETCODE] ClientWorld owns ghost presentation — interpolation for remotes, prediction for local owner.
            if (ClientWorld != null && ClientWorld.IsCreated &&
                TitanOrbitSessionManager.IsClientGameplayReady(ClientWorld))
                return ClientWorld;

            // --- Headless dedicated: no GameObject presentation ---
            // [TITAN-ORBIT] Headless dedicated — never drive GameObject proxies from ServerWorld.
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return null;

            // --- Dual-world Local Host before client is in-game ---
            // [TITAN-ORBIT] basics15 (H27/H28): while ClientWorld existed but was not gameplay-ready,
            // the old ServerWorld fallback spawned ~454 asteroid proxies, then ClientWorld rebuild
            // tore them down (102ms + 78ms DrawAsteroids spikes). Wait for ClientWorld instead.
            // IsLocalHost() cannot be used here — it itself requires gameplay-ready.
            bool dualWorldPresent =
                !TitanOrbitSessionManager.IsDedicatedOnlineClient &&
                ClientWorld != null && ClientWorld.IsCreated &&
                ServerWorld != null && ServerWorld.IsCreated;
            if (dualWorldPresent)
                return null;

            // --- Client-only join / single-world edge cases ---
            // [NETCODE] No dual-world wait: allow ServerWorld only when there is no ClientWorld
            // (legacy host tools). Prefer ClientWorld when it exists even before in-game.
            if (ClientWorld != null && ClientWorld.IsCreated)
                return ClientWorld;

            if (ServerWorld != null && ServerWorld.IsCreated)
                return ServerWorld;

            return null;
        }

        /// <summary>
        /// World that owns the local player's ship ghost tags and predicted pose.
        /// ClientWorld for host + dedicated clients; visualization world otherwise.
        /// </summary>
        public static World GetLocalPlayerShipWorld()
        {
            if (ClientWorld != null && ClientWorld.IsCreated &&
                (IsLocalHost() || TitanOrbitSessionManager.IsDedicatedOnlineClient))
                return ClientWorld;

            return GetVisualizationWorld();
        }

        // --- Local ship queries ---

        /// <summary>
        /// World position of the local ship for UI/aim — moon-dock cinematic, then client-world ECS pose.
        /// </summary>
        public static bool TryGetLocalShipPosition(out Vector3 position)
        {
            if (ShipMoonDockVisualApplier.TryGetLocalFollowPosition(out position))
                return true;

            // [TITAN-ORBIT] Prefer presentation pose when ship ECS queries are gated (GhostSpawnBacklog).
            // Otherwise HasLocalPlayerShip / HUD flicker during gem Instantiates after asteroid destroy.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries && ShipDisplayPose.HasLocalPose)
            {
                position = ShipDisplayPose.LocalPosition;
                return true;
            }

            if (TryGetLocalShipTransform(out var lt))
            {
                position = lt.Position;
                return true;
            }

            if (ShipDisplayPose.HasLocalPose)
            {
                position = ShipDisplayPose.LocalPosition;
                return true;
            }

            position = default;
            return false;
        }

        /// <summary>
        /// Visual proxy pose when available (onBeforeRender sync); otherwise same ECS pose as <see cref="TryGetLocalShipPosition"/>.
        /// </summary>
        public static bool TryGetLocalShipPresentationPosition(out Vector3 position)
        {
            if (ShipDisplayPose.HasLocalPose)
            {
                position = ShipDisplayPose.LocalPosition;
                return true;
            }

            return TryGetLocalShipPosition(out position);
        }

        /// <summary>Local ship <see cref="LocalTransform"/> from <see cref="GetLocalPlayerShipWorld"/>.</summary>
        public static bool TryGetLocalShipTransform(out LocalTransform transform) =>
            TryGetLocalShipTransformFromWorld(GetLocalPlayerShipWorld(), out transform);

        /// <summary>
        /// Live ship <see cref="LocalTransform"/> looked up by <see cref="GhostOwner.NetworkId"/>.
        /// <para>
        /// [HYBRID] People-transport VFX magnet uses this every frame so load floats chase the
        /// current hull instead of the spawn-time baked <c>TargetPosition</c>. Ships are few —
        /// safe to scan (unlike asteroid/planet <c>ToEntityArray</c>, which Crash!!! on Windows).
        /// Prefers <see cref="GetLocalPlayerShipWorld"/> (predicted local / interpolated remotes).
        /// </para>
        /// </summary>
        /// <param name="networkId">Ghost owner network id (same id server puts on load transports).</param>
        /// <param name="transform">Ship pose when found.</param>
        /// <returns>True when a ship ghost with that owner exists in the client ship world.</returns>
        public static bool TryGetShipSimTransformByNetworkId(int networkId, out LocalTransform transform)
        {
            transform = default;
            if (networkId <= 0)
                return false;

            // --- Prefer client ship world (prediction for local owner) ---
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                world = ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            return TryGetShipTransformByNetworkId(world.EntityManager, networkId, out transform);
        }

        /// <summary>
        /// Resolves local ship pose from a specific ECS world using tag, ownership, CommandTarget, and NetworkId fallbacks.
        /// </summary>
        public static bool TryGetLocalShipTransformFromWorld(World world, out LocalTransform transform)
        {
            transform = default;

            // [TITAN-ORBIT] Team picker / rejoin screens hide ship control until player commits to a team.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return false;

            // [TITAN-ORBIT] Ship ToEntityArray during post–Join Team Instantiates Crash!!!
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            // [TITAN-ORBIT] No local ship camera/control until the galaxy build finishes.
            if (IsNetworkInGame() && !IsMapLoadingComplete())
                return false;

            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;

            // --- Fallback chain: explicit tag → NetCode local owner → command target → network id ---
            if (TryGetShipTransform(em, ComponentType.ReadOnly<LocalPlayerShipTag>(), out transform))
                return true;

            if (TryGetShipTransform(em, ComponentType.ReadOnly<GhostOwnerIsLocal>(), out transform))
                return true;

            if (TryGetShipFromCommandTarget(em, out transform))
                return true;

            int localId = GetLocalNetworkId(ClientWorld);
            if (localId > 0 && TryGetShipTransformByNetworkId(em, localId, out transform))
                return true;

            return false;
        }

        /// <summary>Gameplay velocity mirror from <see cref="ShipKinematics"/> on the local ship entity.</summary>
        public static bool TryGetLocalShipVelocity(out Vector3 velocity)
        {
            velocity = default;

            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!TryGetLocalShipEntity(em, out var shipEntity) ||
                !em.HasComponent<ShipKinematics>(shipEntity))
                return false;

            velocity = em.GetComponentData<ShipKinematics>(shipEntity).Velocity;
            return true;
        }

        /// <summary>True when map is loaded, team flow allows control, and a local ship position resolves.</summary>
        public static bool HasLocalPlayerShip()
        {
            if (!IsMapLoadingComplete() || ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return false;

            // [TITAN-ORBIT] During GhostSpawnBacklog (e.g. gem Instantiates after asteroid destroy),
            // ship ECS queries are skipped to avoid Crash!!!. Presentation pose still means we have
            // a ship — without this, NceGameFlow hides gameplay HUD and flashes the lobby overlay.
            if (ShipDisplayPose.HasLocalPose)
                return true;

            // --- Instantiates-hook seed (pose may land one frame later) ---
            // [TITAN-ORBIT] After Join Team, seed is set the Instantiates frame; ShipDisplayPose may
            // not publish until ShipVisualSync runs. Treat seed as "have a ship" so spawn-wait UI
            // does not stick on "Spawning your ship..." for that gap.
            var client = ClientWorld;
            if (client != null && client.IsCreated)
            {
                var em = client.EntityManager;
                if (LocalShipEntitySeed.TryGetSeededShip(em, out _))
                    return true;

                // --- Recover if Instantiates-hook seed was missed (idle Instantiates only) ---
                // [TITAN-ORBIT] Old gate TeamChoiceConfirmed&&!seed made ShouldSkip forever when the
                // hook missed — lobby stuck on semi-transparent "Spawning your ship...".
                if (LocalShipEntitySeed.TryRecoverOwnedShip(em) &&
                    LocalShipEntitySeed.TryGetSeededShip(em, out _))
                    return true;
            }

            return TryGetLocalShipPosition(out _);
        }

        /// <summary>
        /// True when the server still has this player's ship from a prior session on the same match.
        /// </summary>
        public static bool TryGetRejoinableShipForLocalPlayer(out ShipState shipState)
        {
            shipState = default;
            var world = ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            int localId = GetLocalNetworkId(world);
            if (localId <= 0)
                return false;

            if (!TryGetShipStateByNetworkId(world.EntityManager, localId, out shipState))
                return false;

            return shipState.Team != TeamId.None && !shipState.AwaitingTeamSelection;
        }

        /// <summary>
        /// Full <see cref="ShipState"/> for HUD and UI — tries LocalPlayerShipTag, ownership, CommandTarget, NetworkId.
        /// <para>
        /// [TITAN-ORBIT] Tiny <see cref="LocalPlayerShipTag"/> singleton read stays allowed during
        /// <see cref="ClientJoinSettleCache.GhostSpawnBacklog"/> (gem Instantiates on Windows).
        /// Broad ship gathers stay gated — without the tagged path, deposit metronome / orbit menu
        /// could not seed cargo and deposit SFX went silent on the Windows client.
        /// </para>
        /// </summary>
        public static bool TryGetLocalShipState(out ShipState state)
        {
            state = default;

            // --- One tagged entity resolve per frame, then component read ---
            if (TryGetCachedLocalPlayerShipEntity(out var em, out var shipEntity) &&
                em.HasComponent<ShipState>(shipEntity))
            {
                state = em.GetComponentData<ShipState>(shipEntity);
                return true;
            }

            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            em = world.EntityManager;

            // [TITAN-ORBIT] Broader ownership / NetworkId gathers Crash!!! during Instantiates.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            if (TryGetLocalOwnedShipEntity(em, out var ownedShip) &&
                em.HasComponent<ShipState>(ownedShip))
            {
                state = em.GetComponentData<ShipState>(ownedShip);
                return true;
            }

            if (TryGetShipStateFromCommandTarget(em, out state))
                return true;

            int localId = GetLocalNetworkId(ClientWorld);
            if (localId > 0 && TryGetShipStateByNetworkId(em, localId, out state))
                return true;

            return false;
        }

        /// <summary>
        /// Bottom-bar attribute upgrade levels for the local ship (zeros when component missing).
        /// Prefers a tiny <see cref="LocalPlayerShipTag"/> lookup so combat gem Instantiates
        /// (<see cref="ClientJoinSettleCache.GhostSpawnBacklog"/>) do not blank the upgrade HUD.
        /// </summary>
        public static bool TryGetLocalShipAttributeUpgrades(out ShipAttributeUpgradeState attributes)
        {
            attributes = default;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;

            // --- Tiny tagged lookup first (safe during GhostSpawnBacklog) ---
            // [TITAN-ORBIT] TryGetLocalShipEntity scans all ships and is gated off during Instantiates
            // (asteroid destroy → gem ghosts). Without this path, ShipAttributeUpgradeHUD set attrs
            // to default and flashed empty tick marks every burst.
            using (var tagged = em.CreateEntityQuery(typeof(LocalPlayerShipTag), typeof(ShipTag)))
            {
                if (tagged.CalculateEntityCount() == 1)
                {
                    var shipEntity = tagged.GetSingletonEntity();
                    if (!em.HasComponent<ShipAttributeUpgradeState>(shipEntity))
                        return true;
                    attributes = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);
                    return true;
                }
            }

            // --- Broader resolve (skipped during Settling / GhostSpawnBacklog — Crash!!! risk) ---
            if (!TryGetLocalShipEntity(em, out var resolvedShip))
                return false;

            if (!em.HasComponent<ShipAttributeUpgradeState>(resolvedShip))
                return true;

            attributes = em.GetComponentData<ShipAttributeUpgradeState>(resolvedShip);
            return true;
        }

        /// <summary>Match timer and started flag from <see cref="MatchStateSingleton"/>.</summary>
        public static bool TryGetMatchState(out MatchStateSingleton match)
        {
            match = default;
            var world = ClientWorld ?? ServerWorld;
            if (world == null || !world.IsCreated)
                return false;

            using var query = world.EntityManager.CreateEntityQuery(typeof(MatchStateSingleton));
            return query.TryGetSingleton(out match);
        }

        /// <summary>Death / respawn timer state for the local ship — drives death screen UI.</summary>
        public static bool TryGetLocalShipDeathState(out ShipDeathState death)
        {
            death = default;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (TryGetLocalShipEntity(em, out var shipEntity) &&
                em.HasComponent<ShipDeathState>(shipEntity))
            {
                death = em.GetComponentData<ShipDeathState>(shipEntity);
                return true;
            }

            return false;
        }

        /// <summary>Planet orbit slot state for moon-orbit station UI and camera.</summary>
        public static bool TryGetLocalShipOrbitState(out ShipOrbitState orbitState)
        {
            orbitState = default;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            using var tagged = em.CreateEntityQuery(typeof(LocalPlayerShipTag), typeof(ShipOrbitState));
            if (tagged.CalculateEntityCount() > 0)
            {
                orbitState = tagged.GetSingleton<ShipOrbitState>();
                return true;
            }

            if (TryGetLocalOwnedShipEntity(em, out var ownedShip) &&
                em.HasComponent<ShipOrbitState>(ownedShip))
            {
                orbitState = em.GetComponentData<ShipOrbitState>(ownedShip);
                return true;
            }

            int localId = GetLocalNetworkId(ClientWorld);
            if (localId > 0 && TryGetShipOrbitStateByNetworkId(em, localId, out orbitState))
                return true;

            return false;
        }

        /// <summary>
        /// Moon landing cinematic progress — used by dock visual applier and camera follow.
        /// Prefers a tiny <see cref="LocalPlayerShipTag"/> read so Windows gem Instantiates
        /// (<see cref="ClientJoinSettleCache.GhostSpawnBacklog"/>) do not blank dock / deposit.
        /// </summary>
        public static bool TryGetLocalShipMoonDockState(out ShipMoonDockState moonDock)
        {
            moonDock = default;

            if (TryGetCachedLocalPlayerShipEntity(out var em, out var shipEntity) &&
                em.HasComponent<ShipMoonDockState>(shipEntity))
            {
                moonDock = em.GetComponentData<ShipMoonDockState>(shipEntity);
                return true;
            }

            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            em = world.EntityManager;
            if (TryGetLocalShipEntity(em, out shipEntity) &&
                em.HasComponent<ShipMoonDockState>(shipEntity))
            {
                moonDock = em.GetComponentData<ShipMoonDockState>(shipEntity);
                return true;
            }

            return false;
        }

        /// <summary>Resolves local ship <see cref="Entity"/> in an arbitrary world (host diagnostics).</summary>
        public static bool TryGetLocalShipEntityOnWorld(World world, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (world == null || !world.IsCreated)
                return false;

            return TryGetLocalShipEntity(world.EntityManager, out shipEntity);
        }

        /// <summary>
        /// Last applied <see cref="ShipInput"/> on the local ship ghost (client prediction world).
        /// Prefers <see cref="LocalPlayerShipTag"/> so thrust-to-undock still works during gem Instantiates.
        /// </summary>
        public static bool TryGetLocalShipInput(out ShipInput input)
        {
            input = default;

            if (TryGetCachedLocalPlayerShipEntity(out var em, out var shipEntity) &&
                em.HasComponent<ShipInput>(shipEntity))
            {
                input = em.GetComponentData<ShipInput>(shipEntity);
                return true;
            }

            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            em = world.EntityManager;
            if (TryGetLocalShipEntity(em, out shipEntity) &&
                em.HasComponent<ShipInput>(shipEntity))
            {
                input = em.GetComponentData<ShipInput>(shipEntity);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Client mirror of server load eligibility: orbit ring + idle + same source planet.
        /// Used by <see cref="PeopleTransportVfxDriver"/> to retarget load spheres home when the
        /// ship leaves the friendly orbit ring, then chase the ship again when it returns.
        /// Ships-only query (safe under TransformQuarantine).
        /// </summary>
        /// <param name="shipNetworkId">Destination ship from the load spawn RPC.</param>
        /// <param name="sourcePlanetId">Planet the transport left.</param>
        /// <param name="eligible">True when the sphere should keep magnet-steering to the ship.</param>
        /// <returns>False when ship/planet data is missing (caller should keep last target).</returns>
        public static bool TryIsShipEligibleForPeopleLoad(int shipNetworkId, int sourcePlanetId, out bool eligible)
        {
            eligible = false;
            if (shipNetworkId <= 0 || sourcePlanetId == 0)
                return false;

            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                world = ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!TryGetShipPeopleLoadContext(em, shipNetworkId, out var shipState, out var shipInput,
                    out var shipOrbit, out var moonDock, out var shipTransform))
                return false;

            if (!TryGetPlanetPoseByPlanetId(sourcePlanetId, out float3 planetPos, out float planetScale, out var planetState))
                return false;

            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return false;
            float planetSize = math.max(0.5f, planetScale);
            eligible = PeopleTransportSimulationSystem.IsShipEligibleForLoad(
                shipState, shipInput, shipOrbit, moonDock, shipTransform.Position, planetPos,
                planetSize, planetState.PlanetLevel, sourcePlanetId, mapW, mapH);
            return true;
        }

        /// <summary>Whether the player is holding deposit — gem economy HUD indicator.</summary>
        public static bool TryGetLocalShipDepositIntent(out bool wantDepositGems)
        {
            wantDepositGems = false;

            if (TryGetCachedLocalPlayerShipEntity(out var em, out var shipEntity) &&
                em.HasComponent<ShipDepositIntent>(shipEntity))
            {
                wantDepositGems = em.GetComponentData<ShipDepositIntent>(shipEntity).WantDepositGems;
                return true;
            }

            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            em = world.EntityManager;
            if (TryGetLocalShipEntity(em, out shipEntity) &&
                em.HasComponent<ShipDepositIntent>(shipEntity))
            {
                wantDepositGems = em.GetComponentData<ShipDepositIntent>(shipEntity).WantDepositGems;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Ghosted deposit metronome feedback for the local ship (server BeatSequence / chunk).
        /// Prefers cached <see cref="LocalPlayerShipTag"/> entity so Instantiates do not blank SFX.
        /// </summary>
        public static bool TryGetLocalShipDepositFeedback(out ShipDepositFeedback feedback)
        {
            feedback = default;

            if (TryGetCachedLocalPlayerShipEntity(out var em, out var shipEntity) &&
                em.HasComponent<ShipDepositFeedback>(shipEntity))
            {
                feedback = em.GetComponentData<ShipDepositFeedback>(shipEntity);
                return true;
            }

            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            em = world.EntityManager;
            if (TryGetLocalShipEntity(em, out shipEntity) &&
                em.HasComponent<ShipDepositFeedback>(shipEntity))
            {
                feedback = em.GetComponentData<ShipDepositFeedback>(shipEntity);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves the local ship entity for deposit intent writes.
        /// Client: prefers <see cref="LocalPlayerShipTag"/> (safe during GhostSpawnBacklog).
        /// Server (Local Host): matches <see cref="GhostOwner.NetworkId"/> — server ships never
        /// get <see cref="LocalPlayerShipTag"/> (that tag is client-only).
        /// </summary>
        public static bool TryGetLocalShipEntityTagged(World world, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;

            // --- Client prediction world: tiny tagged lookup ---
            if (world.IsClient())
            {
                using (var tagged = em.CreateEntityQuery(typeof(LocalPlayerShipTag), typeof(ShipTag)))
                {
                    if (tagged.CalculateEntityCount() == 1)
                    {
                        shipEntity = tagged.GetSingletonEntity();
                        return true;
                    }
                }

                if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                    return false;

                return TryGetLocalShipEntity(em, out shipEntity);
            }

            // --- Server world (Local Host): NetworkId match — no LocalPlayerShipTag here ---
            int localId = GetLocalNetworkId(ClientWorld);
            if (localId <= 0)
                localId = GetLocalNetworkId(world);
            if (localId <= 0)
                return false;

            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != localId)
                    continue;
                shipEntity = entities[i];
                return true;
            }

            return false;
        }

        /// <summary>Equipped component slots for orbit-station and upgrade UI.</summary>
        public static bool TryGetLocalShipLoadout(out ShipLoadoutState loadout)
        {
            loadout = default;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (TryGetLocalShipEntity(em, out var shipEntity) &&
                em.HasComponent<ShipLoadoutState>(shipEntity))
            {
                loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
                return true;
            }

            return false;
        }

        // --- Session / network readiness ---

        /// <summary>
        /// True when NetCode reports gameplay-ready connection (in-game, not just connected to lobby).
        /// </summary>
        public static bool IsNetworkInGame()
        {
            if (ClientWorld != null && ClientWorld.IsCreated &&
                TitanOrbitSessionManager.IsClientGameplayReady(ClientWorld))
                return true;

#if UNITY_SERVER
            // [NETCODE] Headless dedicated server may have ServerWorld only — treat in-game when connection ready.
            if ((ClientWorld == null || !ClientWorld.IsCreated) &&
                ServerWorld != null && ServerWorld.IsCreated &&
                TitanOrbitSessionManager.IsClientConnectionReady(ServerWorld))
                return true;
#endif
            return false;
        }

        /// <summary>
        /// True when this machine runs both client and server worlds in one process (editor host / MPPM host).
        /// </summary>
        public static bool IsLocalHost()
        {
            if (TitanOrbitSessionManager.IsDedicatedOnlineClient)
                return false;

            return ClientWorld != null && ClientWorld.IsCreated && ServerWorld != null && ServerWorld.IsCreated &&
                   TitanOrbitSessionManager.IsClientGameplayReady(ClientWorld) &&
                   TitanOrbitSessionManager.IsClientConnectionReady(ServerWorld);
        }

        /// <summary>NetCode <see cref="NetworkId"/> for this client's connection entity.</summary>
        public static int GetLocalNetworkId()
        {
            return GetLocalNetworkId(ClientWorld);
        }

        /// <summary>
        /// Map generation finished — host reads <see cref="MapStateSingleton"/>; remote clients infer from ghost stream.
        /// Once true for a session, stays true until disconnect so late ghost arrivals do not flash loading UI.
        /// </summary>
        public static bool IsMapLoadingComplete()
        {
            // --- Session edge tracking ---
            // [TITAN-ORBIT] Statics survive "Play twice" when Domain Reload is off. Also, if nothing
            // called this while on the menu, the previous session's latch could still be true when
            // the next NetworkStreamInGame arrives — Join Team with no map.
            bool inGame = IsNetworkInGame();
            if (!inGame)
            {
                if (s_WasNetworkInGame)
                    ResetRemoteMapLoadTracking();
                s_WasNetworkInGame = false;
                ResetRemoteMapLoadTracking();
                InvalidateLocalPlayerShipFrameCache();
                return false;
            }

            // Rising edge of in-game: drop complete-latch / settle-ish load flags for the new join.
            // Do NOT clear MapSessionMetaCache here — GoInGame meta RPC may already have applied.
            if (!s_WasNetworkInGame)
            {
                s_MapLoadingLatchedComplete = false;
                s_JoinLoadSmoothStart = -1f;
                s_ProxyPlateauCount = -1;
                s_ProxyPlateauSince = -1f;
                s_LatchedLoadingTotalSteps = 0;
                s_LatchedActiveTeamCount = 0;
                s_WasNetworkInGame = true;
            }

            // [TITAN-ORBIT] Latch — replicated asteroid/planet counts can tick upward after the first "complete".
            if (s_MapLoadingLatchedComplete)
                return true;

            bool complete = EvaluateMapLoadingComplete();
            if (complete)
                s_MapLoadingLatchedComplete = true;
            return complete;
        }

        /// <summary>
        /// Computes map-ready (no latch). Complete means the local GameObject map build is far
        /// enough along that Join Team will not hitch on a burst of proxy Instantiates.
        /// </summary>
        static bool EvaluateMapLoadingComplete()
        {
            // --- Local host: server must finish generation, then wait for local GO proxies ---
            // [TITAN-ORBIT] Never treat "server LoadingComplete + 0 denominator + 0 proxies" as done.
            // That skipped the loading screen on second Editor Play when meta/totals were not ready
            // yet but the server singleton was already LoadingComplete.
            if (IsLocalHost() &&
                ServerWorld != null && ServerWorld.IsCreated &&
                TryGetMapLoadingComplete(ServerWorld, out var serverComplete) &&
                serverComplete)
                return TryGetMapProxyBuildComplete();

            // --- Remote / dedicated Windows client ---
            // [TITAN-ORBIT] Do NOT treat ClientWorld MapStateSingleton.LoadingComplete alone as done —
            // that dismissed the bar before hybrid GO Instantiates. Wait for proxy / meta gate.
            if (IsRemoteMapObserverClient() && ClientWorld != null && ClientWorld.IsCreated)
                return TryGetReplicatedMapLoadComplete(ClientWorld);

            if (ClientWorld != null && ClientWorld.IsCreated &&
                TryGetMapLoadingComplete(ClientWorld, out var clientComplete) &&
                clientComplete)
                return TryGetMapProxyBuildComplete();

            return false;
        }

        /// <summary>
        /// Loading-bar fill (0–1). Driven by planet/asteroid GameObject proxies vs server meta N
        /// so the bar covers real map-build cost. Soft crawl only until meta arrives (avoids a
        /// stuck 0% before the first RPC). Never gathers asteroid entities.
        /// </summary>
        public static bool TryGetJoinLoadProgress(out float progress)
        {
            progress = 0f;
            if (!IsNetworkInGame())
                return false;

            // --- Done ---
            if (IsMapLoadingComplete())
            {
                progress = 1f;
                return true;
            }

            // --- Real progress: hybrid GO Instantiates / meta total ---
            // [TITAN-ORBIT] MapLoadingProxyCount is a Dictionary count on the visualizer — safe.
            int total = ResolveMapLoadingDenominator();
            int proxies = EcsWorldVisualizer.MapLoadingProxyCount;
            if (total > 0)
            {
                s_LatchedLoadingTotalSteps = total;
                progress = Mathf.Clamp01((float)proxies / total);
                return true;
            }

            // --- Meta not yet: gentle crawl so the bar is not frozen at 0% ---
            if (s_JoinLoadSmoothStart < 0f)
                s_JoinLoadSmoothStart = Time.realtimeSinceStartup;

            float elapsed = Time.realtimeSinceStartup - s_JoinLoadSmoothStart;
            float t = Mathf.Max(0f, elapsed) / JoinLoadSmoothSeconds;
            progress = (1f - Mathf.Exp(-2.2f * t)) * 0.12f;
            return true;
        }

        /// <summary>0–1 loading bar — prefers <see cref="TryGetJoinLoadProgress"/>.</summary>
        public static bool TryGetMapLoadingProgress(out float progress)
        {
            return TryGetJoinLoadProgress(out progress);
        }

        /// <summary>
        /// Planet/asteroid GO count vs server meta total for status text (e.g. 120/678).
        /// Safe — does not scan ECS asteroids.
        /// </summary>
        public static bool TryGetMapLoadingStepCounts(out int completedSteps, out int totalSteps)
        {
            completedSteps = 0;
            totalSteps = 0;
            if (!IsNetworkInGame())
                return false;

            totalSteps = ResolveMapLoadingDenominator();
            if (totalSteps <= 0)
                return false;

            completedSteps = Mathf.Clamp(EcsWorldVisualizer.MapLoadingProxyCount, 0, totalSteps);
            if (IsMapLoadingComplete())
                completedSteps = totalSteps;
            return true;
        }

        /// <summary>
        /// Server meta total for the loading bar denominator. Prefers
        /// <see cref="MapSessionMetaCache.LoadingTotalSteps"/>, else team+neutral+asteroid sum,
        /// else host <see cref="MapStateSingleton.LoadingTotalSteps"/>.
        /// </summary>
        static int ResolveMapLoadingDenominator()
        {
            if (MapSessionMetaCache.LoadingTotalSteps > 0)
            {
                s_LatchedLoadingTotalSteps = MapSessionMetaCache.LoadingTotalSteps;
                return MapSessionMetaCache.LoadingTotalSteps;
            }

            if (MapSessionMetaCache.HasMeta)
            {
                int sum = MapSessionMetaCache.TeamCount +
                          MapSessionMetaCache.NeutralPlanetCount +
                          MapSessionMetaCache.AsteroidCount;
                if (sum > 0)
                {
                    s_LatchedLoadingTotalSteps = sum;
                    return sum;
                }
            }

            // --- Local host: meta RPC may not have latched yet — read ServerWorld singleton ---
            if (IsLocalHost() &&
                ServerWorld != null && ServerWorld.IsCreated &&
                ServerWorld.EntityManager.CreateEntityQuery(typeof(MapStateSingleton))
                    .TryGetSingleton<MapStateSingleton>(out var map) &&
                map.LoadingTotalSteps > 0)
            {
                s_LatchedLoadingTotalSteps = map.LoadingTotalSteps;
                return map.LoadingTotalSteps;
            }

            return s_LatchedLoadingTotalSteps > 0 ? s_LatchedLoadingTotalSteps : 0;
        }

        // --- Map loading helpers (private) ---

        /// <summary>Counts server-side home planets for remote loading denominator refinement.</summary>
        static int CountServerHomePlanets(World server)
        {
            if (server == null || !server.IsCreated)
                return 0;

            using var homes = server.EntityManager.CreateEntityQuery(typeof(HomePlanetTag));
            return homes.CalculateEntityCount();
        }

        /// <summary>
        /// Legacy aggregate loader — prefer <see cref="TryGetJoinLoadProgress"/> /
        /// <see cref="IsMapLoadingComplete"/>. Kept for host singleton diagnostics.
        /// </summary>
        static bool TryGetMapLoadingState(
            out int completedSteps,
            out int totalSteps,
            out bool loadingComplete,
            out float progress)
        {
            completedSteps = 0;
            totalSteps = 0;
            loadingComplete = false;
            progress = 0f;

            if (IsLocalHost() &&
                ServerWorld != null && ServerWorld.IsCreated &&
                TryReadMapLoadingState(ServerWorld, out completedSteps, out totalSteps, out loadingComplete, out progress))
                return true;

            if (ClientWorld != null && ClientWorld.IsCreated &&
                TryReadMapLoadingState(ClientWorld, out completedSteps, out totalSteps, out loadingComplete, out progress))
                return true;

            return false;
        }

        /// <summary>Reads authoritative <see cref="MapStateSingleton"/> progress fields from a world.</summary>
        static bool TryReadMapLoadingState(
            World world,
            out int completedSteps,
            out int totalSteps,
            out bool loadingComplete,
            out float progress)
        {
            completedSteps = 0;
            totalSteps = 0;
            loadingComplete = false;
            progress = 0f;

            if (world == null || !world.IsCreated)
                return false;

            if (!world.EntityManager.CreateEntityQuery(typeof(MapStateSingleton))
                    .TryGetSingleton<MapStateSingleton>(out var map))
                return false;

            completedSteps = map.LoadingCompletedSteps;
            totalSteps = map.LoadingTotalSteps;
            loadingComplete = map.LoadingComplete;
            progress = loadingComplete
                ? 1f
                : totalSteps > 0
                    ? Mathf.Clamp01((float)completedSteps / totalSteps)
                    : Mathf.Clamp01(map.LoadingProgress);
            return totalSteps > 0 || map.LoadingProgress > 0f || loadingComplete;
        }

        /// <summary>Fallback progress when singleton exists but ghosts are the visible numerator.</summary>
        static bool TryReadSpawnedBodyProgress(
            World world,
            out int completedSteps,
            out int totalSteps,
            out bool loadingComplete,
            out float progress)
        {
            completedSteps = 0;
            totalSteps = 0;
            loadingComplete = false;
            progress = 0f;

            var em = world.EntityManager;
            using var planets = em.CreateEntityQuery(typeof(PlanetState));
            using var asteroids = em.CreateEntityQuery(typeof(AsteroidState));
            completedSteps = planets.CalculateEntityCount() + asteroids.CalculateEntityCount();
            if (completedSteps <= 0)
                return false;

            if (em.CreateEntityQuery(typeof(MapStateSingleton)).TryGetSingleton<MapStateSingleton>(out var map) &&
                map.LoadingTotalSteps > 0)
            {
                totalSteps = map.LoadingTotalSteps;
                loadingComplete = map.LoadingComplete;
            }
            else
            {
                return false;
            }

            progress = loadingComplete ? 1f : Mathf.Clamp01((float)completedSteps / totalSteps);
            return true;
        }

        /// <summary>Client-side map progress estimate when singleton is missing or incomplete.</summary>
        static float EstimateClientMapLoadProgress(World client, out int completedSteps, out int totalSteps)
        {
            completedSteps = 0;
            totalSteps = 0;

            var em = client.EntityManager;
            bool hasMapState = em.CreateEntityQuery(typeof(MapStateSingleton))
                .TryGetSingleton<MapStateSingleton>(out var map);

            if (hasMapState)
            {
                completedSteps = map.LoadingCompletedSteps;
                totalSteps = map.LoadingTotalSteps;
                if (map.LoadingComplete)
                    return 1f;
                if (totalSteps > 0)
                    return Mathf.Clamp01((float)completedSteps / totalSteps);
                if (map.LoadingProgress > 0f)
                    return Mathf.Clamp01(map.LoadingProgress);
            }

            using var planets = em.CreateEntityQuery(typeof(PlanetState));
            using var asteroids = em.CreateEntityQuery(typeof(AsteroidState));
            completedSteps = planets.CalculateEntityCount() + asteroids.CalculateEntityCount();

            if (hasMapState && map.LoadingTotalSteps > 0)
            {
                totalSteps = map.LoadingTotalSteps;
                return Mathf.Clamp01((float)math.min(completedSteps, totalSteps) / totalSteps);
            }

            if (em.CreateEntityQuery(typeof(MapLayoutEntryElement)).TryGetSingletonBuffer<MapLayoutEntryElement>(out var layout) &&
                layout.Length > 0)
            {
                totalSteps = layout.Length;
                return Mathf.Clamp01((float)completedSteps / totalSteps);
            }

            int homeCount = CountReplicatedHomePlanets(em);
            totalSteps = ResolveRemoteMapExpectedTotal(homeCount);
            if (completedSteps <= 0)
                return 0f;

            return Mathf.Clamp01((float)completedSteps / totalSteps);
        }

        // --- Local ship entity resolution (private) ---

        /// <summary>Finds ship transform via NetCode <see cref="CommandTarget"/> on in-game connections.</summary>
        static bool TryGetShipFromCommandTarget(EntityManager em, out LocalTransform transform)
        {
            transform = default;
            using var connections = em.CreateEntityQuery(
                typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(CommandTarget));
            using var targets = connections.ToComponentDataArray<CommandTarget>(Allocator.Temp);
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i].targetEntity;
                if (target == Entity.Null || !em.Exists(target))
                    continue;
                if (!em.HasComponent<ShipTag>(target) || !em.HasComponent<LocalTransform>(target))
                    continue;
                transform = em.GetComponentData<LocalTransform>(target);
                return true;
            }

            return false;
        }

        /// <summary>First ship with <see cref="GhostOwnerIsLocal"/> enableable flag set.</summary>
        static bool TryGetLocalOwnedShipEntity(EntityManager em, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            // [TITAN-ORBIT] Callers may omit the gate — never gather ships during Instantiates /
            // TeamChoice pre-seed (see ShouldSkipShipEntityQueries).
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            using var query = em.CreateEntityQuery(typeof(GhostOwnerIsLocal), typeof(ShipTag));
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (!em.IsComponentEnabled<GhostOwnerIsLocal>(entities[i]))
                    continue;
                shipEntity = entities[i];
                return true;
            }

            return false;
        }

        /// <summary>
        /// Central local-ship lookup: NetworkId match, GhostOwnerIsLocal, LocalPlayerShipTag, then CommandTarget.
        /// Returns false while team/rejoin flow suppresses control so presentation cannot latch onto an
        /// orphan GhostOwner ship during map load.
        /// </summary>
        static bool TryGetLocalShipEntity(EntityManager em, out Entity shipEntity)
        {
            shipEntity = Entity.Null;

            // [TITAN-ORBIT] Same gate as TryGetLocalShipTransformFromWorld — ShipVisualSyncSystem and
            // camera follow this path; without it, a rejoin orphan drove presentation before Join Team.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return false;

            // [TITAN-ORBIT] Ship ToEntityArray during post–Join Team Instantiates Crash!!!
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            int localId = GetLocalNetworkId(ClientWorld);
            if (localId > 0)
            {
                using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
                using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                using var entities = query.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < owners.Length; i++)
                {
                    if (owners[i].NetworkId != localId)
                        continue;
                    shipEntity = entities[i];
                    return true;
                }
            }

            if (TryGetLocalOwnedShipEntity(em, out shipEntity))
                return true;

            using var tagged = em.CreateEntityQuery(typeof(LocalPlayerShipTag), typeof(ShipTag));
            if (tagged.CalculateEntityCount() > 0)
            {
                using var entities = tagged.ToEntityArray(Allocator.Temp);
                if (entities.Length > 0)
                {
                    shipEntity = entities[0];
                    return true;
                }
            }

            using var connections = em.CreateEntityQuery(
                typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(CommandTarget));
            using var targets = connections.ToComponentDataArray<CommandTarget>(Allocator.Temp);
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i].targetEntity;
                if (target == Entity.Null || !em.Exists(target) || !em.HasComponent<ShipTag>(target))
                    continue;
                shipEntity = target;
                return true;
            }

            return false;
        }

        /// <summary>Reads <see cref="ShipState"/> from the ship pointed at by <see cref="CommandTarget"/>.</summary>
        static bool TryGetShipStateFromCommandTarget(EntityManager em, out ShipState state)
        {
            state = default;
            using var connections = em.CreateEntityQuery(
                typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(CommandTarget));
            using var targets = connections.ToComponentDataArray<CommandTarget>(Allocator.Temp);
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i].targetEntity;
                if (target == Entity.Null || !em.Exists(target))
                    continue;
                if (!em.HasComponent<ShipTag>(target) || !em.HasComponent<ShipState>(target))
                    continue;
                state = em.GetComponentData<ShipState>(target);
                return true;
            }

            return false;
        }

        /// <summary>First ship matching marker component (tag or GhostOwnerIsLocal).</summary>
        static bool TryGetShipTransform(EntityManager em, ComponentType marker, out LocalTransform transform)
        {
            transform = default;
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;
            using var query = em.CreateEntityQuery(marker, typeof(ShipTag), typeof(LocalTransform));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (marker == ComponentType.ReadOnly<GhostOwnerIsLocal>() &&
                    !em.IsComponentEnabled<GhostOwnerIsLocal>(entities[i]))
                    continue;

                transform = transforms[i];
                return true;
            }

            return false;
        }

        /// <summary>Ship pose lookup by replicated <see cref="GhostOwner.NetworkId"/>.</summary>
        static bool TryGetShipTransformByNetworkId(EntityManager em, int networkId, out LocalTransform transform)
        {
            transform = default;
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner), typeof(LocalTransform));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                transform = transforms[i];
                return true;
            }

            return false;
        }

        /// <summary><see cref="ShipState"/> lookup by <see cref="GhostOwner.NetworkId"/>.</summary>
        static bool TryGetShipStateByNetworkId(EntityManager em, int networkId, out ShipState state)
        {
            state = default;
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner), typeof(ShipState));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                state = states[i];
                return true;
            }

            return false;
        }

        /// <summary><see cref="ShipOrbitState"/> lookup by <see cref="GhostOwner.NetworkId"/>.</summary>
        static bool TryGetShipOrbitStateByNetworkId(EntityManager em, int networkId, out ShipOrbitState orbitState)
        {
            orbitState = default;
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner), typeof(ShipOrbitState));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipOrbitState>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                orbitState = states[i];
                return true;
            }

            return false;
        }

        /// <summary>
        /// Bundles ship components needed for people-load eligibility in one ships-only scan.
        /// </summary>
        static bool TryGetShipPeopleLoadContext(
            EntityManager em,
            int networkId,
            out ShipState shipState,
            out ShipInput shipInput,
            out ShipOrbitState shipOrbit,
            out ShipMoonDockState moonDock,
            out LocalTransform shipTransform)
        {
            shipState = default;
            shipInput = default;
            shipOrbit = default;
            moonDock = default;
            shipTransform = default;

            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            using var query = em.CreateEntityQuery(
                typeof(ShipTag),
                typeof(GhostOwner),
                typeof(ShipState),
                typeof(ShipInput),
                typeof(ShipOrbitState),
                typeof(ShipMoonDockState),
                typeof(LocalTransform));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var inputs = query.ToComponentDataArray<ShipInput>(Allocator.Temp);
            using var orbits = query.ToComponentDataArray<ShipOrbitState>(Allocator.Temp);
            using var docks = query.ToComponentDataArray<ShipMoonDockState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;

                shipState = states[i];
                shipInput = inputs[i];
                shipOrbit = orbits[i];
                moonDock = docks[i];
                shipTransform = transforms[i];
                return true;
            }

            return false;
        }

        /// <summary>First in-game connection's <see cref="NetworkId"/> on the client world.</summary>
        static int GetLocalNetworkId(World clientWorld)
        {
            if (clientWorld == null || !clientWorld.IsCreated)
                return -1;

            var em = clientWorld.EntityManager;
            using var ids = em.CreateEntityQuery(
                    typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(NetworkId))
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            return ids.Length > 0 ? ids[0].Value : -1;
        }

        /// <summary>Whether any connection entity has <see cref="NetworkStreamInGame"/>.</summary>
        static bool HasNetworkStreamInGame(World world)
        {
            if (world == null || !world.IsCreated) return false;
            return world.EntityManager.CreateEntityQuery(typeof(NetworkStreamInGame)).CalculateEntityCount() > 0;
        }

        /// <summary>Reads <see cref="MapStateSingleton.LoadingComplete"/> from a world.</summary>
        static bool TryGetMapLoadingComplete(World world, out bool loadingComplete)
        {
            loadingComplete = false;
            if (world == null || !world.IsCreated) return false;
            if (!world.EntityManager.CreateEntityQuery(typeof(MapStateSingleton)).TryGetSingleton<MapStateSingleton>(out var map))
                return false;
            loadingComplete = map.LoadingComplete;
            return true;
        }

        /// <summary>Remote LAN/MPPM/dedicated clients have no ServerWorld map singleton.</summary>
        static bool IsRemoteMapObserverClient()
        {
            if (IsLocalHost())
                return false;

            if (TitanOrbitSessionManager.IsDedicatedOnlineClient)
                return true;

            if (TitanOrbit.NetCode.TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                return true;

            // LAN client in main editor may still have a suspended ServerWorld from menu bootstrap.
            return ClientWorld != null && ClientWorld.IsCreated && IsNetworkInGame();
        }

        const float RemoteMapStableSeconds = 0.5f;
        const int RemoteMapMinAsteroids = 32;

        /// <summary>Cached expected spawn total for remote loading bar denominator.</summary>
        static int s_RemoteMapExpectedTotal = -1;
        /// <summary>Last observed replicated planet count — stability detection.</summary>
        static int s_RemoteMapPlanetCount = -1;
        /// <summary>Last observed replicated asteroid count — stability detection.</summary>
        static int s_RemoteMapAsteroidCount = -1;
        /// <summary>realtimeSinceStartup when body counts last changed — settle window before "complete".</summary>
        static float s_RemoteMapStableSince = -1f;
        /// <summary>Stays true after first successful <see cref="IsMapLoadingComplete"/> until session reset.</summary>
        static bool s_MapLoadingLatchedComplete;

        /// <summary>
        /// Previous <see cref="IsNetworkInGame"/> sample — detects leave/enter so latches cannot
        /// survive a second Play or a reconnect without Domain Reload.
        /// </summary>
        static bool s_WasNetworkInGame;

        /// <summary>Last known team count for lobby UI — avoids flicker when home ghosts briefly desync.</summary>
        static int s_LatchedActiveTeamCount;
        /// <summary>
        /// Loading-screen meta latch (team / diagnostics). Not used as a body-count bar numerator.
        /// </summary>
        static int s_LatchedLoadingTotalSteps;

        /// <summary>
        /// realtimeSinceStartup when the soft pre-meta crawl started for this in-game session.
        /// </summary>
        static float s_JoinLoadSmoothStart = -1f;

        /// <summary>Last proxy count while watching for a starve plateau (Pending bake missing).</summary>
        static int s_ProxyPlateauCount = -1;

        /// <summary>When proxy count last changed during the starve watch.</summary>
        static float s_ProxyPlateauSince = -1f;

        /// <summary>
        /// Seconds for the soft crawl before meta arrives (caps at ~12% so real proxy progress owns the bar).
        /// </summary>
        const float JoinLoadSmoothSeconds = 12f;

        /// <summary>Fraction of meta N that must exist as GameObject proxies before Join Team.</summary>
        const float MapProxyReadyRatio = 0.92f;

        /// <summary>
        /// If Instantiates finished but proxies never reach ready ratio (no Pending bake), dismiss after this.
        /// </summary>
        const float MapProxyStarveEscapeSeconds = 12f;

        /// <summary>
        /// Clears remote map heuristics when disconnecting, leaving in-game, or entering Play Mode.
        /// Also clears join-settle managed cache so <c>JoinSettleCompleted</c> cannot stick across Plays.
        /// </summary>
        static void ResetRemoteMapLoadTracking()
        {
            s_RemoteMapExpectedTotal = -1;
            s_RemoteMapPlanetCount = -1;
            s_RemoteMapAsteroidCount = -1;
            s_RemoteMapStableSince = -1f;
            s_MapLoadingLatchedComplete = false;
            s_LatchedActiveTeamCount = 0;
            s_LatchedLoadingTotalSteps = 0;
            s_JoinLoadSmoothStart = -1f;
            s_ProxyPlateauCount = -1;
            s_ProxyPlateauSince = -1f;
            // [TITAN-ORBIT] Drop latched MapSessionMetaRpc so the next join does not reuse old totals.
            MapSessionMetaCache.Clear();
            // JoinSettleCompleted / TransformQuarantine are static — clear on session end so a second
            // Editor Play does not think Instantiates already finished.
            ClientJoinSettleCache.Clear();
            GemClientEntityRegistry.Clear();
            PlanetClientEntityRegistry.Clear();
            PlanetConnectionGraphCache.Clear();
            PlanetConnectionPresentationTriangles.Clear();
            s_PlanetStateByIdCache.Clear();
            s_PlanetStateCacheFrame = -1;
            GemTractorBeamVisibilityTracker.Clear();
        }

        /// <summary>
        /// Remote clients never learn the true spawn queue length; keep a fixed denominator for the loading bar.
        /// Refines once replicated home planets reveal team count.
        /// </summary>
        static int ResolveRemoteMapExpectedTotal(int homeCount)
        {
            if (homeCount > 0)
            {
                s_RemoteMapExpectedTotal = EstimateExpectedRemoteMapBodies(homeCount);
                return s_RemoteMapExpectedTotal;
            }

            return s_RemoteMapExpectedTotal > 0 ? s_RemoteMapExpectedTotal : EstimateMapSpawnStepsFromSettings(0);
        }

        /// <summary>Reads neutral + asteroid midpoint from <see cref="MapGenerationSettingsCache"/>.</summary>
        static int EstimateMapSpawnStepsFromSettings(int homeCount)
        {
            if (MapGenerationSettingsCache.Settings != null)
            {
                var s = MapGenerationSettingsCache.Settings;
                int neutrals = (s.minNeutralPlanets + s.maxNeutralPlanets + 1) / 2;
                int asteroids = (s.asteroidsAtMinMapSize + s.asteroidsAtMaxMapSize + 1) / 2;
                if (homeCount > 0)
                    return homeCount + neutrals + asteroids;

                int teams = (s.minTeamsPerMatch + s.maxTeamsPerMatch + 1) / 2;
                return teams + neutrals + asteroids;
            }

            return homeCount > 0 ? homeCount + 12 + 666 : 678;
        }

        /// <summary>Counts replicated home/planet/asteroid ghosts on the client world.</summary>
        static bool TryGetReplicatedMapBodyCounts(World client, out int homeCount, out int planetCount, out int asteroidCount)
        {
            homeCount = 0;
            planetCount = 0;
            asteroidCount = 0;
            if (client == null || !client.IsCreated)
                return false;

            var em = client.EntityManager;
            homeCount = CountReplicatedHomePlanets(em);
            // --- Instantiated ghosts only ---
            // [NETCODE] Exclude PendingSpawnPlaceholder so the loading bar tracks real Instantiates
            // (1/frame), not CreateEntity placeholders that have no hull / visuals yet.
            using var planets = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.Exclude<PendingSpawnPlaceholder>());
            using var asteroids = em.CreateEntityQuery(
                ComponentType.ReadOnly<AsteroidState>(),
                ComponentType.Exclude<PendingSpawnPlaceholder>());
            planetCount = planets.CalculateEntityCount();
            asteroidCount = asteroids.CalculateEntityCount();
            return homeCount > 0 || planetCount > 0 || asteroidCount > 0;
        }

        /// <summary>Expected total bodies from layout buffer or map settings given home planet count.</summary>
        static int EstimateExpectedRemoteMapBodies(int homeCount)
        {
            if (homeCount <= 0)
                return 0;

            if (ClientWorld != null && ClientWorld.IsCreated &&
                ClientWorld.EntityManager.CreateEntityQuery(typeof(MapLayoutEntryElement))
                    .TryGetSingletonBuffer<MapLayoutEntryElement>(out var layout) &&
                layout.Length > 0)
                return layout.Length;

            return EstimateMapSpawnStepsFromSettings(homeCount);
        }

        /// <summary>
        /// [LEGACY] Ghost body-count progress — not used by the loading UI.
        /// Prefer <see cref="TryGetJoinLoadProgress"/>. Do not call from join UI on Windows
        /// (asteroid <c>CalculateEntityCount</c> gathers are crash-adjacent).
        /// </summary>
        static bool TryReadReplicatedMapLoadProgress(
            World client,
            out int completedSteps,
            out int totalSteps,
            out bool loadingComplete,
            out float progress)
        {
            completedSteps = 0;
            totalSteps = 0;
            loadingComplete = false;
            progress = 0f;

            if (!TryGetReplicatedMapBodyCounts(client, out int homes, out int planets, out int asteroids))
                return false;

            completedSteps = planets + asteroids;
            totalSteps = ResolveRemoteMapExpectedTotal(homes);

            progress = Mathf.Clamp01((float)completedSteps / totalSteps);
            loadingComplete = TryGetReplicatedMapLoadComplete(client);
            return true;
        }

        /// <summary>
        /// Remote clients: dismiss loading when Instantiates settle is done <b>and</b> enough
        /// planet/asteroid GameObject proxies exist (meta N). The loading screen exists to absorb
        /// that GO Instantiates cost — do not open Join Team while the visualizer is still building.
        /// </summary>
        static bool TryGetReplicatedMapLoadComplete(World _)
        {
            // --- Still Instantiating ECS ghosts ---
            // [TITAN-ORBIT] Cover ClientJoinSettle so Join Team does not open mid Instantiates flood.
            if (ClientJoinSettleCache.Settling)
                return false;

            // Prefer the latch set after the first Settling exit (blocks post-team re-settle quirks).
            if (!ClientJoinSettleCache.JoinSettleCompleted &&
                ClientJoinSettleCache.InGameFrames < TitanOrbitClientJoinTransformGateSystem.MinInGameFramesBeforeExit)
                return false;

            // --- GO map build (safe Dictionary count — no asteroid ToEntityArray) ---
            return TryGetMapProxyBuildComplete();
        }

        /// <summary>
        /// True when hybrid planet/asteroid proxies are near the server meta total.
        /// Uses <see cref="EcsWorldVisualizer.MapLoadingProxyCount"/> only — never scans ECS bodies.
        /// </summary>
        static bool TryGetMapProxyBuildComplete()
        {
            // --- Do not finish while GhostSpawn Instantiates are still draining ---
            if (ClientJoinSettleCache.Settling)
                return false;

            int total = ResolveMapLoadingDenominator();
            if (total <= 0)
                return false;

            int proxies = EcsWorldVisualizer.MapLoadingProxyCount;
            int readyAt = Mathf.Max(1, Mathf.CeilToInt(total * MapProxyReadyRatio));
            if (proxies >= readyAt)
            {
                s_ProxyPlateauSince = -1f;
                return true;
            }

            // --- Do NOT starve-escape to Join Team at 0 proxies ---
            // Player.log 2026-07-19: plateau 0/768 → dismiss → TeamChoice → Crash!!!.
            // MapBodyHybridVisualInstantiateHook queues SpawnRequest per Instantiates so proxies
            // should climb. If they stay at 0, keep the loading screen (safer than crashing).
            if (ClientJoinSettleCache.JoinSettleCompleted &&
                TitanOrbitJoinLoadCounters.InstantiatesSession >= readyAt &&
                proxies == 0 &&
                s_ProxyPlateauSince < 0f)
            {
                s_ProxyPlateauSince = Time.realtimeSinceStartup;
                Debug.LogWarning(
                    "[MapLoad] Proxy count still 0/" + total +
                    " after Instantiates settle — keeping loading screen. " +
                    "Check TO_GhostSpawn_v13 hook + MapBodyHybridVisualSpawnRequest drain.");
            }

            return false;
        }

        /// <summary>Length of ghost-replicated map layout buffer on the client (0 until finalize).</summary>
        static bool TryGetReplicatedLayoutEntryCount(World client, out int count)
        {
            count = 0;
            if (client == null || !client.IsCreated)
                return false;

            if (!client.EntityManager.CreateEntityQuery(typeof(MapLayoutEntryElement))
                    .TryGetSingletonBuffer<MapLayoutEntryElement>(out var layout))
                return false;

            count = layout.Length;
            return count > 0;
        }

        /// <summary>True when replicated planet/ship ghosts indicate the client has enough world state for lobby UI.</summary>
        public static bool HasClientReplicatedMapContent()
        {
            return ClientWorld != null && ClientWorld.IsCreated && HasReplicatedMapWorldContent(ClientWorld);
        }

        /// <summary>True when enough planet/asteroid ghosts have streamed and counts stabilized.</summary>
        static bool HasReplicatedMapWorldContent(World client)
        {
            var em = client.EntityManager;
            using var planets = em.CreateEntityQuery(typeof(PlanetState));
            if (planets.CalculateEntityCount() >= 3)
                return true;

            // Host picked a team — at least one ship ghost means the match is live.
            using var ships = em.CreateEntityQuery(typeof(ShipTag));
            return ships.CalculateEntityCount() > 0;
        }

        // --- Team / match queries ---

        /// <summary>Team roster singleton — prefers ServerWorld on host, else ClientWorld.</summary>
        public static TeamStateSingleton GetTeamState()
        {
            if (ServerWorld != null && ServerWorld.IsCreated)
            {
                var serverQuery = ServerWorld.EntityManager.CreateEntityQuery(typeof(TeamStateSingleton));
                if (serverQuery.TryGetSingleton<TeamStateSingleton>(out var serverTeam))
                    return serverTeam;
            }

            if (ClientWorld != null && ClientWorld.IsCreated)
            {
                var clientQuery = ClientWorld.EntityManager.CreateEntityQuery(typeof(TeamStateSingleton));
                if (clientQuery.TryGetSingleton<TeamStateSingleton>(out var clientTeam))
                    return clientTeam;
            }

            return default;
        }

        /// <summary>Number of teams in this match (from home planets, then server team state).</summary>
        public static bool TryGetActiveTeamCount(out int activeTeamCount)
        {
            activeTeamCount = 0;

            // [TITAN-ORBIT] Latch team count once discovered so Join Team UI does not bounce to "Preparing teams...".
            if (s_LatchedActiveTeamCount > 0)
            {
                activeTeamCount = s_LatchedActiveTeamCount;
                return true;
            }

            // --- MapSessionMetaRpc (dedicated clients — no ServerWorld, no gather) ---
            // [TITAN-ORBIT] Prefer this before home-planet queries (those ToComponentDataArray paths
            // are unsafe under TransformQuarantine and often return 0 while meta already has TeamCount).
            if (MapSessionMetaCache.HasMeta && MapSessionMetaCache.TeamCount > 0)
            {
                activeTeamCount = MapSessionMetaCache.TeamCount;
                return LatchActiveTeamCount(activeTeamCount);
            }

            if (ServerWorld != null && ServerWorld.IsCreated)
            {
                using var homes = ServerWorld.EntityManager.CreateEntityQuery(typeof(HomePlanetTag));
                int homeCount = homes.CalculateEntityCount();
                if (homeCount > 0)
                {
                    activeTeamCount = homeCount;
                    return LatchActiveTeamCount(activeTeamCount);
                }

                if (ServerWorld.EntityManager.CreateEntityQuery(typeof(TeamStateSingleton))
                        .TryGetSingleton<TeamStateSingleton>(out var serverTeam) &&
                    serverTeam.ActiveTeamCount > 0)
                {
                    activeTeamCount = serverTeam.ActiveTeamCount;
                    return LatchActiveTeamCount(activeTeamCount);
                }
            }

            var world = GetLocalPlayerShipWorld();
            if (world != null && world.IsCreated)
            {
                int replicatedHomeCount = CountReplicatedHomePlanets(world.EntityManager);
                if (replicatedHomeCount > 0)
                {
                    activeTeamCount = replicatedHomeCount;
                    return LatchActiveTeamCount(activeTeamCount);
                }
            }

            if (!IsMapLoadingComplete())
                return false;

            var teamState = GetTeamState();
            if (teamState.ActiveTeamCount > 0)
            {
                activeTeamCount = teamState.ActiveTeamCount;
                return LatchActiveTeamCount(activeTeamCount);
            }

            return false;
        }

        /// <summary>Stores the first non-zero team count for the current in-game session.</summary>
        static bool LatchActiveTeamCount(int count)
        {
            if (count > 0)
                s_LatchedActiveTeamCount = count;
            return count > 0;
        }

        /// <summary>Counts home planets with <see cref="PlanetState.IsHomePlanet"/> in replicated state.</summary>
        static int CountReplicatedHomePlanets(EntityManager em)
        {
            // [TITAN-ORBIT] Prefer meta / quarantine-safe proxy walk — full planet gather Crash!!! on Windows.
            if (MapSessionMetaCache.HasMeta && MapSessionMetaCache.TeamCount > 0)
                return MapSessionMetaCache.TeamCount;

            if (ClientJoinSettleCache.TransformQuarantine ||
                ClientJoinSettleCache.Settling ||
                ClientJoinSettleCache.GhostSpawnBacklog)
            {
                int count = 0;
                foreach (var entity in GetScratchProxyEntities())
                {
                    if (!em.Exists(entity) || !em.HasComponent<PlanetState>(entity))
                        continue;
                    if (em.GetComponentData<PlanetState>(entity).IsHomePlanet)
                        count++;
                }

                return count;
            }

            using var query = em.CreateEntityQuery(typeof(PlanetState), typeof(PlanetTag));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            int fullCount = 0;
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].IsHomePlanet)
                    fullCount++;
            }

            return fullCount;
        }

        // --- Planet queries ---

        /// <summary>
        /// Finds the team's home planet <see cref="PlanetState.PlanetId"/> for orbit-store Bank UI.
        /// Uses replicated <see cref="PlanetState.IsHomePlanet"/> — not server-only <see cref="HomePlanetTag"/>
        /// (that tag never appears on client ghosts, so Bank RPCs were stuck with home id 0).
        /// </summary>
        /// <param name="team">Local ship's team.</param>
        /// <param name="planetId">Stable planet id of that team's home, or 0 on failure.</param>
        /// <returns>True when a matching home planet was found.</returns>
        public static bool TryGetHomePlanetIdForTeam(TeamId team, out int planetId)
        {
            // --- Resolve home planet id for orbit Bank / store RPCs ---
            planetId = 0;
            if (team == TeamId.None)
                return false;

            // Local host: authoritative server world has HomePlanetTag + full PlanetState.
            if (IsLocalHost() &&
                TryFindHomePlanetIdInWorld(ServerWorld, team, preferHomePlanetTag: true, out planetId))
                return true;

            // Client / visualization: HomePlanetTag is not replicated — filter on IsHomePlanet.
            if (TryFindHomePlanetIdInWorld(GetVisualizationWorld(), team, preferHomePlanetTag: false, out planetId))
                return true;

            if (TryFindHomePlanetIdInWorld(ClientWorld, team, preferHomePlanetTag: false, out planetId))
                return true;

            return false;
        }

        /// <summary>
        /// Scans one world for the team's home planet id. Under TransformQuarantine / Settling /
        /// GhostSpawnBacklog walks hybrid proxies only — never a full planet <c>ToComponentDataArray</c>.
        /// </summary>
        static bool TryFindHomePlanetIdInWorld(
            World world,
            TeamId team,
            bool preferHomePlanetTag,
            out int planetId)
        {
            planetId = 0;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;

            // [TITAN-ORBIT] Windows late-join: Settling OFF is NOT safe for map gathers.
            // TransformQuarantine stays true for the whole in-game session.
            bool useProxyWalk =
                ClientJoinSettleCache.TransformQuarantine ||
                ClientJoinSettleCache.Settling ||
                ClientJoinSettleCache.GhostSpawnBacklog;

            if (useProxyWalk)
            {
                foreach (var entity in GetScratchProxyEntities())
                {
                    if (!em.Exists(entity) || !em.HasComponent<PlanetState>(entity))
                        continue;

                    var state = em.GetComponentData<PlanetState>(entity);
                    if (!state.IsHomePlanet || state.Ownership != team || state.PlanetId <= 0)
                        continue;

                    planetId = state.PlanetId;
                    return true;
                }

                return false;
            }

            // Server / non-quarantined path: HomePlanetTag is a tiny set (one per team).
            if (preferHomePlanetTag)
            {
                using var homeQuery = em.CreateEntityQuery(typeof(HomePlanetTag), typeof(PlanetState));
                if (!homeQuery.IsEmptyIgnoreFilter)
                {
                    using var homeStates = homeQuery.ToComponentDataArray<PlanetState>(Allocator.Temp);
                    for (int i = 0; i < homeStates.Length; i++)
                    {
                        if (homeStates[i].Ownership != team || homeStates[i].PlanetId <= 0)
                            continue;
                        planetId = homeStates[i].PlanetId;
                        return true;
                    }
                }
            }

            // Client ghosts: IsHomePlanet is the replicated flag (HomePlanetTag is server-only).
            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (!states[i].IsHomePlanet || states[i].Ownership != team || states[i].PlanetId <= 0)
                    continue;
                planetId = states[i].PlanetId;
                return true;
            }

            return false;
        }

        /// <summary><see cref="PlanetState"/> by stable <see cref="PlanetState.PlanetId"/> across host/client worlds.</summary>
        public static bool TryGetPlanetStateByPlanetId(int planetId, out PlanetState state)
        {
            state = default;
            if (planetId == 0)
                return false;

            // --- One planet map per frame for all labels/UI ---
            // [TITAN-ORBIT] Avoid N× CreateEntityQuery (one per PlanetWorldStatsLabel LateUpdate).
            EnsurePlanetStateCacheForFrame();
            if (s_PlanetStateByIdCache.TryGetValue(planetId, out state))
                return true;

            // Fallback if cache empty mid-join (registry not ready yet).
            if (IsLocalHost() && TryFindPlanetState(ServerWorld, planetId, out state))
                return true;

            return TryFindPlanetState(ClientWorld, planetId, out state);
        }

        /// <summary>Gem-moon combat state for a planet — shield, orbit zone, contributed gems UI.</summary>
        public static bool TryGetPlanetGemMoonStateByPlanetId(int planetId, out PlanetGemMoonState moonState)
        {
            moonState = default;
            if (planetId == 0)
                return false;

            if (IsLocalHost() && TryFindPlanetGemMoonState(ServerWorld, planetId, out moonState))
                return true;

            if (TryFindPlanetGemMoonState(ClientWorld, planetId, out moonState))
                return true;

            return false;
        }

        /// <summary>Reads contributed gem bank balance from the server ledger (local host only).</summary>
        public static bool TryGetContributedGems(int homePlanetId, out float amount)
        {
            amount = 0f;
            if (homePlanetId <= 0 || !IsLocalHost())
                return false;

            var server = ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            int networkId = GetLocalNetworkId();
            if (networkId <= 0)
                return false;

            var em = server.EntityManager;
            using var query = em.CreateEntityQuery(typeof(HomePlanetTag), typeof(PlanetState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != homePlanetId)
                    continue;

                amount = ContributedGemsLogic.Get(em, entities[i], networkId);
                return true;
            }

            return false;
        }

        /// <summary>One PlanetState snapshot per planet id — rebuilt once per frame for labels/UI.</summary>
        static readonly Dictionary<int, PlanetState> s_PlanetStateByIdCache = new Dictionary<int, PlanetState>(32);

        /// <summary>Frame stamp for <see cref="s_PlanetStateByIdCache"/> (avoids N× CreateEntityQuery).</summary>
        static int s_PlanetStateCacheFrame = -1;

        /// <summary>Scratch for <see cref="TryFindPlanetStateFromClientRegistry"/> — no per-call List alloc.</summary>
        static readonly List<Entity> s_PlanetRegistryScratch = new List<Entity>(32);

        /// <summary>
        /// Rebuilds the per-frame planet-id → PlanetState map once, then serves lookups.
        /// Labels call this every LateUpdate — without the cache, Local Host ran a full planet
        /// CreateEntityQuery per planet per frame (major Scripts hitch).
        /// </summary>
        static void EnsurePlanetStateCacheForFrame()
        {
            if (s_PlanetStateCacheFrame == Time.frameCount)
                return;

            s_PlanetStateCacheFrame = Time.frameCount;
            s_PlanetStateByIdCache.Clear();

            // Prefer authoritative server on Local Host; else client visualization world.
            if (IsLocalHost() && ServerWorld != null && ServerWorld.IsCreated)
                FillPlanetStateCacheFromWorld(ServerWorld, allowFullQuery: true);
            else if (ClientWorld != null && ClientWorld.IsCreated)
                FillPlanetStateCacheFromWorld(ClientWorld, allowFullQuery: false);
        }

        /// <summary>Fills <see cref="s_PlanetStateByIdCache"/> from one world (server full query or client-safe).</summary>
        static void FillPlanetStateCacheFromWorld(World world, bool allowFullQuery)
        {
            var em = world.EntityManager;

            if (allowFullQuery ||
                (!ClientJoinSettleCache.Settling &&
                 !ClientJoinSettleCache.TransformQuarantine &&
                 !ClientJoinSettleCache.GhostSpawnBacklog))
            {
                using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState));
                using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
                for (int i = 0; i < states.Length; i++)
                {
                    var s = states[i];
                    if (s.PlanetId != 0)
                        s_PlanetStateByIdCache[s.PlanetId] = s;
                }

                return;
            }

            // Client join Instantiates / quarantine — Instantiates planet registry only (no archetype gather).
            if (ClientJoinSettleCache.Settling)
                return;

            PlanetClientEntityRegistry.CopyLive(s_PlanetRegistryScratch);
            for (int i = 0; i < s_PlanetRegistryScratch.Count; i++)
            {
                Entity entity = s_PlanetRegistryScratch[i];
                if (entity == Entity.Null ||
                    !em.Exists(entity) ||
                    !em.HasComponent<PlanetState>(entity))
                    continue;
                var s = em.GetComponentData<PlanetState>(entity);
                if (s.PlanetId != 0)
                    s_PlanetStateByIdCache[s.PlanetId] = s;
            }
        }

        /// <summary>Linear search for <see cref="PlanetState"/> by planet id in a world.</summary>
        static bool TryFindPlanetState(World world, int planetId, out PlanetState state)
        {
            state = default;
            if (world == null || !world.IsCreated || planetId == 0)
                return false;

            // Frame cache is filled from the preferred world; still accept caller world as fallback.
            EnsurePlanetStateCacheForFrame();
            if (s_PlanetStateByIdCache.TryGetValue(planetId, out state))
                return true;

            // Rare: cache empty / different world — quarantine-safe single lookup.
            if (world.IsClient())
            {
                if (ClientJoinSettleCache.Settling)
                    return false;

                var em = world.EntityManager;
                if (EcsWorldVisualizer.Active != null &&
                    EcsWorldVisualizer.Active.TryGetPlanetPoseByPlanetId(em, planetId, out _, out _, out state))
                    return true;

                return TryFindPlanetStateFromClientRegistry(em, planetId, out state);
            }

            return false;
        }

        /// <summary>
        /// Quarantine-safe PlanetState lookup via <see cref="PlanetClientEntityRegistry"/>.
        /// </summary>
        static bool TryFindPlanetStateFromClientRegistry(EntityManager em, int planetId, out PlanetState state)
        {
            state = default;
            PlanetClientEntityRegistry.CopyLive(s_PlanetRegistryScratch);
            for (int i = 0; i < s_PlanetRegistryScratch.Count; i++)
            {
                Entity entity = s_PlanetRegistryScratch[i];
                if (entity == Entity.Null ||
                    !em.Exists(entity) ||
                    !em.HasComponent<PlanetState>(entity))
                    continue;
                var s = em.GetComponentData<PlanetState>(entity);
                if (s.PlanetId != planetId)
                    continue;
                state = s;
                return true;
            }

            return false;
        }

        /// <summary>Linear search for <see cref="PlanetGemMoonState"/> by parent planet id.</summary>
        static bool TryFindPlanetGemMoonState(World world, int planetId, out PlanetGemMoonState moonState)
        {
            moonState = default;
            if (world == null || !world.IsCreated)
                return false;

            if (ClientJoinSettleCache.Settling ||
                ClientJoinSettleCache.GhostSpawnBacklog ||
                ClientJoinSettleCache.TransformQuarantine)
            {
                // Quarantine: walk hybrid proxies only (no planet archetype gather).
                var em = world.EntityManager;
                foreach (var entity in GetScratchProxyEntities())
                {
                    if (!em.Exists(entity) ||
                        !em.HasComponent<PlanetState>(entity) ||
                        !em.HasComponent<PlanetGemMoonState>(entity))
                        continue;
                    if (em.GetComponentData<PlanetState>(entity).PlanetId != planetId)
                        continue;
                    moonState = em.GetComponentData<PlanetGemMoonState>(entity);
                    return true;
                }

                return false;
            }

            var emFull = world.EntityManager;
            using var query = emFull.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState), typeof(PlanetGemMoonState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var moonStates = query.ToComponentDataArray<PlanetGemMoonState>(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;
                moonState = moonStates[i];
                return true;
            }

            return false;
        }

        /// <summary>Planet world position, scale, and state by <see cref="PlanetState.PlanetId"/>.</summary>
        public static bool TryGetPlanetPoseByPlanetId(int planetId, out float3 position, out float scale, out PlanetState state)
        {
            position = default;
            scale = 1f;
            state = default;
            if (planetId == 0)
                return false;

            if (IsLocalHost() && TryFindPlanetPose(ServerWorld, planetId, out position, out scale, out state))
                return true;

            if (TryFindPlanetPose(ClientWorld, planetId, out position, out scale, out state))
                return true;

            return false;
        }

        /// <summary>Planet visual spin rotation for minimap and world labels.</summary>
        public static bool TryGetPlanetRotationByPlanetId(int planetId, out quaternion rotation)
        {
            rotation = quaternion.identity;
            if (planetId == 0)
                return false;

            if (IsLocalHost() && TryFindPlanetRotation(ServerWorld, planetId, out rotation))
                return true;

            return TryFindPlanetRotation(ClientWorld, planetId, out rotation);
        }

        /// <summary>Planet pose (position, scale, state) — quarantine-safe via hybrid proxies on Windows.</summary>
        static bool TryFindPlanetPose(World world, int planetId, out float3 position, out float scale, out PlanetState state)
        {
            position = default;
            scale = 1f;
            state = default;
            if (world == null || !world.IsCreated)
                return false;

            // [TITAN-ORBIT] Settling OFF alone is NOT safe — TransformQuarantine stays on all in-game.
            // Player.log 2026-07-19 10:29: Settling OFF → planet gather → Crash!!!
            if (ClientJoinSettleCache.Settling || ClientJoinSettleCache.GhostSpawnBacklog)
                return false;

            var em = world.EntityManager;
            if (ClientJoinSettleCache.TransformQuarantine)
            {
                return EcsWorldVisualizer.Active != null &&
                       EcsWorldVisualizer.Active.TryGetPlanetPoseByPlanetId(
                           em, planetId, out position, out scale, out state);
            }

            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState), typeof(LocalTransform));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;
                state = states[i];
                position = transforms[i].Position;
                scale = math.max(0.25f, transforms[i].Scale);
                return true;
            }

            return false;
        }

        /// <summary>Planet <see cref="LocalTransform.Rotation"/> — quarantine-safe via hybrid proxies.</summary>
        static bool TryFindPlanetRotation(World world, int planetId, out quaternion rotation)
        {
            rotation = quaternion.identity;
            if (world == null || !world.IsCreated)
                return false;

            if (ClientJoinSettleCache.Settling || ClientJoinSettleCache.GhostSpawnBacklog)
                return false;

            var em = world.EntityManager;
            if (ClientJoinSettleCache.TransformQuarantine)
            {
                if (EcsWorldVisualizer.Active == null ||
                    !EcsWorldVisualizer.Active.TryGetPlanetPoseByPlanetId(
                        em, planetId, out _, out _, out _))
                    return false;

                // Re-walk for rotation only (pose helper does not return rot — read entity again).
                foreach (var entity in GetScratchProxyEntities())
                {
                    if (!em.Exists(entity) ||
                        !em.HasComponent<PlanetState>(entity) ||
                        !em.HasComponent<LocalTransform>(entity))
                        continue;
                    if (em.GetComponentData<PlanetState>(entity).PlanetId != planetId)
                        continue;
                    rotation = em.GetComponentData<LocalTransform>(entity).Rotation;
                    return true;
                }

                return false;
            }

            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState), typeof(LocalTransform));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;
                rotation = transforms[i].Rotation;
                return true;
            }

            return false;
        }

        static readonly List<Entity> s_ProxyEntityScratch = new List<Entity>(256);

        /// <summary>Fills scratch from <see cref="EcsWorldVisualizer"/> hybrid registry (no ECS gather).</summary>
        static List<Entity> GetScratchProxyEntities()
        {
            s_ProxyEntityScratch.Clear();
            if (EcsWorldVisualizer.Active != null)
                EcsWorldVisualizer.Active.CopyLiveProxyEntities(s_ProxyEntityScratch);
            return s_ProxyEntityScratch;
        }
    }
}
