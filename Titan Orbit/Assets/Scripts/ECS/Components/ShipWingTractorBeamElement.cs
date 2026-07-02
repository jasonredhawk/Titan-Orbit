using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    public struct ShipWingTractorBeamElement : IBufferElementData
    {
        public float3 LocalPosition;
        public float TractorBeamDistance;
        public float TractorBeamDistancePerLevel;
        public float TractorBeamPower;
        public float TractorBeamPowerPerLevel;
        public float MaxGems;
        public float MaxGemsPerLevel;

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

    public static class ShipWingTractorBeamPose
    {
        public static float3 GetWorldPosition(in LocalTransform shipTransform, in ShipWingTractorBeamElement wing) =>
            GemTractorBeamMath.ResolveWingWorldPosition(shipTransform.Position, shipTransform.Rotation, wing.LocalPosition);

        public static void GetTractorParams(
            in ShipWingTractorBeamElement wing,
            int shipLevel,
            bool inOrbitZone,
            out float searchRadius,
            out float attractionSpeed) =>
            GemTractorBeamMath.GetWingTractorParams(wing.ToParams(), shipLevel, inOrbitZone, out searchRadius, out attractionSpeed);
    }
}
