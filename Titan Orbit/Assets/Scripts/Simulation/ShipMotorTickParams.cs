using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Per-tick motor configuration gathered from <see cref="TitanOrbit.ECS.ShipMotorConfig"/>,
    /// orbit context, and status effects. Passed into <see cref="ShipMotorSimulator.Step"/> each
    /// fixed step — not stored on entities.
    /// </summary>
    public struct ShipMotorTickParams
    {
        // --- Timestep ---
        /// <summary>Fixed delta time for this simulation step (frame-rate independent).</summary>
        public float FixedDeltaTime;

        // --- Motor limits (from ShipMotorConfig + upgrades) ---
        public float EngineThrust;
        public float MaxSpeed;
        public float RotationSpeedDegPerSec;
        public float BrakeDeceleration;
        public float RecoilDecayPerSecond;
        // --- Status / mode flags ---
        /// <summary>When true, motor applies hard braking (electric shock status).</summary>
        public bool ElectricShockDisabled;
        /// <summary>When true, ship does not rotate toward aim (cinematic dock, etc.).</summary>
        public bool TheatricalRotationLocked;

        // --- Orbit override (moon dock / planet ring) ---
        /// <summary>When true, velocity blends toward orbit tangential speed instead of thrust.</summary>
        public bool UseOrbit;
        /// <summary>Target velocity from PlanetOrbitMath when UseOrbit is true.</summary>
        public float3 OrbitDesiredVelocity;
        /// <summary>How quickly current velocity aligns to orbit desired velocity.</summary>
        public float OrbitAlignRate;
        /// <summary>Locked Y height for top-down space (always 0 in Titan Orbit).</summary>
        public float FixedY;
    }
}
