using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Shared constants and deterministic helpers for drone swarm presentation and combat.
    /// Client visuals lift meshes slightly; server fire / hit spheres stay on <see cref="FixedY"/>.
    /// <para>
    /// [TITAN-ORBIT] Lives in the <c>TitanOrbit.Entities</c> assembly (not <c>TitanOrbit.Game</c>) on
    /// purpose: both the client-only <c>DroneSwarmVisualDriver</c> (Game assembly, which references
    /// Entities) and the server-only <c>DroneSwarmCombatSystem</c> (ECS assembly, which also
    /// references Entities) need this math. ECS never references Game — putting shared drone math
    /// here instead of in Game is what lets both sides compile without a circular assembly reference.
    /// </para>
    /// <see cref="DroneSwarmPositioning"/> (below, same file) holds the ship-relative formation math;
    /// this type holds tunable constants + tiny stateless helpers (toroidal distance, buzz phase).
    /// </summary>
    public static class DroneSwarmLogic
    {
        /// <summary>
        /// Floor escort ring radius (world units) so presentation-scaled hulls still clear the mesh.
        /// </summary>
        public const float DefaultOrbitRadius = 1.85f;

        /// <summary>Padding beyond hull radius before orbit multiplier (legacy 0.7, slightly bumped).</summary>
        public const float MarginBeyondHull = 0.85f;

        /// <summary>Multiplies (hull + margin) for escort ring size.</summary>
        public const float OrbitRadiusMultiplier = 2.25f;

        /// <summary>
        /// Rear-hemisphere half-angle from aft (90 = full behind-the-ship half-plane).
        /// Slot angles stay in [180 − this, 180 + this] so drones never sit ahead of the beam.
        /// </summary>
        public const float RearScatterHalfAngleDeg = 90f;

        /// <summary>
        /// Minimum standoff beyond the covering hull along the slot ray.
        /// Sized just past <see cref="DroneSwarmPositioning.DroneHitSphereRadius"/> so meshes
        /// clear the box without sitting a ship-length off the tail.
        /// </summary>
        public const float RearMinClearance = 0.28f;

        /// <summary>
        /// Extra distance beyond <see cref="RearMinClearance"/> (hash 0 → min, hash 1 → min + this).
        /// Keep this small — the covering box already places the slot on the hull face.
        /// </summary>
        public const float RearMaxClearance = 0.35f;

        /// <summary>Lateral spacing between shields on the same block wall.</summary>
        public const float ShieldFormationSpacing = 0.75f;

        /// <summary>Buzz wobble amplitude (world units).</summary>
        public const float BuzzAmplitude = 0.28f;

        /// <summary>Buzz wobble frequency.</summary>
        public const float BuzzSpeed = 3.2f;

        /// <summary>How fast drones drift around the ring (degrees per second) when combat orbit resumes.</summary>
        public const float DefaultOrbitSpeedDeg = 55f;

        /// <summary>
        /// Authoritative flight / combat height — Titan Orbit is XZ gameplay.
        /// Server hit tests, muzzle spawn, and shield intercept math must use this.
        /// </summary>
        public const float FixedY = 0f;

        /// <summary>
        /// Client-only height added on top of the ship presentation Y so buzz meshes clear the deck.
        /// [HYBRID] Never feed into server combat.
        /// </summary>
        public const float PresentationLiftY = 0.28f;

        // --- Combat tuning (legacy DroneSwarmController defaults) ---
        // [TITAN-ORBIT] Per-shot damage is NOT here — fighter/mining use
        // StoreItemData.GetCombatDroneDamage(purchaseLevel). Fire rate / range stay constant.

        /// <summary>Fighter shots per second.</summary>
        public const float FighterFireRate = 1.2f;

        /// <summary>Fighter bullet speed (world units / sec).</summary>
        public const float FighterBulletSpeed = 18f;

        /// <summary>Max toroidal distance from owner ship to an enemy hull before fighter may fire.</summary>
        public const float FighterEngageRange = 6f;

        /// <summary>
        /// Max toroidal distance from owner ship to an enemy planetary-defense pad.
        /// Pads sit on the planet ring and turrets shoot from ~20 (Lv1) to ~40 (Lv6) —
        /// <see cref="FighterEngageRange"/> never reaches them during a normal planet fight.
        /// Fighter acquire also uses the drone bolt travel budget when that is larger.
        /// </summary>
        public const float DefensePadEngageRange = 24f;

        /// <summary>
        /// Synthetic shield/fighter target id for a defense pad. GhostOwner NetworkIds stay
        /// small and positive; 0 remains “no target”.
        /// </summary>
        public const int DefensePadEnemyIdBase = 1_000_000;

        /// <summary>Stable id so server hit-scan and client meshes assign the same pad.</summary>
        public static int MakeDefensePadEnemyId(int planetId, int slotIndex)
        {
            if (planetId <= 0 || slotIndex < 0)
                return 0;
            return DefensePadEnemyIdBase + planetId * 8 + slotIndex;
        }

        /// <summary>True when <paramref name="enemyId"/> was minted by <see cref="MakeDefensePadEnemyId"/>.</summary>
        public static bool IsDefensePadEnemyId(int enemyId) => enemyId >= DefensePadEnemyIdBase;

        /// <summary>Mining shots per second.</summary>
        public const float MiningFireRate = 1f;

        /// <summary>Mining laserbolt speed.</summary>
        public const float MiningBulletSpeed = 16f;

        /// <summary>Max toroidal distance from owner ship to asteroid before mining may fire.</summary>
        public const float MiningEngageRange = 11f;

        /// <summary>Shield block engage range from owner ship.</summary>
        public const float ShieldEngageRange = 16f;

        /// <summary>
        /// Sentinel mount index for drone / world-space spawns.
        /// Local clients must NOT reproject these onto ship weapon barrels.
        /// </summary>
        public const int NoWeaponMountReproject = -1;

        /// <summary>Visual scale of drone-fired bullets (tracer / impact size only).</summary>
        public const float DroneBulletVisualScale = 0.58f;

        /// <summary>
        /// Gameplay strength of drone shots vs the same authored bullet type on a ship.
        /// Applies to damage (via <see cref="StoreItemData.GetCombatDroneDamage"/>), burn DPS,
        /// heal amount, pull/push force, pull radius, and blast radius.
        /// Does <b>not</b> scale bank multipliers (e.g. 1.25 fire power stays 1.25) or
        /// time fields (burn duration, tick interval, shock / gravity lifetime).
        /// </summary>
        public const float DroneFirePowerScale = 1f / 6f;

        /// <summary>
        /// Legacy fighter bank name. Live combat uses the drone's purchase-planet family bank.
        /// </summary>
        public const string FighterBankCategoryName = "Bullets";

        /// <summary>
        /// Legacy mining bank name. Live combat uses the drone's purchase-planet family bank.
        /// </summary>
        public const string MiningBankCategoryName = "Laserbolt";

        /// <summary>
        /// Deterministic buzz/orbit phase from ship network id + slot so peers match.
        /// </summary>
        public static float DeterministicBasePhaseRad(int shipNetworkId, int slotIndex, StoreItemType droneType)
        {
            uint hash = MixSlotHash(shipNetworkId, slotIndex, droneType);
            return (hash % 6283) / 1000f;
        }

        /// <summary>
        /// Two independent [0, 1] values from the same slot seed (rear-scatter angle + distance).
        /// </summary>
        public static void DeterministicUnit01Pair(
            int shipNetworkId,
            int slotIndex,
            StoreItemType droneType,
            out float unitA,
            out float unitB)
        {
            uint hash = MixSlotHash(shipNetworkId, slotIndex, droneType);
            unitA = (hash & 0xFFFFu) / 65535f;
            hash ^= hash >> 13;
            hash *= 0x85EBCA6Bu;
            hash ^= hash >> 16;
            unitB = (hash & 0xFFFFu) / 65535f;
        }

        static uint MixSlotHash(int shipNetworkId, int slotIndex, StoreItemType droneType)
        {
            uint hash = (uint)(shipNetworkId ^ (slotIndex * unchecked((int)0x9E3779B9)) ^ ((int)droneType * unchecked((int)0x85EBCA6B)));
            hash ^= hash >> 16;
            hash *= 0x7FEB352D;
            hash ^= hash >> 15;
            return hash;
        }

        /// <summary>
        /// Presentation-only height offset. Prefer adding <see cref="PresentationLiftY"/> on local Y
        /// while the hub follows the ship.
        /// </summary>
        public static float PresentationWorldY(float optionalBuzzY = 0f)
        {
            return FixedY + PresentationLiftY + optionalBuzzY;
        }

        /// <summary>
        /// Toroidal XZ distance (shortest path on the wrap map). Inline so this assembly does not
        /// need to depend on <c>ToroidalMapEcs</c> (Shared assembly) for a single helper.
        /// </summary>
        public static float ToroidalDistanceXZ(float ax, float az, float bx, float bz, float mapW, float mapH)
        {
            float dx = bx - ax;
            float dz = bz - az;
            if (mapW > 1f)
            {
                while (dx > mapW * 0.5f) dx -= mapW;
                while (dx < -mapW * 0.5f) dx += mapW;
            }
            if (mapH > 1f)
            {
                while (dz > mapH * 0.5f) dz -= mapH;
                while (dz < -mapH * 0.5f) dz += mapH;
            }
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Toroidal shortest offset from A to B on XZ (Y ignored).</summary>
        public static Vector3 ToroidalOffsetXZ(Vector3 from, Vector3 to, float mapW, float mapH)
        {
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            if (mapW > 1f)
            {
                while (dx > mapW * 0.5f) dx -= mapW;
                while (dx < -mapW * 0.5f) dx += mapW;
            }
            if (mapH > 1f)
            {
                while (dz > mapH * 0.5f) dz -= mapH;
                while (dz < -mapH * 0.5f) dz += mapH;
            }
            return new Vector3(dx, 0f, dz);
        }
    }

    /// <summary>
    /// Shared buzz / formation clock for client visuals and server combat.
    /// Writers publish NetCode <c>ServerTick</c> seconds (same timeline as moon orbit);
    /// readers use <see cref="Seconds"/> so mesh pose and muzzle origin stay locked.
    /// <para>
    /// [TITAN-ORBIT] Do not use <c>Time.time</c> for drone buzz — client and server process clocks
    /// diverge on late-join and make mining shots appear from behind the ship instead of the mesh.
    /// </para>
    /// </summary>
    public static class DroneSwarmSimTime
    {
        /// <summary>Last published ServerTick elapsed seconds (0 until first publish).</summary>
        public static double Seconds { get; private set; }

        /// <summary>True after at least one <see cref="Publish"/> this session.</summary>
        public static bool HasValue { get; private set; }

        /// <summary>
        /// Publishes the shared drone clock. Call from server combat each tick and from the
        /// client visual driver each LateUpdate (fractional ServerTick for smooth buzz).
        /// </summary>
        /// <param name="seconds">ServerTick seconds (≥ 0).</param>
        public static void Publish(double seconds)
        {
            if (seconds < 0d)
                seconds = 0d;
            Seconds = seconds;
            HasValue = true;
        }

        /// <summary>
        /// Best available time for pose math: published value, else a caller-provided fallback
        /// (World.Time / Time.time only when NetworkTime is not ready yet).
        /// </summary>
        public static double ResolveOrFallback(double fallbackSeconds)
        {
            return HasValue ? Seconds : fallbackSeconds;
        }
    }

    /// <summary>
    /// Ship-relative drone formation math shared by client visuals and server combat.
    /// Fighter / mining / idle shields scatter in the rear half-plane just outside the
    /// covering hull; active shields still form block walls toward enemies.
    /// Uses seam-correct toroidal XZ offsets (inline wrap) matching <c>ToroidalMapEcs</c>.
    /// <para>
    /// [TITAN-ORBIT] Merged into the same file as <see cref="DroneSwarmLogic"/> (both in the
    /// <c>TitanOrbit.Entities</c> assembly) so the server-only ECS combat system and the
    /// client-only Game presentation driver share one compiled copy of this math instead of two
    /// assemblies disagreeing about where "the real" positioning code lives.
    /// </para>
    /// </summary>
    public static class DroneSwarmPositioning
    {
        /// <summary>Legacy hit sphere radius for shield-body bullet intercept (world units).</summary>
        public const float DroneHitSphereRadius = 0.42f;

        /// <summary>One shield drone assigned to its closest in-range enemy.</summary>
        public struct ShieldAssignment
        {
            /// <summary>
            /// Target ship GhostOwner NetworkId, or a <see cref="DroneSwarmLogic.MakeDefensePadEnemyId"/>
            /// pad id. 0 = idle (rear scatter).
            /// </summary>
            public int EnemyNetworkId;

            /// <summary>Index of this shield among shields assigned to the same enemy.</summary>
            public int IndexOnEnemy;

            /// <summary>How many shields share this enemy this frame.</summary>
            public int CountOnEnemy;
        }

        /// <summary>Polar orbit slot + buzz offset for one drone this tick.</summary>
        public struct OrbitSlotTarget
        {
            /// <summary>Ship-local angle degrees (0 = forward, 180 = aft) before world conversion.</summary>
            public float AngleDeg;

            /// <summary>Ring radius from ship center on XZ.</summary>
            public float Radius;

            /// <summary>Planar buzz wobble added after polar placement.</summary>
            public Vector3 Buzz;
        }

        /// <summary>
        /// Builds ship basis on the FixedY plane (forward / right unit vectors).
        /// </summary>
        public static void GetShipBasis(
            Vector3 shipWorldPos,
            Quaternion shipWorldRot,
            out Vector3 shipPos,
            out Vector3 forward,
            out Vector3 right)
        {
            shipPos = shipWorldPos;
            shipPos.y = DroneSwarmLogic.FixedY;
            forward = shipWorldRot * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
                forward = Vector3.forward;
            forward.Normalize();
            right = Vector3.Cross(Vector3.up, forward);
            if (right.sqrMagnitude < 0.01f)
                right = Vector3.right;
            right.Normalize();
        }

        /// <summary>Shared fighter / mining / shield buzz wobble (legacy feel).</summary>
        public static Vector3 ComputeBuzzOffset(
            Vector3 axisA,
            Vector3 axisB,
            int slotIndex,
            float clusterOrdinal,
            float buzzAmplitude,
            float buzzSpeed,
            double timeSeconds,
            float buzzPhase)
        {
            float t = (float)timeSeconds;
            float buzz = buzzPhase + slotIndex * 0.37f;
            float wobble = buzz + clusterOrdinal * 0.61f + t * buzzSpeed * 0.45f;
            return axisA * (Mathf.Sin(t * buzzSpeed + buzz) * buzzAmplitude)
                + axisB * (Mathf.Cos(t * buzzSpeed * 1.17f + buzz * 1.3f) * buzzAmplitude * 0.55f)
                + axisA * (Mathf.Sin(wobble) * buzzAmplitude * 0.45f)
                + axisB * (Mathf.Cos(wobble * 1.13f) * buzzAmplitude * 0.35f);
        }

        /// <summary>World XZ polar → world position on FixedY.</summary>
        public static Vector3 WorldPolarToWorld(Vector3 shipPos, float worldAngleDeg, float radius)
        {
            float rad = worldAngleDeg * Mathf.Deg2Rad;
            Vector3 world = shipPos + new Vector3(Mathf.Sin(rad) * radius, 0f, Mathf.Cos(rad) * radius);
            world.y = DroneSwarmLogic.FixedY;
            return world;
        }

        /// <summary>Converts a world XZ offset into polar angle/radius.</summary>
        public static void WorldOffsetToWorldPolar(Vector3 offset, out float worldAngleDeg, out float radius)
        {
            offset.y = 0f;
            radius = offset.magnitude;
            worldAngleDeg = radius > 0.001f
                ? Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg
                : 0f;
        }

        /// <summary>Ship-local slot angle → world polar angle for lag catch-up.</summary>
        public static float ShipLocalSlotToWorldAngleDeg(
            Vector3 forward,
            Vector3 right,
            float localAngleDeg,
            float radius)
        {
            float rad = localAngleDeg * Mathf.Deg2Rad;
            Vector3 offset = forward * (Mathf.Cos(rad) * radius) + right * (Mathf.Sin(rad) * radius);
            WorldOffsetToWorldPolar(offset, out float worldAngleDeg, out _);
            return worldAngleDeg;
        }

        /// <summary>
        /// Covering-box half-extent along a ship-local polar ray (0° = forward, 180° = aft).
        /// Matches the authored hull box: axis-aligned faces, not a sphere.
        /// </summary>
        public static float GetHullRadiusAlongLocalAngle(
            float extentRight,
            float extentForward,
            float localAngleDeg)
        {
            float ex = Mathf.Max(0.05f, extentRight);
            float ez = Mathf.Max(0.05f, extentForward);
            float rad = localAngleDeg * Mathf.Deg2Rad;
            float dx = Mathf.Abs(Mathf.Sin(rad));
            float dz = Mathf.Abs(Mathf.Cos(rad));
            float tx = dx > 1e-5f ? ex / dx : float.PositiveInfinity;
            float tz = dz > 1e-5f ? ez / dz : float.PositiveInfinity;
            float r = Mathf.Min(tx, tz);
            if (float.IsInfinity(r) || r < 0.05f)
                return Mathf.Max(ex, ez);
            return r;
        }

        /// <summary>
        /// Stamps world-space covering-box half-extents and hull origin onto <paramref name="ctx"/>.
        /// Covering values are presentation-space (world = value × transformScale).
        /// Zero / missing covering falls back to <see cref="BodyCollisionMath.GetShipHullRadiusWorld"/>.
        /// </summary>
        public static void ApplyCoveringHullShape(
            ref SlotEvaluationContext ctx,
            float transformScale,
            float coveringExtentX,
            float coveringExtentZ,
            float coveringCenterX,
            float coveringCenterZ)
        {
            float scale = Mathf.Max(0.25f, transformScale);
            float extR = coveringExtentX * scale;
            float extF = coveringExtentZ * scale;
            if (extR < 0.05f || extF < 0.05f)
            {
                float fallback = BodyCollisionMath.GetShipHullRadiusWorld(transformScale);
                ctx.HullExtentRight = fallback;
                ctx.HullExtentForward = fallback;
                ctx.HullOrigin = ctx.ShipPos;
                return;
            }

            ctx.HullExtentRight = extR;
            ctx.HullExtentForward = extF;
            Vector3 local = new Vector3(coveringCenterX * scale, 0f, coveringCenterZ * scale);
            Vector3 origin = ctx.ShipPos + ctx.Right * local.x + ctx.Forward * local.z;
            origin.y = DroneSwarmLogic.FixedY;
            ctx.HullOrigin = origin;
        }

        /// <summary>
        /// Fighter / mining / idle-shield escort: random rear-hemisphere angle and
        /// standoff from the covering hull. Hash is deterministic per ship + slot.
        /// </summary>
        public static OrbitSlotTarget ComputeRearScatterOrbitSlot(
            Vector3 shipForward,
            Vector3 shipRight,
            int slotIndex,
            int shipNetworkId,
            StoreItemType droneType,
            float hullExtentRight,
            float hullExtentForward,
            float buzzAmplitude,
            float buzzSpeed,
            double timeSeconds,
            float buzzPhase)
        {
            DroneSwarmLogic.DeterministicUnit01Pair(
                shipNetworkId, slotIndex, droneType, out float uAngle, out float uRadius);

            float halfArc = Mathf.Clamp(DroneSwarmLogic.RearScatterHalfAngleDeg, 1f, 90f);
            float angleDeg = 180f + (uAngle * 2f - 1f) * halfArc;
            float hullAlong = GetHullRadiusAlongLocalAngle(
                hullExtentRight, hullExtentForward, angleDeg);
            float extra = DroneSwarmLogic.RearMinClearance
                + uRadius * DroneSwarmLogic.RearMaxClearance;
            float radius = hullAlong + extra;

            Vector3 behind = -shipForward;
            Vector3 buzz = ComputeBuzzOffset(
                shipRight, behind, slotIndex, uAngle,
                buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);
            return new OrbitSlotTarget { AngleDeg = angleDeg, Radius = radius, Buzz = buzz };
        }

        /// <summary>Shield idle: orbit port/starboard arcs with buzz.</summary>
        public static OrbitSlotTarget ComputeShieldSideOrbitSlot(
            Vector3 shipForward,
            Vector3 shipRight,
            int slotIndex,
            int sideOrdinal,
            int sideCount,
            float orbitRadius,
            float buzzAmplitude,
            float buzzSpeed,
            double timeSeconds,
            float buzzPhase)
        {
            int sideSign = (sideOrdinal % 2 == 0) ? 1 : -1;
            if (sideCount <= 1)
                sideSign = sideOrdinal == 0 ? 1 : -1;

            float sideCenter = sideSign * Mathf.PI * 0.5f;
            float wobble = buzzPhase + sideOrdinal * 0.85f + (float)timeSeconds * buzzSpeed * 0.45f;
            float sweep = Mathf.PI * 0.32f;
            float angleRad = sideCenter
                + Mathf.Sin(wobble) * sweep * 0.55f
                + Mathf.Cos(wobble * 0.73f + sideOrdinal) * sweep * 0.35f;

            Vector3 radialDir = shipForward * Mathf.Cos(angleRad) + shipRight * Mathf.Sin(angleRad);
            Vector3 tangent = Vector3.Cross(Vector3.up, radialDir);
            if (tangent.sqrMagnitude < 0.0001f)
                tangent = shipForward;
            tangent.Normalize();

            Vector3 buzz = ComputeBuzzOffset(
                tangent, radialDir, slotIndex, sideOrdinal,
                buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);
            return new OrbitSlotTarget
            {
                AngleDeg = angleRad * Mathf.Rad2Deg,
                Radius = orbitRadius,
                Buzz = buzz,
            };
        }

        /// <summary>Shield active: wall just outside hull toward an enemy (toroidal).</summary>
        public static OrbitSlotTarget ComputeShieldBlockOrbitSlot(
            Vector3 shipPos,
            Vector3 shipForward,
            Vector3 shipRight,
            Vector3 enemyPos,
            int slotIndex,
            int indexOnEnemy,
            int countOnEnemy,
            float blockDistanceFromShip,
            float formationSpacing,
            float buzzAmplitude,
            float buzzSpeed,
            double timeSeconds,
            float buzzPhase,
            float mapW,
            float mapH)
        {
            shipPos.y = DroneSwarmLogic.FixedY;
            enemyPos.y = DroneSwarmLogic.FixedY;

            // Toroidal shortest offset on XZ (inline — avoids assembly visibility flakiness on some Editor syncs).
            float dx = enemyPos.x - shipPos.x;
            float dz = enemyPos.z - shipPos.z;
            if (mapW > 1f)
            {
                while (dx > mapW * 0.5f) dx -= mapW;
                while (dx < -mapW * 0.5f) dx += mapW;
            }
            if (mapH > 1f)
            {
                while (dz > mapH * 0.5f) dz -= mapH;
                while (dz < -mapH * 0.5f) dz += mapH;
            }
            Vector3 toEnemy = new Vector3(dx, 0f, dz);
            float dist = toEnemy.magnitude;
            if (dist < 0.01f)
            {
                return ComputeShieldSideOrbitSlot(
                    shipForward, shipRight, slotIndex, indexOnEnemy, countOnEnemy,
                    blockDistanceFromShip, buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);
            }

            Vector3 lineDir = toEnemy / dist;
            Vector3 perp = Vector3.Cross(Vector3.up, lineDir);
            if (perp.sqrMagnitude < 0.01f)
                perp = shipRight;
            perp.Normalize();

            float lateral = (indexOnEnemy - (countOnEnemy - 1) * 0.5f) * formationSpacing;
            float along = Mathf.Min(blockDistanceFromShip, dist * 0.42f);
            Vector3 baseOffset = lineDir * along + perp * lateral;

            // Convert ship-relative offset into ship-local polar for lag catch-up.
            float localAngle = Mathf.Atan2(
                Vector3.Dot(baseOffset, shipRight),
                Vector3.Dot(baseOffset, shipForward)) * Mathf.Rad2Deg;
            float radius = baseOffset.magnitude;
            Vector3 buzz = ComputeBuzzOffset(
                perp, lineDir, slotIndex, indexOnEnemy,
                buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);
            return new OrbitSlotTarget { AngleDeg = localAngle, Radius = radius, Buzz = buzz };
        }

        /// <summary>Idle shield: flat face points outward from the ship center.</summary>
        public static Quaternion ComputeShieldFaceOutwardRotation(
            Vector3 shipWorldPos,
            Vector3 droneWorldPos,
            Vector3 flatFaceRestNormal)
        {
            Vector3 outward = droneWorldPos - shipWorldPos;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.0001f)
                return Quaternion.identity;
            Vector3 rest = flatFaceRestNormal.sqrMagnitude > 0.0001f
                ? flatFaceRestNormal.normalized
                : Vector3.up;
            return Quaternion.FromToRotation(rest, outward.normalized);
        }

        /// <summary>Active shield: flat face points at the threat (toroidal shortest).</summary>
        public static Quaternion ComputeShieldFaceEnemyRotation(
            Vector3 droneWorldPos,
            Vector3 enemyWorldPos,
            Vector3 flatFaceRestNormal,
            float mapW,
            float mapH)
        {
            float dx = enemyWorldPos.x - droneWorldPos.x;
            float dz = enemyWorldPos.z - droneWorldPos.z;
            if (mapW > 1f)
            {
                while (dx > mapW * 0.5f) dx -= mapW;
                while (dx < -mapW * 0.5f) dx += mapW;
            }
            if (mapH > 1f)
            {
                while (dz > mapH * 0.5f) dz -= mapH;
                while (dz < -mapH * 0.5f) dz += mapH;
            }
            Vector3 toEnemy = new Vector3(dx, 0f, dz);
            if (toEnemy.sqrMagnitude < 0.0001f)
                return Quaternion.identity;
            Vector3 rest = flatFaceRestNormal.sqrMagnitude > 0.0001f
                ? flatFaceRestNormal.normalized
                : Vector3.up;
            return Quaternion.FromToRotation(rest, toEnemy.normalized);
        }

        /// <summary>
        /// Each shield picks the closest in-range enemy from <paramref name="shieldFromPos"/>
        /// (idle rear pose). Shields that choose the same enemy still share a lateral wall.
        /// </summary>
        /// <param name="shieldSlotIndices">Equipment slots that hold living shield drones.</param>
        /// <param name="shieldFromPos">Idle planar pose per slot (same order as the slot list).</param>
        /// <param name="enemyNetworkIds">Candidate enemy / pad ids.</param>
        /// <param name="enemyPosById">Planar positions keyed by those ids.</param>
        /// <param name="mapW">Toroidal width from MapStateSingleton.</param>
        /// <param name="mapH">Toroidal height from MapStateSingleton.</param>
        /// <param name="assignmentsOut">Cleared then filled keyed by equipment slot index.</param>
        public static void BuildShieldAssignments(
            IReadOnlyList<int> shieldSlotIndices,
            IReadOnlyList<Vector3> shieldFromPos,
            List<int> enemyNetworkIds,
            Dictionary<int, Vector3> enemyPosById,
            float mapW,
            float mapH,
            Dictionary<int, ShieldAssignment> assignmentsOut)
        {
            assignmentsOut.Clear();
            if (shieldSlotIndices == null || shieldSlotIndices.Count == 0)
                return;
            if (enemyNetworkIds == null || enemyNetworkIds.Count == 0 ||
                shieldFromPos == null || enemyPosById == null)
                return;

            float hullMaxSq = DroneSwarmLogic.ShieldEngageRange * DroneSwarmLogic.ShieldEngageRange;
            float padMaxSq = DroneSwarmLogic.DefensePadEngageRange * DroneSwarmLogic.DefensePadEngageRange;
            s_CountPerEnemy.Clear();

            int n = Mathf.Min(shieldSlotIndices.Count, shieldFromPos.Count);
            for (int i = 0; i < n; i++)
            {
                int slot = shieldSlotIndices[i];
                Vector3 from = shieldFromPos[i];
                int bestId = 0;
                float bestSq = float.MaxValue;
                for (int e = 0; e < enemyNetworkIds.Count; e++)
                {
                    int enemyId = enemyNetworkIds[e];
                    if (enemyId == 0 || !enemyPosById.TryGetValue(enemyId, out Vector3 ep))
                        continue;

                    float dist = DroneSwarmLogic.ToroidalDistanceXZ(
                        from.x, from.z, ep.x, ep.z, mapW, mapH);
                    float sq = dist * dist;
                    float maxSq = DroneSwarmLogic.IsDefensePadEnemyId(enemyId) ? padMaxSq : hullMaxSq;
                    if (sq >= maxSq || sq >= bestSq)
                        continue;
                    bestSq = sq;
                    bestId = enemyId;
                }

                if (bestId == 0)
                    continue;

                if (!s_CountPerEnemy.ContainsKey(bestId))
                    s_CountPerEnemy[bestId] = 0;
                int indexOnEnemy = s_CountPerEnemy[bestId];
                s_CountPerEnemy[bestId] = indexOnEnemy + 1;
                assignmentsOut[slot] = new ShieldAssignment
                {
                    EnemyNetworkId = bestId,
                    IndexOnEnemy = indexOnEnemy,
                    CountOnEnemy = 0,
                };
            }

            s_AssignKeys.Clear();
            foreach (var key in assignmentsOut.Keys)
                s_AssignKeys.Add(key);
            for (int i = 0; i < s_AssignKeys.Count; i++)
            {
                int slot = s_AssignKeys[i];
                ShieldAssignment a = assignmentsOut[slot];
                if (s_CountPerEnemy.TryGetValue(a.EnemyNetworkId, out int total))
                {
                    a.CountOnEnemy = total;
                    assignmentsOut[slot] = a;
                }
            }
        }

        static readonly Dictionary<int, int> s_CountPerEnemy = new Dictionary<int, int>(8);
        static readonly List<int> s_AssignKeys = new List<int>(8);

        /// <summary>
        /// Inputs for one drone slot evaluation. Caller fills ship basis, cluster ordinals,
        /// and optional shield enemy target — no ECS types so Game + ECS can share this.
        /// </summary>
        public struct SlotEvaluationContext
        {
            /// <summary>Ship center on FixedY.</summary>
            public Vector3 ShipPos;

            /// <summary>Flattened forward (unit).</summary>
            public Vector3 Forward;

            /// <summary>Flattened right (unit).</summary>
            public Vector3 Right;

            /// <summary>Hull-based escort ring radius.</summary>
            public float OrbitRadius;

            /// <summary>Shared ServerTick seconds (buzz phase).</summary>
            public double TimeSeconds;

            /// <summary>GhostOwner.NetworkId for deterministic buzz seed.</summary>
            public int ShipNetworkId;

            /// <summary>Map width for toroidal shield block.</summary>
            public float MapW;

            /// <summary>Map height for toroidal shield block.</summary>
            public float MapH;

            /// <summary>Index among fighter+mining drones (0-based).</summary>
            public int RearOrdinal;

            /// <summary>Count of fighter+mining drones (≥ 1 when used).</summary>
            public int RearCount;

            /// <summary>Index among shield drones (0-based).</summary>
            public int ShieldOrdinal;

            /// <summary>Count of shield drones (≥ 1 when used).</summary>
            public int ShieldCount;

            /// <summary>True when this shield has an assigned in-range enemy.</summary>
            public bool HasShieldTarget;

            /// <summary>Assigned enemy planar position (FixedY).</summary>
            public Vector3 EnemyPos;

            /// <summary>Index of this shield on the assigned enemy wall.</summary>
            public int IndexOnEnemy;

            /// <summary>How many shields share that enemy.</summary>
            public int CountOnEnemy;

            /// <summary>World-space covering-box half-extent along ship right (X).</summary>
            public float HullExtentRight;

            /// <summary>World-space covering-box half-extent along ship forward (Z).</summary>
            public float HullExtentForward;

            /// <summary>
            /// Covering-box center on FixedY. Rear scatter is measured from here so
            /// drones sit outside the real hull, not a sphere at the transform origin.
            /// </summary>
            public Vector3 HullOrigin;
        }

        /// <summary>
        /// Planar world pose for one drone slot (combat + presentation XZ).
        /// Client may add PresentationLiftY on local Y after converting to hub-local space.
        /// </summary>
        public struct EvaluatedSlotPose
        {
            /// <summary>World XZ on FixedY including buzz (authoritative muzzle / hit sphere).</summary>
            public Vector3 WorldPosition;

            /// <summary>Ship-local polar angle degrees.</summary>
            public float AngleDeg;

            /// <summary>Polar radius from ship center.</summary>
            public float Radius;

            /// <summary>Buzz offset already baked into WorldPosition.</summary>
            public Vector3 Buzz;
        }

        /// <summary>
        /// Single entry point for fighter / mining / shield planar pose.
        /// Combat fire origins and client meshes must both call this so muzzles match meshes.
        /// </summary>
        /// <param name="droneType">Equipment type (fighter / mining / shield).</param>
        /// <param name="slotIndex">Equipment buffer index (buzz seed).</param>
        /// <param name="ctx">Ship basis + cluster ordinals + optional shield target.</param>
        /// <returns>Planar world pose on FixedY.</returns>
        public static EvaluatedSlotPose EvaluateSlotPose(
            StoreItemType droneType,
            int slotIndex,
            in SlotEvaluationContext ctx)
        {
            float buzzPhase = DroneSwarmLogic.DeterministicBasePhaseRad(
                ctx.ShipNetworkId, slotIndex, droneType);

            OrbitSlotTarget slot;
            switch (droneType)
            {
                case StoreItemType.FighterDrone:
                case StoreItemType.MiningDrone:
                    slot = ComputeRearScatterOrbitSlot(
                        ctx.Forward, ctx.Right, slotIndex,
                        ctx.ShipNetworkId, droneType,
                        ctx.HullExtentRight, ctx.HullExtentForward,
                        DroneSwarmLogic.BuzzAmplitude, DroneSwarmLogic.BuzzSpeed,
                        ctx.TimeSeconds, buzzPhase);
                    break;

                case StoreItemType.ShieldDrone:
                    if (ctx.HasShieldTarget)
                    {
                        slot = ComputeShieldBlockOrbitSlot(
                            ctx.ShipPos, ctx.Forward, ctx.Right, ctx.EnemyPos,
                            slotIndex, ctx.IndexOnEnemy, Mathf.Max(1, ctx.CountOnEnemy),
                            ctx.OrbitRadius, DroneSwarmLogic.ShieldFormationSpacing,
                            DroneSwarmLogic.BuzzAmplitude, DroneSwarmLogic.BuzzSpeed,
                            ctx.TimeSeconds, buzzPhase, ctx.MapW, ctx.MapH);
                    }
                    else
                    {
                        slot = ComputeRearScatterOrbitSlot(
                            ctx.Forward, ctx.Right, slotIndex,
                            ctx.ShipNetworkId, droneType,
                            ctx.HullExtentRight, ctx.HullExtentForward,
                            DroneSwarmLogic.BuzzAmplitude, DroneSwarmLogic.BuzzSpeed,
                            ctx.TimeSeconds, buzzPhase);
                    }
                    break;

                default:
                    slot = new OrbitSlotTarget
                    {
                        AngleDeg = 180f,
                        Radius = ctx.OrbitRadius,
                        Buzz = Vector3.zero,
                    };
                    break;
            }

            Vector3 polarOrigin = ctx.HullExtentRight > 0.01f ? ctx.HullOrigin : ctx.ShipPos;
            polarOrigin.y = DroneSwarmLogic.FixedY;
            float worldAngle = ShipLocalSlotToWorldAngleDeg(
                ctx.Forward, ctx.Right, slot.AngleDeg, slot.Radius);
            Vector3 world = WorldPolarToWorld(polarOrigin, worldAngle, slot.Radius);
            world += slot.Buzz;
            world.y = DroneSwarmLogic.FixedY;

            return new EvaluatedSlotPose
            {
                WorldPosition = world,
                AngleDeg = slot.AngleDeg,
                Radius = slot.Radius,
                Buzz = slot.Buzz,
            };
        }

        /// <summary>Hull-based escort radius (legacy moon-dock style padding).</summary>
        public static float GetDroneOrbitRadiusFromHull(float hullRadiusWorld)
        {
            float mul = Mathf.Max(0.1f, DroneSwarmLogic.OrbitRadiusMultiplier);
            return Mathf.Max(
                DroneSwarmLogic.DefaultOrbitRadius,
                (hullRadiusWorld + DroneSwarmLogic.MarginBeyondHull) * mul);
        }
    }
}
