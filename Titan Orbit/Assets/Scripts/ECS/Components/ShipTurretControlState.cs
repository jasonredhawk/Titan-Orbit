using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Ghosted ship possession of a planetary defense turret pad.
    /// When <see cref="IsControlling"/> is true, the ship hull is stowed (hidden, frozen, immune)
    /// and the player's <see cref="ShipInput"/> Aim + Fire drive that pad's muzzle instead of
    /// the ship guns. Exit is RMB thrust (server reads <see cref="ShipInput.Thrust"/>).
    /// <para>
    /// [NETCODE] Must be baked on the ship ghost prefab — runtime-only AddComponent does not
    /// replicate GhostFields to clients (same trap as <see cref="ShipMatchStats"/>).
    /// [TITAN-ORBIT] Turrets are not separate ghosts; possession is a mode on the ship plus
    /// <see cref="PlanetaryDefenseSlotElement.OccupiedByNetworkId"/> on the planet buffer.
    /// </para>
    /// </summary>
    public struct ShipTurretControlState : IComponentData
    {
        /// <summary>True while this ship is stowed and piloting a defense pad.</summary>
        [GhostField] public bool IsControlling;

        /// <summary>
        /// Stable <see cref="PlanetState.PlanetId"/> of the occupied pad's planet.
        /// 0 when not controlling.
        /// </summary>
        [GhostField] public int PlanetId;

        /// <summary>0-based slot index on that planet. Ignored when not controlling.</summary>
        [GhostField] public byte SlotIndex;
    }
}
