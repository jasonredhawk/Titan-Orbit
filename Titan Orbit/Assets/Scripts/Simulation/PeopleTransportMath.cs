using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Magnet-steered people transport motion ported from legacy PeopleTransportProjectile.
    /// Server <see cref="ECS.Systems.PeopleTransportSystem"/> and client
    /// <see cref="Game.PeopleTransportVisualApplier"/> share these constants and steering math
    /// so visuals match authoritative delivery timing. Uses toroidal helpers from
    /// <see cref="Shared.ToroidalMapEcs"/> for wrap-aware paths.
    /// </summary>
    public static class PeopleTransportMath
    {
        /// <summary>[TITAN-ORBIT] Target one-way visual travel time before duration multipliers.</summary>
        public const float TargetVisualTravelSeconds = 3f;
        public const float VisualTravelDurationMultiplier = 5f;
        public const float VisualTravelSpeedBonus = 2.4f;
        public const float SurfaceSpawnOutwardNudge = 0.45f;
        public const float LoadMagnetSpeedMultiplier = 1.5f;
        public const float MagnetCloseRangeWorld = 5f;
        public const float MagnetCloseRangeSpeedRatio = 18f / 11f;
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
        public const float PeopleAmountScaleMin = 1f;
        public const float PeopleAmountScaleMax = 12f;
        public const float VisualScaleMinMultiplier = 0.9f;
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
            myPos.y = 0f;
            targetPos.y = 0f;
            float3 toTarget = ToroidalMapEcs.ToroidalDirection(myPos, targetPos, mapW, mapH);
            float dist = ToroidalMapEcs.ToroidalDistance(myPos, targetPos, mapW, mapH);
            float closeSpeed = cruiseSpeed * MagnetCloseRangeSpeedRatio;
            float speed = dist <= MagnetCloseRangeWorld ? closeSpeed : cruiseSpeed;
            float3 targetVel = toTarget * speed;
            return math.lerp(currentVel, targetVel, math.saturate(speed * dt * 4f));
        }

        public static float GetVisualScaleMultiplier(float peopleAmount)
        {
            // --- Compute value ---
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
            float3 fromPos = fromWorldPos;
            fromPos.y = 0f;
            float3 toCore = ToroidalMapEcs.ToroidalDirection(fromPos, planetCenter, mapW, mapH);
            float surfaceWorld = math.max(0.25f, planetSize) * 0.5f;
            float3 surface = planetCenter - toCore * surfaceWorld;
            surface.y = 0f;
            return surface;
        }

        public static float3 GetPlanetSurfaceSpawnToward(float3 planetCenter, float planetSize, float3 towardWorldPos, float mapW, float mapH)
        {
            // --- Compute value ---
            float3 surface = GetPlanetSurfaceToward(planetCenter, planetSize, towardWorldPos, mapW, mapH);
            float3 outward = ToroidalMapEcs.ToroidalDirection(planetCenter, surface, mapW, mapH);
            float nudge = math.max(SurfaceSpawnOutwardNudge, planetSize * 0.045f);
            surface += outward * nudge;
            surface.y = 0f;
            return surface;
        }

        public static float3 GetShipMagnetTarget(float3 shipCenter, float shipRadius, float3 fromWorldPos, float mapW, float mapH)
        {
            // --- Compute value ---
            float3 fromPos = fromWorldPos;
            fromPos.y = 0f;
            float3 toCenter = ToroidalMapEcs.ToroidalDirection(fromPos, shipCenter, mapW, mapH);
            float hullRadius = math.max(0.2f, shipRadius);
            float inset = math.clamp(hullRadius * ShipHullMagnetInset, 0.05f, 0.45f);
            float3 hullPoint = shipCenter - toCenter * math.max(0.2f, hullRadius - inset);
            hullPoint.y = 0f;
            return hullPoint;
        }

        public static float GetShipHullRadius(float shipTransformScale)
        {
            // --- Compute value ---
            if (shipTransformScale > 0.01f)
                return math.max(0.2f, shipTransformScale);
            return 1f;
        }

        public static float3 GetShipUnloadSpawnToward(float3 shipCenter, float shipRadius, float3 towardWorldPos, float mapW, float mapH)
        {
            // --- Compute value ---
            float3 outward = ToroidalMapEcs.ToroidalDirection(shipCenter, towardWorldPos, mapW, mapH);
            float hullRadius = math.max(0.2f, shipRadius);
            float nudge = math.max(0.08f, hullRadius * 0.06f);
            float3 spawn = shipCenter + outward * (hullRadius + nudge);
            spawn.y = 0f;
            return spawn;
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
            if (elapsed < UnloadDeliveryMinSeconds)
                return false;
            if (ToroidalMapEcs.ToroidalDistance(projectilePos, spawnPosition, mapW, mapH) < UnloadDeliveryMinTravelDistance)
                return false;
            float surfaceReach = math.max(0.85f, planetSize * 0.12f);
            float3 surface = GetPlanetSurfaceToward(planetCenter, planetSize, projectilePos, mapW, mapH);
            return ToroidalMapEcs.ToroidalDistance(projectilePos, surface, mapW, mapH) <= surfaceReach;
        }

        public static bool CanCompleteReturnToSourcePlanet(
            float3 projectilePos,
            float3 spawnPosition,
            float3 planetCenter,
            float planetSize,
            float elapsed,
            float mapW,
            float mapH)
        {
            if (elapsed < EffectiveVisualTravelSeconds)
                return false;
            if (ToroidalMapEcs.ToroidalDistance(projectilePos, spawnPosition, mapW, mapH) < 0.75f)
                return false;
            float surfaceReach = math.max(0.85f, planetSize * 0.12f);
            float3 surface = GetPlanetSurfaceToward(planetCenter, planetSize, projectilePos, mapW, mapH);
            return ToroidalMapEcs.ToroidalDistance(projectilePos, surface, mapW, mapH) <= surfaceReach;
        }
    }
}
