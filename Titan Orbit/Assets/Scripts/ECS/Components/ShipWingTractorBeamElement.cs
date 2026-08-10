using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] One wing-mounted gem tractor beam on a ship — local position and per-level stat
    /// scaling. Stored in a DynamicBuffer; multiple wings can collect gems in parallel. Baked from
    /// <see cref="Authoring.ShipWingTractorBeamAuthoring"/> children in StarshipGhostAuthoring.
    /// </summary>
    public struct ShipWingTractorBeamElement : IBufferElementData
    {
        // --- Type members ---
        /// <summary>
        /// [UNITY] Hull-root local offset in <b>unscaled prefab space</b> (not immediate-parent
        /// <c>localPosition</c>, and not yet multiplied by <see cref="BodyCollisionMath.ShipPresentationScale"/>).
        /// <see cref="ShipWingTractorBeamPose.GetWorldPosition"/> applies presentation scale so beams
        /// line up with the hybrid ship mesh.
        /// </summary>
        public float3 LocalPosition;

        /// <summary>[TITAN-ORBIT] Base tractor search radius at ship level 1.</summary>
        public float TractorBeamDistance;

        /// <summary>[TITAN-ORBIT] Additional search radius per ship level above 1.</summary>
        public float TractorBeamDistancePerLevel;

        /// <summary>[TITAN-ORBIT] Base gem attraction speed at ship level 1.</summary>
        public float TractorBeamPower;

        /// <summary>[TITAN-ORBIT] Additional attraction speed per ship level above 1.</summary>
        public float TractorBeamPowerPerLevel;

        /// <summary>[TITAN-ORBIT] Base max gems this wing can hold at ship level 1.</summary>
        public float MaxGems;

        /// <summary>[TITAN-ORBIT] Additional gem capacity per ship level above 1.</summary>
        public float MaxGemsPerLevel;

        /// <summary>
        /// [STANDARD] Converts buffer element to simulation params struct for GemTractorBeamMath.
        /// </summary>
        /// <returns>Blittable params struct for shared tractor beam math.</returns>
        public ShipWingTractorBeamParams ToParams() => new ShipWingTractorBeamParams
        {
            LocalPosition = LocalPosition,
            TractorBeamDistance = TractorBeamDistance,
            TractorBeamDistancePerLevel = TractorBeamDistancePerLevel,
            TractorBeamPower = TractorBeamPower,
            TractorBeamPowerPerLevel = TractorBeamPowerPerLevel,
            MaxGems = MaxGems,
            MaxGemsPerLevel = MaxGemsPerLevel,
        };
    }

    /// <summary>
    /// [ECS/DOTS] Helper to resolve wing world positions and tractor beam reach/power from ship
    /// transform + level. Used by <see cref="GemTractorBeamSystem"/> and client VFX trackers.
    /// </summary>
    public static class ShipWingTractorBeamPose
    {
        /// <summary>
        /// [ECS/DOTS] Resolves wing attachment point in world space from ship hull transform.
        /// </summary>
        /// <param name="shipTransform">Ship hull LocalTransform (position + rotation).</param>
        /// <param name="wing">Baked wing element with unscaled hull-root local offset.</param>
        /// <returns>World-space wing position on the XZ plane (presentation-scaled).</returns>
        public static float3 GetWorldPosition(in LocalTransform shipTransform, in ShipWingTractorBeamElement wing)
        {
            // --- Prefab-local → presentation world ---
            // [TITAN-ORBIT] Chassis prefabs are authored large; hybrid proxies multiply by
            // ShipPresentationScale (~0.155). Wing LocalPosition stays unscaled (same as bake /
            // InverseTransformPoint). Without this multiply, multi-wing upgrade ships draw beams
            // far outside the visible hull while gems still pull toward those inflated origins.
            float ecsScale = math.max(0.25f, shipTransform.Scale);
            float3 presentationLocal = wing.LocalPosition * (BodyCollisionMath.ShipPresentationScale * ecsScale);
            return GemTractorBeamMath.ResolveWingWorldPosition(
                shipTransform.Position, shipTransform.Rotation, presentationLocal);
        }

        /// <summary>
        /// [TITAN-ORBIT] Computes effective search radius and attraction speed for one wing at the
        /// given ship level, with orbit-zone bonus when applicable, then applies global
        /// <see cref="TractorBeamSettings"/> range/power multipliers so server and client
        /// share one resolution path.
        /// </summary>
        /// <param name="wing">Baked wing stats.</param>
        /// <param name="shipLevel">Current ship upgrade level.</param>
        /// <param name="inOrbitZone">True when ship is inside a friendly orbit ring (range bonus).</param>
        /// <param name="searchRadius">Output effective search radius (after settings multiplier).</param>
        /// <param name="attractionSpeed">Output gameplay pull speed toward this wing (after multiplier).</param>
        public static void GetTractorParams(
            in ShipWingTractorBeamElement wing,
            int shipLevel,
            bool inOrbitZone,
            out float searchRadius,
            out float attractionSpeed)
        {
            // --- Per-wing authored stats (level + orbit) ---
            GemTractorBeamMath.GetWingTractorParams(
                wing.ToParams(), shipLevel, inOrbitZone, out searchRadius, out attractionSpeed);

            // --- Designer global multipliers (TractorBeamSettings asset) ---
            // [TITAN-ORBIT] Applied here so every GetTractorParams caller (server pull, client VFX)
            // stays matched without sprinkling ApplyReachAndPower at each call site.
            TractorBeamSettingsCache.ApplyReachAndPower(ref searchRadius, ref attractionSpeed);
        }
    }
}

