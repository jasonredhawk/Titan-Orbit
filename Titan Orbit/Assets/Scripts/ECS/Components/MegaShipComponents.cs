using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One of the three level-7 MEGA slots on a planet ghost. Buffer length is always 3
    /// (tree branches 0 / 1 / 2). Each slot is any hull from the full MEGA catalog.
    /// <para>
    /// [NETCODE] Must be baked on the planet ghost prefab — runtime <c>AddBuffer</c> does not
    /// replicate <see cref="GhostField"/> values. Every field on a ghost buffer must be a GhostField.
    /// </para>
    /// [TITAN-ORBIT] <see cref="OccupiedByNetworkId"/> is the MEGA owner's GhostOwner id while
    /// that unique hull is alive. 0 = available for purchase. Death / disconnect clears it.
    /// </summary>
    [InternalBufferCapacity(3)]
    public struct PlanetMegaShipSlotElement : IBufferElementData
    {
        /// <summary>0-based L7 tree branch (left / center / right).</summary>
        [GhostField] public byte SlotIndex;

        /// <summary>Index into <c>MegaShipCatalog.entries</c> for this match's rolled hull.</summary>
        [GhostField] public ushort CatalogIndex;

        /// <summary>GhostOwner.NetworkId of the player flying this MEGA, or 0 when free.</summary>
        [GhostField] public int OccupiedByNetworkId;
    }

    /// <summary>
    /// Ghosted MEGA identity on a ship. When <see cref="IsMega"/> is true the hull is a static
    /// endgame ship: no attribute upgrades, no gems, auto-fire guns, unique occupancy.
    /// Previous L6 family/level/branch are stored so death can restore that chassis.
    /// <para>
    /// [NETCODE] Must be baked on StarshipGhost — runtime AddComponent does not replicate.
    /// </para>
    /// </summary>
    public struct MegaShipState : IComponentData
    {
        /// <summary>True while this ship is a purchased MEGA hull.</summary>
        [GhostField] public bool IsMega;

        /// <summary>Catalog index of the live MEGA prefab.</summary>
        [GhostField] public ushort CatalogIndex;

        /// <summary>Planet that sold this MEGA (occupancy lives on that planet's slot buffer).</summary>
        [GhostField] public int StorePlanetId;

        /// <summary>0–2 slot on that planet (matches L7 branch).</summary>
        [GhostField] public byte MegaSlotIndex;

        /// <summary>Family index to restore after MEGA death.</summary>
        [GhostField] public byte PreviousFamilyIndex;

        /// <summary>Ship level to restore (normally 6).</summary>
        [GhostField] public int PreviousLevel;

        /// <summary>Branch to restore on the previous level.</summary>
        [GhostField] public int PreviousBranch;

        /// <summary>When true, friendlies cannot Take Control of any gun pad.</summary>
        [GhostField] public bool GunsLocked;
    }

    /// <summary>
    /// Server-only sticky auto-aim, one slot per weapon mount. A single hull-center
    /// search fills empty slots when Fire is pressed. Locks clear when Fire is released
    /// so the next press re-acquires the closest targets.
    /// </summary>
    [InternalBufferCapacity(32)]
    public struct MegaShipAutoAimSlotElement : IBufferElementData
    {
        /// <summary>Current lock (ship, planet, or asteroid). Null when none.</summary>
        public Entity Target;

        /// <summary>Last toroidal aim point written when the lock was validated.</summary>
        public float3 AimPoint;
    }

    /// <summary>
    /// One gunner pad on a MEGA hull — one element per weapon mount.
    /// OccupiedByNetworkId is the friendly piloting that mount (0 = auto-fire).
    /// <para>
    /// [NETCODE] Baked empty on StarshipGhost; length is resized when a MEGA chassis applies.
    /// Ghost buffers require every field to be a GhostField.
    /// </para>
    /// </summary>
    [InternalBufferCapacity(32)]
    public struct MegaShipGunnerSlotElement : IBufferElementData
    {
        /// <summary>Index into the ship's <see cref="ShipWeaponMountElement"/> buffer.</summary>
        [GhostField] public byte MountIndex;

        /// <summary>GhostOwner.NetworkId of the gunner, or 0 when the mount auto-aims for the owner.</summary>
        [GhostField] public int OccupiedByNetworkId;

        /// <summary>
        /// Hull-local planar yaw of this mount in degrees (ghosted so hybrid proxies can
        /// rotate the classified weapon child). Server writes this from
        /// <see cref="ShipWeaponMountElement.LocalRotation"/> each tick.
        /// </summary>
        [GhostField(Quantization = 10, Smoothing = SmoothingAction.Interpolate)]
        public float CurrentYawDeg;

        /// <summary>
        /// Toroidal distance from this muzzle to its current auto-aim target (0 = none).
        /// Client reticles sit at this range along the live barrel heading.
        /// </summary>
        [GhostField(Quantization = 10, Smoothing = SmoothingAction.Interpolate)]
        public float TargetDistance;
    }

    /// <summary>
    /// Ghosted possession of a MEGA gun pad on a friendly MEGA. Mirrors
    /// <see cref="ShipTurretControlState"/> but the pad lives on another ship, not a planet.
    /// Must be baked on StarshipGhost.
    /// </summary>
    public struct ShipMegaGunControlState : IComponentData
    {
        /// <summary>True while this ship is stowed and aiming a MEGA mount.</summary>
        [GhostField] public bool IsControlling;

        /// <summary>GhostOwner.NetworkId of the MEGA owner (identifies the hull).</summary>
        [GhostField] public int MegaOwnerNetworkId;

        /// <summary>Mount / gunner-slot index on that MEGA.</summary>
        [GhostField] public byte MountIndex;
    }
}
