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

        /// <summary>True while forward thrust is held (right-click hold).</summary>
        [GhostField]
        public bool Thrust;

        /// <summary>
        /// [TITAN-ORBIT] OVERDRIVE intent — Shift held (does <b>not</b> require thrust).
        /// Motor engage latch keys off this + energy; burst speed/drain only while Thrust is also held.
        /// </summary>
        [GhostField]
        public bool Overdrive;

        /// <summary>
        /// [NETCODE] InputEvent — tracks "pressed this tick" for one-shot actions like firing.
        /// </summary>
        [GhostField]
        public InputEvent Fire;

        /// <summary>
        /// [NETCODE] InputEvent — B key / CycleBullet action. Server increments
        /// <see cref="ShipLoadoutState.RuntimeBulletIndex"/> when set (same pattern as <see cref="Fire"/>).
        /// </summary>
        [GhostField]
        public InputEvent CycleBullet;

        /// <summary>True when space-brake deceleration is toggled on.</summary>
        [GhostField]
        public bool SpaceBrakes;

        /// <summary>When true at a landed moon, gems transfer to the planet (manual or auto-deposit toggle).</summary>
        [GhostField]
        public bool WantDepositGems;

        public FixedString512Bytes ToFixedString() =>
            $"ShipInput[t={Thrust},o={Overdrive},f={Fire.Count},c={CycleBullet.Count},b={SpaceBrakes},d={WantDepositGems}]";
    }
}
