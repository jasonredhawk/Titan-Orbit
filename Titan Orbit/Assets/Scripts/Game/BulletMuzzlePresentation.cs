using TitanOrbit.ECS;
using TitanOrbit.ECS.Authoring;
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
    /// Starblast-style local muzzle resolve for cosmetic bullet VFX.
    /// <para>
    /// World tracer velocity is always <c>aimDir * BulletSpeed + shipVel</c> (same as server),
    /// so shots leave the nose at relative muzzle speed while flying. Hull pose prefers predicted
    /// <see cref="LocalTransform"/> when soft-track lags; otherwise matches the drawn hybrid hull.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Under hybrid / TransformQuarantine, client ECS mount buffers are often empty
    /// while the server fills mounts from the hull GO (<see cref="ShipWeaponMountSyncSystem"/>).
    /// Cosmetics therefore fall back to <see cref="ShipWeaponMountAuthoring"/> on the visual hull
    /// so tracers still Instantiates. Damage remains server-side.
    /// </para>
    /// </summary>
    public static class BulletMuzzlePresentation
    {
        /// <summary>
        /// If presentation lags predicted sim by more than this × one-tick travel, use predicted pose.
        /// </summary>
        const float PresentationLagTicks = 1.25f;

        /// <summary>Minimum |vel| treated as real kinematics (not noise).</summary>
        const float MinKinematicsSpeed = 0.05f;

        /// <summary>Last hull position for pose-delta velocity fallback.</summary>
        static float3 s_LastHullPos;

        /// <summary>True after <see cref="s_LastHullPos"/> has been written once.</summary>
        static bool s_HasLastHullPos;

        /// <summary>Realtime of last hull sample (for delta dt).</summary>
        static float s_LastHullSampleTime;

        /// <summary>
        /// Resolves world muzzle origin/forward for one mount index on the local ship.
        /// <paramref name="shipVel"/> is planar hull velocity (kinematics or pose-delta).
        /// No origin lead — Starblast lock is pose + <c>shipVel</c> in velocity only; inventing a
        /// future muzzle lets the first tracer visually pierce rocks the server has not hit.
        /// </summary>
        public static bool TryResolveMuzzle(
            EntityManager em,
            Entity shipEntity,
            int mountIndex,
            out float3 fireOrigin,
            out float3 fireForward,
            out bool isDisplaySpace,
            out float3 shipVel)
        {
            fireOrigin = default;
            fireForward = new float3(0f, 0f, 1f);
            isDisplaySpace = false;
            shipVel = float3.zero;

            if (!em.Exists(shipEntity))
                return false;

            if (!TryGetLocalHullTransform(em, shipEntity, out LocalTransform shipTransform, out isDisplaySpace))
                return false;

            if (!TryGetMountElement(em, shipEntity, mountIndex, out ShipWeaponMountElement mount))
                return false;

            if (!ShipWeaponPose.TryResolve(shipTransform, mount, out fireOrigin, out fireForward))
                return false;

            // Velocity from hull pose; origin stays on the drawn/predicted barrels (no lead).
            // Keep fireOrigin.y from the weapon mount — do not slam to 0 / hull Y.
            shipVel = GetLocalShipVelocity(em, shipEntity, shipTransform.Position);

            fireForward.y = 0f;
            if (math.lengthsq(fireForward) < 0.0001f)
                fireForward = new float3(0f, 0f, 1f);
            else
                fireForward = math.normalize(fireForward);

            return true;
        }

        /// <summary>
        /// Builds Starblast world velocity: <c>aim * BulletSpeed + shipVel</c> (planar).
        /// Matches <see cref="BulletSimulationSystem"/> server spawn math.
        /// </summary>
        public static float3 BuildBulletWorldVelocity(float3 fireForward, float bulletSpeed, float3 shipVel)
        {
            float3 aim = fireForward;
            aim.y = 0f;
            if (math.lengthsq(aim) < 0.0001f)
                aim = new float3(0f, 0f, 1f);
            else
                aim = math.normalize(aim);

            float3 v = shipVel;
            v.y = 0f;
            return aim * math.max(1f, bulletSpeed) + v;
        }

        /// <summary>
        /// Rewrites a local-owner spawn onto current predicted/presentation muzzle with correct velocity.
        /// Returns false when the local muzzle cannot be resolved — caller should keep server pose
        /// (never leave the player with zero tracers).
        /// </summary>
        public static bool TryReprojectLocalOwnerSpawn(ref BulletVfxBridge.SpawnRequest req)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!TryGetLocalShipEntity(em, out Entity shipEntity))
                return false;

            // Ensure OwnerNetworkId is set for adopt / local checks.
            if (req.OwnerNetworkId <= 0 && em.HasComponent<GhostOwner>(shipEntity))
                req.OwnerNetworkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            if (req.OwnerNetworkId <= 0)
                req.OwnerNetworkId = EcsGameBridge.GetLocalNetworkId();

            // Local-only: anticipation always; server packets must match local owner (after id fill).
            if (!req.IsAnticipation && !IsLocalOwner(req.OwnerNetworkId))
                return false;

            if (!em.HasComponent<ShipWeaponConfig>(shipEntity))
                return false;

            if (!TryResolveMuzzle(em, shipEntity, 0, out float3 origin, out float3 forward,
                    out bool displaySpace, out float3 shipVel))
                return false;

            var weaponCfg = em.GetComponentData<ShipWeaponConfig>(shipEntity);
            // [TITAN-ORBIT] Same formula as BulletSimulationSystem — never strip shipVel via length hacks.
            req.SpawnPosition = origin;
            req.Velocity = BuildBulletWorldVelocity(forward, weaponCfg.BulletSpeed, shipVel);
            req.IsDisplaySpace = displaySpace;
            return true;
        }

        /// <summary>
        /// True when <paramref name="ownerNetworkId"/> is the local player's GhostOwner id.
        /// Falls back to the local ship entity's <see cref="GhostOwner"/> when
        /// <see cref="EcsGameBridge.GetLocalNetworkId"/> is not ready yet.
        /// </summary>
        public static bool IsLocalOwner(int ownerNetworkId)
        {
            if (ownerNetworkId <= 0)
                return false;

            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId > 0)
                return ownerNetworkId == localId;

            // --- Id edge case (join / host timing) ---
            // [NETCODE] NetworkId singleton can lag briefly; LocalPlayerShipTag / GhostOwnerIsLocal still resolve.
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;
            var em = world.EntityManager;
            if (!TryGetLocalShipEntity(em, out Entity shipEntity) ||
                !em.HasComponent<GhostOwner>(shipEntity))
                return false;
            return em.GetComponentData<GhostOwner>(shipEntity).NetworkId == ownerNetworkId;
        }

        /// <summary>
        /// Local ship via tag / GhostOwnerIsLocal first (works even when NetworkId lookup fails).
        /// </summary>
        public static bool TryGetLocalShipEntity(EntityManager em, out Entity shipEntity)
        {
            if (EcsGameBridge.TryGetLocalShipEntityOnWorld(EcsGameBridge.ClientWorld, out shipEntity) &&
                em.Exists(shipEntity))
                return true;

            shipEntity = Entity.Null;
            return false;
        }

        /// <summary>
        /// ECS mount buffer first; hybrid GO <see cref="ShipWeaponMountAuthoring"/> when the
        /// client buffer is empty (common under TransformQuarantine — server sync is server-only).
        /// </summary>
        static bool TryGetMountElement(
            EntityManager em,
            Entity shipEntity,
            int mountIndex,
            out ShipWeaponMountElement mount)
        {
            mount = default;

            // --- ECS buffer (baked / catalog) ---
            if (em.HasBuffer<ShipWeaponMountElement>(shipEntity))
            {
                var mounts = em.GetBuffer<ShipWeaponMountElement>(shipEntity);
                if (mounts.Length > 0)
                {
                    int idx = mountIndex % mounts.Length;
                    if (idx < 0)
                        idx = 0;
                    mount = mounts[idx];
                    return true;
                }
            }

            // --- Hybrid GO fallback (presentation hull) ---
            if (!TryGetLocalHullRoot(em, shipEntity, out Transform hullRoot))
                return false;

            var authorings = hullRoot.GetComponentsInChildren<ShipWeaponMountAuthoring>(true);
            if (authorings == null || authorings.Length == 0)
                return false;

            // Collect valid mounts (skip hull root itself).
            int validCount = 0;
            for (int i = 0; i < authorings.Length; i++)
            {
                var a = authorings[i];
                if (a != null && a.transform != hullRoot)
                    validCount++;
            }

            if (validCount == 0)
                return false;

            int target = mountIndex % validCount;
            if (target < 0)
                target = 0;

            int seen = 0;
            for (int i = 0; i < authorings.Length; i++)
            {
                var a = authorings[i];
                if (a == null || a.transform == hullRoot)
                    continue;
                if (seen == target)
                {
                    ShipChassisPrefabBakeUtility.GetHullRootLocalPose(
                        hullRoot, a.transform, out float3 localPos, out quaternion localRot);
                    mount = new ShipWeaponMountElement
                    {
                        LocalPosition = localPos,
                        LocalRotation = localRot,
                        DirectionAngleDeg = a.DirectionAngleDeg,
                        CannonIndex = a.CannonIndex,
                    };
                    return true;
                }

                seen++;
            }

            return false;
        }

        /// <summary>Hull GO for mount authorings — registry by network id, else local visual root.</summary>
        static bool TryGetLocalHullRoot(EntityManager em, Entity shipEntity, out Transform hullRoot)
        {
            hullRoot = null;
            int networkId = 0;
            if (em.HasComponent<GhostOwner>(shipEntity))
                networkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            if (networkId <= 0)
                networkId = EcsGameBridge.GetLocalNetworkId();

            if (networkId > 0 && ShipWeaponProxyRegistry.TryGetHull(networkId, out hullRoot) && hullRoot != null)
                return true;

            hullRoot = EcsWorldVisualizer.LocalPlayerShipVisualRoot;
            return hullRoot != null;
        }

        /// <summary>
        /// Hull transform: predicted when soft-track lags; else live proxy / ShipDisplayPose.
        /// </summary>
        static bool TryGetLocalHullTransform(
            EntityManager em,
            Entity shipEntity,
            out LocalTransform shipTransform,
            out bool isDisplaySpace)
        {
            shipTransform = default;
            isDisplaySpace = false;

            bool hasPredicted = em.HasComponent<LocalTransform>(shipEntity);
            LocalTransform predicted = hasPredicted
                ? em.GetComponentData<LocalTransform>(shipEntity)
                : default;

            // --- Presentation candidates (drawn hull / camera) ---
            bool hasPresentation = false;
            LocalTransform presentation = default;
            Transform visualRoot = EcsWorldVisualizer.LocalPlayerShipVisualRoot;
            if (visualRoot != null)
            {
                float scale = hasPredicted ? predicted.Scale : 1f;
                presentation = LocalTransform.FromPositionRotationScale(
                    visualRoot.position, visualRoot.rotation, scale);
                hasPresentation = true;
            }
            else if (ShipDisplayPose.HasLocalPose)
            {
                float scale = hasPredicted ? predicted.Scale : 1f;
                presentation = LocalTransform.FromPositionRotationScale(
                    ShipDisplayPose.LocalPosition, ShipDisplayPose.LocalRotation, scale);
                hasPresentation = true;
            }

            // --- Starblast lock: when soft-track trails predicted sim, fire from predicted nose ---
            if (hasPredicted && hasPresentation)
            {
                float3 shipVel = ReadKinematicsOrZero(em, shipEntity);
                float tickTravel = math.max(math.length(shipVel) * (1f / 60f), 0.05f);
                float gap = math.distance(predicted.Position, presentation.Position);
                if (gap > tickTravel * PresentationLagTicks)
                {
                    shipTransform = predicted;
                    isDisplaySpace = false;
                    return true;
                }

                shipTransform = presentation;
                isDisplaySpace = true;
                return true;
            }

            if (hasPresentation)
            {
                shipTransform = presentation;
                isDisplaySpace = true;
                return true;
            }

            if (hasPredicted)
            {
                shipTransform = predicted;
                isDisplaySpace = false;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Ship planar velocity: kinematics if strong enough, else pose-delta of the hull.
        /// </summary>
        public static float3 GetLocalShipVelocity(EntityManager em, Entity shipEntity, float3 currentHullPos)
        {
            float3 kin = ReadKinematicsOrZero(em, shipEntity);
            if (math.lengthsq(kin) >= MinKinematicsSpeed * MinKinematicsSpeed)
            {
                UpdateHullSample(currentHullPos);
                return kin;
            }

            // --- Pose-delta fallback (hull is moving but kinematics missing/stale) ---
            float now = Time.realtimeSinceStartup;
            float3 deltaVel = float3.zero;
            if (s_HasLastHullPos)
            {
                float dt = now - s_LastHullSampleTime;
                if (dt > 1e-4f && dt < 0.25f)
                {
                    float3 delta = currentHullPos - s_LastHullPos;
                    delta.y = 0f;
                    if (math.lengthsq(delta) > 1e-6f)
                        deltaVel = delta / dt;
                }
            }

            UpdateHullSample(currentHullPos);
            deltaVel.y = 0f;
            return deltaVel;
        }

        static float3 ReadKinematicsOrZero(EntityManager em, Entity shipEntity)
        {
            if (!em.HasComponent<ShipKinematics>(shipEntity))
                return float3.zero;
            float3 v = em.GetComponentData<ShipKinematics>(shipEntity).Velocity;
            v.y = 0f;
            return v;
        }

        static void UpdateHullSample(float3 currentHullPos)
        {
            s_LastHullPos = currentHullPos;
            s_LastHullPos.y = 0f;
            s_LastHullSampleTime = Time.realtimeSinceStartup;
            s_HasLastHullPos = true;
        }
    }
}
