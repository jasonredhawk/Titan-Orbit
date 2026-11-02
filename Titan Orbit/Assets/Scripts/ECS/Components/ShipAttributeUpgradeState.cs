using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Per-stat gem upgrade levels (0 through current ship level each). Players spend gems from
    /// the bottom HUD to increment these. A 3-second hold on RESET zeros one ability with no
    /// gem refund. Counts feed <see cref="TitanOrbit.Data.ShipComponentExtraLevelMath"/>
    /// as <c>abilityLevel</c> in Extra Level math
    /// (each part: Base + its own PerExtra × (shipLevel + ability)),
    /// applied by <see cref="ShipStatApplyLogic"/>. Ghost-serialized for client upgrade UI.
    /// All ten clear when the player pays for the next chassis level. A free same-slot
    /// family swap keeps them. Buying a Titan parks a server copy and death restore
    /// writes that copy back onto the old hull (<see cref="PreTitanAttributeSnapshot"/>).
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
