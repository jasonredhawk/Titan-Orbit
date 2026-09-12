using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Marks client-only Entities Graphics mesh parts parented to a ship ghost for rendering.
    /// Not replicated — rebuilt locally from <see cref="ShipChassisVisualCatalog"/>.
    /// </summary>
    public struct ShipVisualPartTag : IComponentData
    {
        /// <summary>Ship ghost entity this visual part follows via <see cref="Unity.Transforms.Parent"/>.</summary>
        public Entity ShipEntity;
    }

    /// <summary>
    /// Tracks which chassis visual is attached to a ship on the client (Entities Graphics path).
    /// </summary>
    public struct ShipClientVisualState : IComponentData
    {
        public FixedString64Bytes ChassisId;
        public int AppliedShipLevel;
        public int AppliedBranchIndex;
        public TeamId AppliedTeam;
    }

    /// <summary>
    /// Client-only attitude pivot between ship ghost yaw and mesh parts (roll + pitch).
    /// Mirrors <c>ShipBankVisualApplier</c> BankPivot hierarchy for Entities Graphics ships.
    /// Parent of this pivot is the ship ghost — no intermediate smooth-anchor layer.
    /// </summary>
    public struct ShipVisualBankPivotTag : IComponentData
    {
        public Entity ShipEntity;
    }

    /// <summary>Smoothed roll + pitch state on the visual bank pivot entity.</summary>
    public struct ShipVisualBankState : IComponentData
    {
        public float CurrentBankAngleDeg;
        public float SmoothedYawRateDegPerSec;
        public float PrevYawDeg;
        public bool YawInitialized;
        public float CurrentPitchAngleDeg;
        public float SmoothedForwardAccel;
        public float AccelPitchAngleDeg;
        public float ImpactPitchAngleDeg;
        public float PrevForwardSpeed;
        public bool PitchSpeedInitialized;
    }
}
