using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>Per-tick motor configuration gathered from ship stats and world context.</summary>
    public struct ShipMotorTickParams
    {
        public float FixedDeltaTime;
        public float EngineThrust;
        public float MaxSpeed;
        public float RotationSpeedDegPerSec;
        public float BrakeDeceleration;
        public float RecoilDecayPerSecond;
        public bool ElectricShockDisabled;
        public bool TheatricalRotationLocked;
        public bool UseOrbit;
        public float3 OrbitDesiredVelocity;
        public float OrbitAlignRate;
        public float FixedY;
    }
}
