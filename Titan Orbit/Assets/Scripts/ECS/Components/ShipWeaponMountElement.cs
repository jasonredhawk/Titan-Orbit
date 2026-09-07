using TitanOrbit.Simulation;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] One weapon mount on a ship hull — local offset, rotation, and <b>per-barrel</b>
    /// combat stats. Stored in a DynamicBuffer so multi-cannon ships fire from multiple muzzles.
    /// Pose is baked from child <see cref="Authoring.ShipWeaponMountAuthoring"/> / chassis prefab
    /// Weapon children via <see cref="ShipChassisPrefabBakeUtility"/>. Combat numbers come from
    /// <see cref="ShipWeaponMountCombatLogic"/> (family Weapon catalog stats × ship level).
    /// <para>
    /// <see cref="LocalPosition"/> is <b>unscaled prefab-local</b> (same contract as
    /// <see cref="ShipWingTractorBeamElement"/>). <see cref="ShipWeaponPose"/> multiplies by
    /// <see cref="BodyCollisionMath.ShipPresentationScale"/> at fire time so server muzzles match
    /// the hybrid hull (which is drawn at ~0.155× prefab size).
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Each barrel keeps its own <see cref="FirePower"/> and <see cref="FireRate"/> —
    /// not a hull average. Regular-ship weapon stats ignore prefab child scale so a fat mesh
    /// and a slim mesh with the same catalog Weapon row deal the same damage and cadence.
    /// </para>
    /// </summary>
    public struct ShipWeaponMountElement : IBufferElementData
    {
        /// <summary>
        /// Unscaled hull-root local offset from the chassis prefab bake.
        /// Presentation scale is applied in <see cref="ShipWeaponPose.TryResolve"/> — do not
        /// pre-multiply when writing the buffer.
        /// </summary>
        public float3 LocalPosition;

        /// <summary>[UNITY] Local rotation of the mount relative to hull.</summary>
        public quaternion LocalRotation;

        /// <summary>[TITAN-ORBIT] Extra yaw offset in degrees for angled cannons.</summary>
        public float DirectionAngleDeg;

        /// <summary>[TITAN-ORBIT] Index into weapon config arrays for multi-cannon loadouts.</summary>
        public int CannonIndex;

        /// <summary>
        /// [TITAN-ORBIT] Damage and energy cost for bullets from this barrel only
        /// (catalog family firePower + ship level + Fire Power attributes).
        /// </summary>
        public float FirePower;

        /// <summary>
        /// [TITAN-ORBIT] Shots per second for this barrel only (catalog family fireRate +
        /// ship level). Independent of other mounts — see <see cref="FireCooldown"/>.
        /// </summary>
        public float FireRate;

        /// <summary>
        /// [TITAN-ORBIT] Seconds until this barrel may fire again. Ticked per mount so mixed
        /// calibers keep different cadences while Fire is held.
        /// </summary>
        public float FireCooldown;

        /// <summary>
        /// [TITAN-ORBIT] Level-1 firePower for this barrel (before attributes) — bullet VFX
        /// growth baseline so a fat gun looks larger than a peashooter at the same ship level.
        /// </summary>
        public float ReferenceFirePower;

        /// <summary>
        /// [TITAN-ORBIT] Acquire + travel range for this barrel (MEGA catalog component
        /// <c>bulletRange</c>). Regular ships leave this 0 and use hull <c>ShipWeaponConfig</c>.
        /// </summary>
        public float BulletRange;

        /// <summary>
        /// [TITAN-ORBIT] Muzzle speed for this barrel (MEGA unique-component
        /// <c>bulletSpeed</c>). Regular ships leave this 0 and use hull
        /// <c>ShipWeaponConfig.BulletSpeed</c>. Not a hull sum — guns/cannons/snipers
        /// each keep their own catalog number.
        /// </summary>
        public float BulletSpeed;

        /// <summary>
        /// [TITAN-ORBIT] MEGA turret traverse in degrees/sec. Regular ships leave this 0
        /// (barrels stay at bake pose). Written by <c>MegaShipStatApplyLogic</c> from
        /// <c>MegaShipPartStats.weaponRotationSpeed</c> after runtime defaults/minimums.
        /// </summary>
        public float WeaponRotationSpeed;

        /// <summary>
        /// [TITAN-ORBIT] MEGA per-mount <c>BulletVfxBank</c> category from the catalog unique
        /// weapon row (or type-table default). Regular ships leave this 0 and fire the
        /// hull <c>ShipLoadoutState.RuntimeBulletIndex</c>.
        /// </summary>
        public int BulletBankIndex;
    }

    /// <summary>
    /// [ECS/DOTS] Shared muzzle origin and fire direction from ship hull transform + weapon mount
    /// buffer element. Single source of truth for where bullets spawn — used by
    /// <see cref="BulletSimulationSystem"/> (server hits) and client tracer VFX bridges
    /// (ECS fallback when live GO weapons are missing).
    /// [BurstCompile] target per ship-simulation architecture rule.
    /// </summary>
    public static class ShipWeaponPose
    {
        /// <summary>
        /// [ECS/DOTS] Server / authority muzzle from unbanked catalog-bake locals + yaw-only ship.
        /// Client cosmetics prefer live weapon <c>Transform.position</c>
        /// (<c>BulletMuzzlePresentation</c>) so BankPivot banking matches the drawn barrel.
        /// Do not feed banked GO locals into this path — yaw-only × banked local lifts the muzzle.
        /// </summary>
        /// <param name="shipTransform">Ship hull LocalTransform at fire time (yaw-only sim).</param>
        /// <param name="mount">Unbanked bake/catalog mount (hull-root local, prefab units).</param>
        /// <param name="fireOrigin">Output muzzle world position (keeps mount local Y).</param>
        /// <param name="fireForward">Output normalized fire direction on XZ plane.</param>
        /// <returns>False if the computed forward vector degenerates to zero length.</returns>
        [BurstCompile]
        public static bool TryResolve(
            in LocalTransform shipTransform,
            in ShipWeaponMountElement mount,
            out float3 fireOrigin,
            out float3 fireForward)
        {
            fireOrigin = float3.zero;
            fireForward = new float3(0f, 0f, 1f);

            // --- Local mount forward, flattened to XZ plane ---
            // [TITAN-ORBIT] Top-down shooter — ignore vertical aim component.
            float3 localFwd = math.mul(mount.LocalRotation, new float3(0f, 0f, 1f));
            localFwd.y = 0f;
            if (math.lengthsq(localFwd) < 0.0001f)
                localFwd = new float3(0f, 0f, 1f);
            else
                localFwd = math.normalize(localFwd);

            // --- Prefab-local → presentation world (match hybrid hull + wing beams) ---
            // [TITAN-ORBIT] Chassis Weapon children bake at full prefab size (e.g. localX ±4.28).
            // Hybrid ship proxies draw at ShipPresentationScale (~0.155). Without this multiply,
            // server bullets spawned ~4u beside the visible hull while client tracers used live
            // GO muzzles on the scaled mesh — player had to aim ~20° off to land hits.
            float ecsScale = math.max(0.25f, shipTransform.Scale);
            float3 presentationLocal =
                mount.LocalPosition * (BodyCollisionMath.ShipPresentationScale * ecsScale);
            fireOrigin = shipTransform.Position + math.rotate(shipTransform.Rotation, presentationLocal);

            // --- Hull-relative cannon forward ---
            // [TITAN-ORBIT] Legacy Starship convention: hullRot * flatten(Inverse(hullRot) * weaponWorldForward)
            float3 cannonFwd = math.rotate(shipTransform.Rotation, localFwd);
            cannonFwd.y = 0f;
            if (math.lengthsq(cannonFwd) < 0.0001f)
                cannonFwd = math.rotate(shipTransform.Rotation, new float3(0f, 0f, 1f));
            cannonFwd = math.normalize(cannonFwd);

            // --- Apply authored yaw offset for angled cannons ---
            float angleRad = math.radians(mount.DirectionAngleDeg);
            float3 cannonRight = math.normalize(math.cross(new float3(0f, 1f, 0f), cannonFwd));
            fireForward = math.normalize(cannonFwd * math.cos(angleRad) + cannonRight * math.sin(angleRad));
            fireForward.y = 0f;
            if (math.lengthsq(fireForward) < 0.0001f)
                return false;
            fireForward = math.normalize(fireForward);
            return true;
        }
    }
}
