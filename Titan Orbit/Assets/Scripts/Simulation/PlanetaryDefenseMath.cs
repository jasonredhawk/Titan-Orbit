using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Pure placement / range math for planetary defense slots.
    /// Angles match minimap defense dots (<c>MinimapController.LayoutLevelDots</c>): even spacing
    /// around the planet with index 0 at “north” (+Z on the XZ plane / UI +Y).
    /// <para>
    /// [TITAN-ORBIT] Slot radius is halfway between the planet surface and the ship/moon
    /// orbit-ring centerline. Combat engage range is measured from the turret pad as
    /// (pad→orbit gap) × multiplier, where the multiplier comes from
    /// <c>PlanetaryDefenseConfig</c> Level 1→6 fire-distance ranges (default 2× → 3×).
    /// Level 7 (crown) is gated: planet at <see cref="PlanetEconomyMath.MaxPlanetLevel"/>
    /// and the gem-moon reservoir full.
    /// </para>
    /// </summary>
    public static class PlanetaryDefenseMath
    {
        /// <summary>
        /// Authoritative combat / deposit height — Titan Orbit is XZ gameplay.
        /// [TITAN-ORBIT] Same floor as drone combat (<c>DroneSwarmLogic.FixedY</c>).
        /// </summary>
        public const float FixedY = 0f;

        /// <summary>
        /// Crown turret level (Solfeggio 963). Not tied to planet level — requires
        /// <see cref="IsCrownTurretUnlocked"/>.
        /// </summary>
        public const int CrownTurretLevel = 7;

        /// <summary>
        /// Epsilon when comparing moon gem fill to capacity (float safety).
        /// </summary>
        public const float MoonGemFullEpsilon = 0.001f;

        /// <summary>
        /// Even ring angle for slot <paramref name="slotIndex"/> of <paramref name="slotCount"/>.
        /// Matches minimap: <c>π/2 + 2π × i / count</c> so index 0 is straight “up” / +Z.
        /// </summary>
        /// <param name="slotIndex">0-based slot index.</param>
        /// <param name="slotCount">Number of slots (= planet level when owned).</param>
        /// <returns>Radians on the XZ plane for <c>cos/sin</c>.</returns>
        public static float GetEvenRingSlotAngle(int slotIndex, int slotCount)
        {
            int count = math.max(1, slotCount);
            int i = math.clamp(slotIndex, 0, count - 1);
            return (math.PI * 0.5f) + (math.PI * 2f * i) / count;
        }

        /// <summary>
        /// World-space radius of the slot ring: midpoint between planet surface and
        /// ship-orbit ring centerline (same idea as minimap level dots between fill and orbit).
        /// </summary>
        /// <param name="planetSize">Planet <c>LocalTransform.Scale</c>.</param>
        /// <param name="planetLevel">Planet level (orbit ring currently ignores level; kept for API).</param>
        public static float GetSlotRingRadiusWorld(float planetSize, int planetLevel)
        {
            // --- Surface ---
            // [PHYSICS] Hull radius from baked mesh base × scale (same as planet collision).
            float surfaceWorld = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetSize);

            // --- Orbit ring centerline ---
            PlanetOrbitMath.GetRingRadiiWorld(
                planetSize, planetLevel, out _, out _, out float orbitCenterWorld);

            // Halfway between surface and orbit path.
            float mid = math.lerp(surfaceWorld, orbitCenterWorld, 0.5f);

            // Safety: stay strictly outside the surface and inside the orbit center.
            float minR = surfaceWorld + 0.05f;
            float maxR = math.max(minR, orbitCenterWorld - 0.05f);
            return math.clamp(mid, minR, maxR);
        }

        /// <summary>
        /// World position of a defense slot relative to a planet center (no toroidal unwrap).
        /// Angle matches minimap level dots; radius is surface↔orbit midpoint.
        /// </summary>
        public static float3 GetSlotWorldPosition(
            float3 planetPosition,
            float planetSize,
            int planetLevel,
            int slotIndex,
            int slotCount)
        {
            float angle = GetEvenRingSlotAngle(slotIndex, slotCount);
            float radius = GetSlotRingRadiusWorld(planetSize, planetLevel);
            return new float3(
                planetPosition.x + math.cos(angle) * radius,
                FixedY,
                planetPosition.z + math.sin(angle) * radius);
        }

        /// <summary>
        /// Slot world position on the map tile nearest <paramref name="nearPosition"/> (toroidal).
        /// </summary>
        public static float3 GetSlotWorldPositionNear(
            float3 nearPosition,
            float3 planetPosition,
            float planetSize,
            int planetLevel,
            int slotIndex,
            int slotCount,
            float mapW,
            float mapH)
        {
            float3 planetNear = nearPosition +
                ToroidalMapEcs.ShortestOffsetXZ(nearPosition, planetPosition, mapW, mapH);
            planetNear.y = FixedY;
            float3 slot = GetSlotWorldPosition(
                planetNear, planetSize, planetLevel, slotIndex, slotCount);
            slot.y = FixedY;
            return slot;
        }

        /// <summary>
        /// Radial distance from the defense pad out to the ship orbit-ring centerline.
        /// Engage range = this gap × the turret's engage-range multiplier.
        /// </summary>
        public static float GetPadToOrbitGap(float planetSize, int planetLevel)
        {
            float padRadius = GetSlotRingRadiusWorld(planetSize, planetLevel);
            PlanetOrbitMath.GetRingRadiiWorld(
                planetSize, planetLevel, out _, out _, out float orbitCenterWorld);
            return math.max(0.05f, orbitCenterWorld - padRadius);
        }

        /// <summary>
        /// Max fire range measured from the turret pad (not planet center).
        /// Equals (orbit-ring centerline − pad radius) × <paramref name="padToOrbitMultiplier"/>.
        /// Default recipe uses 2 at Lv1 and 3 at Lv6.
        /// </summary>
        /// <param name="planetSize">Planet <c>LocalTransform.Scale</c>.</param>
        /// <param name="planetLevel">Planet level (orbit / pad radii).</param>
        /// <param name="padToOrbitMultiplier">
        /// Absolute multiple of the pad→orbit gap (2 = ×2, 3 = ×3). Not “beyond fraction”.
        /// </param>
        public static float GetEngageRangeFromTurret(
            float planetSize,
            int planetLevel,
            float padToOrbitMultiplier)
        {
            float gap = GetPadToOrbitGap(planetSize, planetLevel);
            float mul = math.max(0.05f, padToOrbitMultiplier);
            return gap * mul;
        }

        /// <summary>
        /// [LEGACY] Older call sites passed “beyond fraction” where 1.0 meant ×2 total.
        /// Converts to absolute multiplier then uses <see cref="GetEngageRangeFromTurret"/>.
        /// </summary>
        public static float GetEngageRangeFromPlanetCenter(
            float planetSize,
            int planetLevel,
            float rangeBeyondOrbitOuter)
        {
            float absoluteMul = 1f + math.max(0f, rangeBeyondOrbitOuter);
            return GetEngageRangeFromTurret(planetSize, planetLevel, absoluteMul);
        }

        /// <summary>
        /// How many defense slots an owned planet should expose (= clamped planet level).
        /// Neutral / unowned planets expose zero slots.
        /// </summary>
        public static int GetSlotCountForOwnedPlanet(int planetLevel)
        {
            return math.clamp(planetLevel, 1, PlanetEconomyMath.MaxPlanetLevel);
        }

        /// <summary>
        /// Max turret level from planet level alone (1..6). Does <b>not</b> include crown Lv7 —
        /// use the overload with moon gem fill for that.
        /// </summary>
        public static int GetMaxTurretLevelForPlanet(int planetLevel)
        {
            return math.clamp(planetLevel, 1, PlanetEconomyMath.MaxPlanetLevel);
        }

        /// <summary>
        /// True when crown Lv7 may be built: planet at max level and moon gem pool is full.
        /// </summary>
        /// <param name="planetLevel">Owned planet level.</param>
        /// <param name="currentMoonGems">Moon reservoir current (server or ghosted).</param>
        /// <param name="maxMoonGems">Moon reservoir capacity.</param>
        public static bool IsCrownTurretUnlocked(
            int planetLevel,
            float currentMoonGems,
            float maxMoonGems)
        {
            // --- Planet must be fully leveled ---
            if (planetLevel < PlanetEconomyMath.MaxPlanetLevel)
                return false;

            // --- Moon gem pool must be at capacity (healed / undrained) ---
            if (maxMoonGems <= MoonGemFullEpsilon)
                return false;

            return currentMoonGems >= maxMoonGems - MoonGemFullEpsilon;
        }

        /// <summary>
        /// Max turret level including crown Lv7 when the moon gate passes.
        /// </summary>
        public static int GetMaxTurretLevelForPlanet(
            int planetLevel,
            float currentMoonGems,
            float maxMoonGems)
        {
            int baseMax = GetMaxTurretLevelForPlanet(planetLevel);
            if (IsCrownTurretUnlocked(planetLevel, currentMoonGems, maxMoonGems))
                return CrownTurretLevel;
            return baseMax;
        }
    }
}
