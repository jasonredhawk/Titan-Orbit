using Unity.Collections;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Owner client input for predicted ship movement and combat. Implements
    /// <see cref="IInputComponentData"/> — NetCode serializes this from client to server each tick
    /// and applies it to predicted ghosts on the client during <see cref="GhostInputSystemGroup"/>.
    /// Filled by <see cref="Game.ShipInputBridge"/> on the client. Paired with
    /// <see cref="ShipInputApplySystem"/> which copies pending input onto the local ghost.
    /// </summary>
    public struct ShipInput : IInputComponentData
    {
        // --- Type members ---
        /// <summary>Normalized aim direction on the XZ plane (mouse relative to ship).</summary>
        [GhostField(Quantization = 1000)]
        public float2 AimPlanarDir;

        /// <summary>Reserved for future strafe input; currently unused (thrust uses ship forward).</summary>
        [GhostField(Quantization = 1000)]
        public float2 MovePlanarDir;

        /// <summary>True while forward thrust is held (right-click or W).</summary>
        [GhostField]
        public bool Thrust;

        /// <summary>
        /// [NETCODE] InputEvent — tracks "pressed this tick" for one-shot actions like firing.
        /// </summary>
        [GhostField]
        public InputEvent Fire;

        /// <summary>True when space-brake deceleration is toggled on.</summary>
        [GhostField]
        public bool SpaceBrakes;

        /// <summary>When true at a landed moon, gems transfer to the planet (manual or auto-deposit toggle).</summary>
        [GhostField]
        public bool WantDepositGems;

        public FixedString512Bytes ToFixedString() =>
            $"ShipInput[t={Thrust},f={Fire.Count},b={SpaceBrakes},d={WantDepositGems}]";
    }
}
