using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Magnet-steered people transport motion ported from legacy PeopleTransportProjectile.
    /// Server people-transport systems and client <c>PeopleTransportVisualSyncSystem</c> share these
    /// constants and steering math so cosmetic flight matches delivery timing. Uses toroidal helpers
    /// from <see cref="ToroidalMapEcs"/> for wrap-aware paths.
    /// </summary>
    public static class PeopleTransportMath
    {
        /// <summary>
        /// Base hop time (seconds). Effective travel =
        /// <c>Target × DurationMultiplier / SpeedBonus</c> → 3 × 5 / 2.4 = 6.25s
        /// (the slower ECS feel before the 2.75s “visibility” speedup).
        /// </summary>
        public const float TargetVisualTravelSeconds = 3f;

        /// <summary>Stretches hop time — paired with <see cref="VisualTravelSpeedBonus"/>.</summary>
        public const float VisualTravelDurationMultiplier = 5f;

        /// <summary>Shortens hop time — paired with <see cref="VisualTravelDurationMultiplier"/>.</summary>
        public const float VisualTravelSpeedBonus = 2.4f;

        /// <summary>Outward nudge from planet surface when spawning a load transport (world units).</summary>
        public const float SurfaceSpawnOutwardNudge = 0.45f;

        /// <summary>Load flights cruise a bit faster than unload (toward the moving ship).</summary>
        public const float LoadMagnetSpeedMultiplier = 1.15f;
        public const float MagnetCloseRangeWorld = 5f;
        /// <summary>Mild end-of-hop speed-up (legacy 18/11 was too snappy with the slow cruise).</summary>
        public const float MagnetCloseRangeSpeedRatio = 1.25f;
        public const float ShipLoadCollectPadding = 0.22f;
        public const float ShipLoadCollectMinDistance = 0.4f;
        public const float ShipHullMagnetInset = 0.12f;
        public const float LoadDeliveryMinSeconds = 0.22f;
        public const float LoadDeliveryMinSpawnDistance = 0.35f;
        public const float UnloadDeliveryMinSeconds = 0.18f;
        public const float UnloadDeliveryMinTravelDistance = 0.3f;
        public const float TransportRadius = 0.25f;
        /// <summary>HP per person in sphere (legacy PeopleTransportProjectile.HealthPerShipLevel).</summary>
        public const float HealthPerPeopleAmount = 4f;
        /// <summary>Legacy PeopleTransportProjectile amount → scale curve range.</summary>
        public const float PeopleAmountScaleMin = 1f;
        public const float PeopleAmountScaleMax = 12f;
        public const float VisualScaleMinMultiplier = 0.9f;
        /// <summary>
        /// Max scale at <see cref="PeopleAmountScaleMax"/>. Raised above the old 2.1 so packed
        /// +N spheres (e.g. L6 unload = one +6) read clearly larger than a lone +1.
        /// </summary>
        public const float VisualScaleMaxMultiplier = 2.7f;

        public static float EffectiveVisualTravelSeconds =>
            TargetVisualTravelSeconds * VisualTravelDurationMultiplier / VisualTravelSpeedBonus;

        public static float ComputeCruiseSpeed(float3 fromPos, float3 toPos, bool isLoad, float mapW, float mapH)
        {
            // --- Compute value ---
            float travelDist = ToroidalMapEcs.ToroidalDistance(fromPos, toPos, mapW, mapH);
            float cruiseSpeed = math.max(0.08f, travelDist / EffectiveVisualTravelSeconds);
            if (isLoad)
                cruiseSpeed *= LoadMagnetSpeedMultiplier;
            return cruiseSpeed;
        }

        public static float3 SteerMagnetVelocity(
            float3 myPos,
            float3 targetPos,
            float3 currentVel,
            float dt,
            float cruiseSpeed,
            float mapW,
            float mapH)
        {
            float3 toTarget = ToroidalMapEcs.ToroidalDirection(myPos, targetPos, mapW, mapH);
            float dist = ToroidalMapEcs.ToroidalDistance(myPos, targetPos, mapW, mapH);
            float closeSpeed = cruiseSpeed * MagnetCloseRangeSpeedRatio;
            float speed = dist <= MagnetCloseRangeWorld ? closeSpeed : cruiseSpeed;
            float3 targetVel = toTarget * speed;
            return math.lerp(currentVel, targetVel, math.saturate(speed * dt * 4f));
        }

        /// <summary>
        /// Multiplier on the prefab's authored localScale from carried people amount.
        /// Dispatch packs each load/unload batch into one sphere: load Amount =
        /// <c>min(ship, planet)</c>, unload Amount = ship level. Higher Amount → larger visual
        /// (e.g. +1 ≈ 0.9×, +6 ≈ 1.7×, +12 ≈ 2.7× on the prefab's 0.25 base scale).
        /// </summary>
        public static float GetVisualScaleMultiplier(float peopleAmount)
        {
            float clamped = math.clamp(math.max(0.001f, peopleAmount), PeopleAmountScaleMin, PeopleAmountScaleMax);
            float normalized = math.unlerp(PeopleAmountScaleMin, PeopleAmountScaleMax, clamped);
            return math.lerp(VisualScaleMinMultiplier, VisualScaleMaxMultiplier, normalized);
        }

        public static float ComputeMaxHealth(float peopleAmount)
        {
            float amount = math.max(0.001f, peopleAmount);
            return math.max(HealthPerPeopleAmount, amount * HealthPerPeopleAmount);
        }

        public static float GetBulletHitRadius(float transformScale)
        {
            return math.max(TransportRadius, math.max(0.001f, transformScale));
        }

        public static float3 GetPlanetSurfaceToward(float3 planetCenter, float planetSize, float3 fromWorldPos, float mapW, float mapH)
        {
            // --- Compute value ---
            float3 toCore = ToroidalMapEcs.ToroidalDirection(fromWorldPos, planetCenter, mapW, mapH);
            float surfaceWorld = math.max(0.25f, planetSize) * 0.5f;
            float3 surface = planetCenter - toCore * surfaceWorld;
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapW, mapH);
            return SphericalMapEcs.ProjectToSphere(surface, radius);
        }

        public static float3 GetPlanetSurfaceSpawnToward(float3 planetCenter, float planetSize, float3 towardWorldPos, float mapW, float mapH)
        {
            // --- Compute value ---
            float3 surface = GetPlanetSurfaceToward(planetCenter, planetSize, towardWorldPos, mapW, mapH);
            float3 outward = ToroidalMapEcs.ToroidalDirection(planetCenter, surface, mapW, mapH);
            float nudge = math.max(SurfaceSpawnOutwardNudge, planetSize * 0.045f);
            surface += outward * nudge;
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapW, mapH);
            return SphericalMapEcs.ProjectToSphere(surface, radius);
        }

        public static float3 GetShipMagnetTarget(float3 shipCenter, float shipRadius, float3 fromWorldPos, float mapW, float mapH)
        {
            // --- Compute value ---
            float3 toCenter = ToroidalMapEcs.ToroidalDirection(fromWorldPos, shipCenter, mapW, mapH);
            float hullRadius = math.max(0.2f, shipRadius);
            float inset = math.clamp(hullRadius * ShipHullMagnetInset, 0.05f, 0.45f);
            float3 hullPoint = shipCenter - toCenter * math.max(0.2f, hullRadius - inset);
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapW, mapH);
            return SphericalMapEcs.ProjectToSphere(hullPoint, radius);
        }

        /// <summary>
        /// World hull radius from ECS <c>LocalTransform.Scale</c> — matches ship presentation size.
        /// [TITAN-ORBIT] Do not use Scale raw as radius (that spawned unloads ~1 unit out and looked
        /// like they left from the nose when the ship faced the planet).
        /// </summary>
        public static float GetShipHullRadius(float shipTransformScale) =>
            BodyCollisionMath.GetShipHullRadiusWorld(shipTransformScale);

        /// <summary>
        /// Unload spawn on the planet-facing flank of the ship (toroidal), independent of ship yaw.
        /// </summary>
        /// <param name="shipCenter">Ship logical position.</param>
        /// <param name="shipRadius">World hull radius from <see cref="GetShipHullRadius"/>.</param>
        /// <param name="planetCenter">Planet center — direction ship→planet defines the flank.</param>
        public static float3 GetShipUnloadSpawnToward(
            float3 shipCenter,
            float shipRadius,
            float3 planetCenter,
            float mapW,
            float mapH)
        {
            // --- Planet-facing flank (ignore ship rotation / nose) ---
            // [TITAN-ORBIT] ToroidalDirection(ship, planet) is always the side closest to the planet.
            float3 towardPlanet = ToroidalMapEcs.ToroidalDirection(shipCenter, planetCenter, mapW, mapH);
            float hullRadius = math.max(BodyCollisionMath.MinShipHullRadiusWorld, shipRadius);
            // Clear the visual hull so the float reads as leaving the planetward side, not the cockpit.
            float nudge = math.max(0.2f, hullRadius * 0.55f);
            float3 spawn = shipCenter + towardPlanet * (hullRadius + nudge);
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapW, mapH);
            return SphericalMapEcs.ProjectToSphere(spawn, radius);
        }

        public static bool CanDeliverLoadToShip(float3 projectilePos, float3 shipCenter, float shipRadius, float mapW, float mapH)
        {
            // --- CanDeliverLoadToShip ---
            float3 hullPoint = GetShipMagnetTarget(shipCenter, shipRadius, projectilePos, mapW, mapH);
            float collectDist = math.max(ShipLoadCollectMinDistance, TransportRadius + ShipLoadCollectPadding);
            return ToroidalMapEcs.ToroidalDistance(projectilePos, hullPoint, mapW, mapH) <= collectDist;
        }

        public static bool HasBriefTravelBeforeLoad(float3 projectilePos, float3 spawnPosition, float elapsed, float mapW, float mapH)
        {
            // --- HasBriefTravelBeforeLoad ---
            if (elapsed < LoadDeliveryMinSeconds)
                return false;
            return ToroidalMapEcs.ToroidalDistance(projectilePos, spawnPosition, mapW, mapH) >= LoadDeliveryMinSpawnDistance;
        }

        public static bool CanCompleteUnloadDelivery(float3 projectilePos, float3 spawnPosition, float3 planetCenter, float planetSize, float elapsed, float mapW, float mapH)
        {
            // --- CanCompleteUnloadDelivery ---
            // [TITAN-ORBIT] Brief min-time + min-travel so a brand-new spawn on the surface is not
            // consumed on the same tick; once those clear, surface reach finishes the hop.
            if (elapsed < UnloadDeliveryMinSeconds)
                return false;
            if (ToroidalMapEcs.ToroidalDistance(projectilePos, spawnPosition, mapW, mapH) < UnloadDeliveryMinTravelDistance)
                return false;
            float surfaceReach = math.max(0.85f, planetSize * 0.12f);
            float3 surface = GetPlanetSurfaceToward(planetCenter, planetSize, projectilePos, mapW, mapH);
            return ToroidalMapEcs.ToroidalDistance(projectilePos, surface, mapW, mapH) <= surfaceReach;
        }

        /// <summary>
        /// Whether a load transport that turned around (ship left orbit / became ineligible) has
        /// reached the source planet surface and should refund population.
        /// <para>
        /// [TITAN-ORBIT] Uses surface reach + a short min elapsed only. Do <b>not</b> wait the full
        /// <see cref="EffectiveVisualTravelSeconds"/> hop (~6.25s), and do <b>not</b> require a large
        /// distance-from-spawn. Load spheres spawn on the surface; when they return along that same
        /// radial, spawn distance shrinks again while they are on the surface — a 0.75 world-unit
        /// spawn gate fought surface consume and left spheres bouncing near the planet for a long
        /// time (or until an intermittent geometry sweet spot).
        /// </para>
        /// </summary>
        public static bool CanCompleteReturnToSourcePlanet(
            float3 projectilePos,
            float3 spawnPosition,
            float3 planetCenter,
            float planetSize,
            float elapsed,
            float mapW,
            float mapH)
        {
            // --- Return-to-planet consume (ship left ring mid-load) ---
            // spawnPosition is unused for distance gating (spawn is on the surface — see summary).
            _ = spawnPosition;

            // Short min-time only: avoids same-tick refund if the ship leaves on the spawn frame.
            // Unload's min-travel-from-spawn does not apply here — that gate fights surface arrival.
            if (elapsed < UnloadDeliveryMinSeconds)
                return false;

            float surfaceReach = math.max(0.85f, planetSize * 0.12f);
            float3 surface = GetPlanetSurfaceToward(planetCenter, planetSize, projectilePos, mapW, mapH);
            return ToroidalMapEcs.ToroidalDistance(projectilePos, surface, mapW, mapH) <= surfaceReach;
        }
    }
}
