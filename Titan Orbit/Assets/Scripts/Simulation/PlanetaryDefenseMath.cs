using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Pure placement math for planetary defense slots.
    /// Angles match minimap defense dots (<c>MinimapController.LayoutLevelDots</c>): even spacing
    /// around the planet with index 0 at “north” (+Z on the XZ plane / UI +Y).
    /// <para>
    /// [TITAN-ORBIT] Slot radius is halfway between the planet surface and the ship/moon
    /// orbit-ring centerline. Combat engage range is an absolute world distance from the
    /// turret pad (authored on <c>PlanetaryDefenseConfig</c> — default 20 at Lv1, +4 per level above 1),
    /// independent of planet size. Level 7 (crown) is gated: planet at
    /// <see cref="PlanetEconomyMath.MaxPlanetLevel"/> and the gem-moon reservoir full.
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
            return planetPosition + SphericalMapEcs.OrbitOffsetWorld(
                planetPosition, math.cos(angle) * radius, math.sin(angle) * radius);
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
            _ = nearPosition;
            _ = mapW;
            _ = mapH;
            return GetSlotWorldPosition(planetPosition, planetSize, planetLevel, slotIndex, slotCount);
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
