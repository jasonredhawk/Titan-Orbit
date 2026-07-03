using Unity.Collections;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Owner client input for predicted ship movement and combat.</summary>
    public struct ShipInput : IInputComponentData
    {
        [GhostField(Quantization = 1000)]
        public float2 AimPlanarDir;

        [GhostField(Quantization = 1000)]
        public float2 MovePlanarDir;

        [GhostField]
        public bool Thrust;

        [GhostField]
        public InputEvent Fire;

        [GhostField]
        public bool SpaceBrakes;

        /// <summary>When true at a landed moon, gems transfer to the planet (manual or auto-deposit toggle).</summary>
        [GhostField]
        public bool WantDepositGems;

        public FixedString512Bytes ToFixedString() =>
            $"ShipInput[t={Thrust},f={Fire.Count},b={SpaceBrakes},d={WantDepositGems}]";
    }
}
