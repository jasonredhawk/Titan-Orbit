using System.Collections.Generic;
using TitanOrbit.Data;
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
    /// Local muzzle resolve for cosmetic bullet VFX.
    /// <para>
    /// Prefers the live weapon component: <c>origin = weapon.position</c>, aim = <b>unbanked</b>
    /// planar <c>weapon.forward</c> (BankPivot roll stripped) + authored
    /// <see cref="ShipWeaponMountAuthoring.DirectionAngleDeg"/>. That keeps sequential fire aligned
    /// with each barrel mesh. Falls back to ECS <see cref="ShipWeaponPose"/> when no live GO.
    /// Velocity is <c>aim * BulletSpeed + shipVel</c> (planar). Damage is server-side.
    /// </para>
    /// </summary>
    public static class BulletMuzzlePresentation
    {
        /// <summary>[UNITY] Created by <see cref="ShipBankVisualApplier"/> under the hull root.</summary>
        const string BankPivotName = "BankPivot";

        /// <summary>
        /// If presentation lags predicted sim by more than this × one-tick travel, use predicted pose
        /// for the ECS fallback path only (live GO path uses drawn weapon transforms).
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

        /// <summary>Scratch list for live weapon discovery (main thread only).</summary>
        static readonly List<LiveWeaponMount> s_LiveMountScratch = new List<LiveWeaponMount>(8);

        /// <summary>One offensive barrel on the drawn hull (authoring + live Transform).</summary>
        struct LiveWeaponMount
        {
            public Transform Weapon;
            public float DirectionAngleDeg;
            public int CannonIndex;
            /// <summary>
            /// Discovery order before sort — secondary key so equal CannonIndex stays stable
            /// (List.Sort is unstable; ties used to reshuffle barrels → random aim).
            /// </summary>
            public int CollectOrder;
        }

        /// <summary>
        /// Resolves world muzzle origin/forward for one mount index on the local ship.
        /// Live GO path: tip position + unbanked planar forward of that weapon component.
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

            // --- Preferred: live weapon component (position + unbanked aim) ---
            int cannonIndex = 0;
            if (TryGetMountElementFromBuffer(em, shipEntity, mountIndex, out ShipWeaponMountElement ecsMount))
                cannonIndex = ecsMount.CannonIndex;

            if (TryResolveLiveWeaponMuzzle(em, shipEntity, mountIndex, cannonIndex,
                    out fireOrigin, out fireForward, out float3 hullPosForVel))
            {
                isDisplaySpace = true;
                shipVel = GetLocalShipVelocity(em, shipEntity, hullPosForVel);
                return true;
            }

            // --- Fallback: catalog/bake buffer + yaw-only ShipWeaponPose ---
            if (!TryGetLocalHullTransform(em, shipEntity, out LocalTransform shipTransform, out isDisplaySpace))
                return false;

            if (!TryGetMountElementFromBuffer(em, shipEntity, mountIndex, out ShipWeaponMountElement mount))
                return false;

            if (!ShipWeaponPose.TryResolve(shipTransform, mount, out fireOrigin, out fireForward))
                return false;

            shipVel = GetLocalShipVelocity(em, shipEntity, shipTransform.Position);
            fireForward.y = 0f;
            if (math.lengthsq(fireForward) < 0.0001f)
                fireForward = new float3(0f, 0f, 1f);
            else
                fireForward = math.normalize(fireForward);

            return true;
        }

        /// <summary>
        /// Count of live weapon barrels on the local hull (GO path). 0 when none / no hull.
        /// Used by anticipation volleys so index matches <see cref="TryResolveMuzzle"/>.
        /// </summary>
        public static int GetLiveWeaponMountCount(EntityManager em, Entity shipEntity)
        {
            if (!TryCollectLiveWeaponMounts(em, shipEntity, s_LiveMountScratch))
                return 0;
            return s_LiveMountScratch.Count;
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
        /// Rewrites a local-owner spawn onto current live/presentation muzzle with correct velocity.
        /// Returns false when the local muzzle cannot be resolved — caller should keep server pose.
        /// </summary>
        public static bool TryReprojectLocalOwnerSpawn(ref BulletVfxBridge.SpawnRequest req)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!TryGetLocalShipEntity(em, out Entity shipEntity))
                return false;

            if (req.OwnerNetworkId <= 0 && em.HasComponent<GhostOwner>(shipEntity))
                req.OwnerNetworkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            if (req.OwnerNetworkId <= 0)
                req.OwnerNetworkId = EcsGameBridge.GetLocalNetworkId();

            if (!req.IsAnticipation && !IsLocalOwner(req.OwnerNetworkId))
                return false;

            if (!em.HasComponent<ShipWeaponConfig>(shipEntity))
                return false;

            // [TITAN-ORBIT] Use the spawn's MountIndex — hardcoding 0 snapped every volley bullet
            // onto the first barrel after upgrade-tree multi-cannon hulls landed.
            int mountIndex = req.MountIndex < 0 ? 0 : req.MountIndex;
            if (!TryResolveMuzzle(em, shipEntity, mountIndex, out float3 origin, out float3 forward,
                    out bool displaySpace, out float3 shipVel))
                return false;

            var weaponCfg = em.GetComponentData<ShipWeaponConfig>(shipEntity);
            req.SpawnPosition = origin;
            req.Velocity = BuildBulletWorldVelocity(forward, weaponCfg.BulletSpeed, shipVel);
            req.IsDisplaySpace = displaySpace;
            return true;
        }

        /// <summary>
        /// True when <paramref name="ownerNetworkId"/> is the local player's GhostOwner id.
        /// </summary>
        public static bool IsLocalOwner(int ownerNetworkId)
        {
            if (ownerNetworkId <= 0)
                return false;

            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId > 0)
                return ownerNetworkId == localId;

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
        /// Live barrel tip + aim along that weapon component. BankPivot roll is stripped so aim
        /// matches the barrel’s hull-relative facing (not a banked wild XZ spray).
        /// </summary>
        static bool TryResolveLiveWeaponMuzzle(
            EntityManager em,
            Entity shipEntity,
            int mountIndex,
            int cannonIndex,
            out float3 fireOrigin,
            out float3 fireForward,
            out float3 hullPosForVel)
        {
            fireOrigin = default;
            fireForward = new float3(0f, 0f, 1f);
            hullPosForVel = float3.zero;

            if (!TryCollectLiveWeaponMounts(em, shipEntity, s_LiveMountScratch))
                return false;

            int count = s_LiveMountScratch.Count;
            if (count <= 0)
                return false;

            // Unique CannonIndex → exact barrel. Duplicate/default 0s → sorted list index.
            int matchCount = 0;
            int matchIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (s_LiveMountScratch[i].CannonIndex != cannonIndex)
                    continue;
                matchCount++;
                matchIdx = i;
            }

            int idx;
            if (matchCount == 1)
                idx = matchIdx;
            else
            {
                idx = mountIndex % count;
                if (idx < 0)
                    idx = 0;
            }

            LiveWeaponMount live = s_LiveMountScratch[idx];
            if (live.Weapon == null)
                return false;

            // Exact component tip — BankPivot may lift Y; that is intentional for the flash.
            fireOrigin = live.Weapon.position;

            // --- Unbanked planar aim (strip BankPivot roll, keep weapon local yaw/pitch facing) ---
            Vector3 unbankedFwd = GetUnbankedWorldForward(live.Weapon);
            if (!TryBuildPlanarAimFromWeaponForward(unbankedFwd, live.DirectionAngleDeg, out fireForward))
                return false;

            if (TryGetLocalHullRoot(em, shipEntity, out Transform hullRoot) && hullRoot != null)
                hullPosForVel = hullRoot.position;
            else
                hullPosForVel = fireOrigin;

            return true;
        }

        /// <summary>
        /// World forward of <paramref name="weapon"/> with <c>BankPivot</c> roll removed so aim
        /// matches the hull-relative weapon component (same facing the player sees on the mesh).
        /// </summary>
        static Vector3 GetUnbankedWorldForward(Transform weapon)
        {
            if (weapon == null)
                return Vector3.forward;

            Transform bank = FindBankPivotAncestor(weapon);
            if (bank == null || bank.parent == null)
                return weapon.forward;

            // World → BankPivot local (includes roll) → world via hull parent (yaw only).
            Vector3 inBank = bank.InverseTransformDirection(weapon.forward);
            return bank.parent.TransformDirection(inBank);
        }

        /// <summary>Walks parents for the BankPivot created by <see cref="ShipBankVisualApplier"/>.</summary>
        static Transform FindBankPivotAncestor(Transform start)
        {
            Transform t = start;
            while (t != null)
            {
                if (t.name == BankPivotName)
                    return t;
                t = t.parent;
            }

            return null;
        }

        /// <summary>
        /// Planar XZ aim from a world forward, then optional yaw offset (legacy Starship).
        /// </summary>
        static bool TryBuildPlanarAimFromWeaponForward(
            Vector3 weaponForward,
            float directionAngleDeg,
            out float3 fireForward)
        {
            float3 fwd = weaponForward;
            fwd.y = 0f;
            if (math.lengthsq(fwd) < 0.0001f)
                fwd = new float3(0f, 0f, 1f);
            else
                fwd = math.normalize(fwd);

            float angleRad = math.radians(directionAngleDeg);
            float3 right = math.normalize(math.cross(new float3(0f, 1f, 0f), fwd));
            fireForward = math.normalize(fwd * math.cos(angleRad) + right * math.sin(angleRad));
            fireForward.y = 0f;
            if (math.lengthsq(fireForward) < 0.0001f)
                return false;
            fireForward = math.normalize(fireForward);
            return true;
        }

        /// <summary>
        /// Discovers offensive barrels on the drawn hull: authoring markers first, then weapon-named
        /// children (<see cref="ShipComponentAbilityStatsMath.IsWeaponComponent"/> / "Weapon").
        /// Order matches ECS buffer: CannonIndex, then discovery order (stable ties).
        /// </summary>
        static bool TryCollectLiveWeaponMounts(
            EntityManager em,
            Entity shipEntity,
            List<LiveWeaponMount> into)
        {
            into.Clear();
            if (!TryGetLocalHullRoot(em, shipEntity, out Transform hullRoot) || hullRoot == null)
                return false;

            int collectOrder = 0;

            // --- Authoring markers (preferred) ---
            var authorings = hullRoot.GetComponentsInChildren<ShipWeaponMountAuthoring>(true);
            if (authorings != null)
            {
                for (int i = 0; i < authorings.Length; i++)
                {
                    var a = authorings[i];
                    if (a == null || a.transform == hullRoot)
                        continue;
                    into.Add(new LiveWeaponMount
                    {
                        Weapon = a.transform,
                        DirectionAngleDeg = a.DirectionAngleDeg,
                        CannonIndex = a.CannonIndex,
                        CollectOrder = collectOrder++,
                    });
                }
            }

            if (into.Count > 0)
            {
                EnsureUniqueLiveCannonIndices(into);
                into.Sort(CompareLiveMountStable);
                return true;
            }

            // --- Name / family id scan (same rules as chassis bake) ---
            var transforms = hullRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t == null || t == hullRoot)
                    continue;
                if (!ShipWeaponMountCollector.LooksLikeWeaponTransform(t))
                    continue;

                into.Add(new LiveWeaponMount
                {
                    Weapon = t,
                    DirectionAngleDeg = 0f,
                    // Unique indices so sort matches hierarchy discovery order.
                    CannonIndex = collectOrder,
                    CollectOrder = collectOrder,
                });
                collectOrder++;
            }

            if (into.Count > 0)
                into.Sort(CompareLiveMountStable);

            return into.Count > 0;
        }

        /// <summary>
        /// [STANDARD] CannonIndex primary, CollectOrder secondary — List.Sort is unstable on ties.
        /// </summary>
        static int CompareLiveMountStable(LiveWeaponMount a, LiveWeaponMount b)
        {
            int byCannon = a.CannonIndex.CompareTo(b.CannonIndex);
            if (byCannon != 0)
                return byCannon;
            return a.CollectOrder.CompareTo(b.CollectOrder);
        }

        /// <summary>
        /// When every live barrel still has the same authored CannonIndex (usually 0), rewrite
        /// to discovery order so slots align with the ECS mount buffer after bake uniquify.
        /// </summary>
        static void EnsureUniqueLiveCannonIndices(List<LiveWeaponMount> mounts)
        {
            if (mounts == null || mounts.Count <= 1)
                return;

            bool allSame = true;
            int first = mounts[0].CannonIndex;
            for (int i = 1; i < mounts.Count; i++)
            {
                if (mounts[i].CannonIndex != first)
                {
                    allSame = false;
                    break;
                }
            }

            if (!allSame)
                return;

            for (int i = 0; i < mounts.Count; i++)
            {
                var m = mounts[i];
                m.CannonIndex = m.CollectOrder;
                mounts[i] = m;
            }
        }

        /// <summary>ECS mount buffer only (no GO recomposition).</summary>
        static bool TryGetMountElementFromBuffer(
            EntityManager em,
            Entity shipEntity,
            int mountIndex,
            out ShipWeaponMountElement mount)
        {
            mount = default;
            if (!em.HasBuffer<ShipWeaponMountElement>(shipEntity))
                return false;

            var mounts = em.GetBuffer<ShipWeaponMountElement>(shipEntity);
            if (mounts.Length <= 0)
                return false;

            int idx = mountIndex % mounts.Length;
            if (idx < 0)
                idx = 0;
            mount = mounts[idx];
            return true;
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
        /// Hull transform for ECS fallback path: predicted when soft-track lags; else presentation.
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
