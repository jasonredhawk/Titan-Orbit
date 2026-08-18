using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Per-stat gem upgrade levels (0 through current ship level each). Players spend gems from
    /// the bottom HUD to increment these. Counts feed <see cref="TitanOrbit.Data.ShipComponentExtraLevelMath"/>
    /// as <c>abilityLevel</c> in Extra Level math
    /// (non-weapons add <c>(N−1)</c>; weapons use ship+ability only per barrel),
    /// applied by <see cref="ShipStatApplyLogic"/>. Ghost-serialized for client upgrade UI.
    /// Reset on ship level-up (chassis change).
    /// </summary>
    public struct ShipAttributeUpgradeState : IComponentData
    {
        // --- Type members ---
        /// <summary>Levels invested in weapon damage multiplier.</summary>
        [GhostField] public int FirePower;
        /// <summary>Levels invested in projectile speed.</summary>
        [GhostField] public int BulletSpeed;
        /// <summary>Levels invested in max hull HP.</summary>
        [GhostField] public int MaxHealth;
        /// <summary>Levels invested in passive HP regeneration.</summary>
        [GhostField] public int HealthRegen;
        /// <summary>Levels invested in weapon energy pool size.</summary>
        [GhostField] public int EnergyCapacity;
        /// <summary>Levels invested in energy recharge rate.</summary>
        [GhostField] public int EnergyRegen;
        /// <summary>Levels invested in top speed and thrust scaling.</summary>
        [GhostField] public int MovementSpeed;
        /// <summary>Levels invested in turn rate (degrees per second).</summary>
        [GhostField] public int RotationSpeed;
        /// <summary>Levels invested in gem cargo capacity.</summary>
        [GhostField] public int GemCapacity;
        /// <summary>Levels invested in troop cap.</summary>
        [GhostField] public int PeopleCapacity;
    }
}
