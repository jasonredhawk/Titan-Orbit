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
    }

    /// <summary>
    /// Server-only sticky auto-aim, one slot per weapon mount. A single hull-center
    /// search fills empty slots when Fire is pressed. Locks clear when Fire is released
    /// so the next press re-acquires the closest targets.
    /// <para>
    /// [TITAN-ORBIT] <see cref="AimPoint"/> is the lead intercept (target motion +
    /// this hull's velocity), not the target's current pivot. Phase B still fires
    /// along the barrel; this point is what the barrel looks at.
    /// </para>
    /// </summary>
    [InternalBufferCapacity(32)]
    public struct MegaShipAutoAimSlotElement : IBufferElementData
    {
        /// <summary>Current lock (ship, planet, or asteroid). Null when none.</summary>
        public Entity Target;

        /// <summary>
        /// Lead intercept the barrel should look at (toroidal, unwrapped near the muzzle).
        /// </summary>
        public float3 AimPoint;

        /// <summary>
        /// Planar flight budget to that intercept (0 = no lead / hull-forward park).
        /// Phase B grows <c>BulletElement.MaxDistance</c> with this so fleeing shots
        /// are not culled before they arrive.
        /// </summary>
        public float InterceptDistance;
    }

    /// <summary>
    /// Ghosted aim state for one MEGA weapon mount. Only the MEGA owner aims and fires
    /// these barrels (auto-aim, or Shift + mouse point). There is no remote gunner occupancy.
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

        /// <summary>
        /// Planar yaw in degrees. While <see cref="TargetDistance"/> &gt; 0 this is the
        /// <b>world</b> fire heading (same ray the server spawned). Idle / parked this is
        /// hull-local yaw. Clients must not LookAt a lagged aim point from the live muzzle —
        /// that over-rotates as the hull moves, then snaps back when the snapshot catches up.
        /// </summary>
        [GhostField(Quantization = 10, Smoothing = SmoothingAction.Clamp)]
        public float CurrentYawDeg;

        /// <summary>
        /// Toroidal distance from this muzzle to its current aim point (0 = not tracking).
        /// Client reticles sit at this range along the live barrel heading.
        /// </summary>
        [GhostField(Quantization = 10, Smoothing = SmoothingAction.Clamp)]
        public float TargetDistance;

        /// <summary>
        /// World X of the lock's <b>current</b> point (ship / pad / moon — not the lead
        /// intercept). Turret meshes LookAt this from the live muzzle; bullets still use
        /// hull-local <c>LocalRotation</c> toward the lead.
        /// </summary>
        [GhostField(Quantization = 10, Smoothing = SmoothingAction.Clamp)]
        public float AimWorldX;

        /// <summary>
        /// World Z of the lock's current point. Paired with <see cref="AimWorldX"/>.
        /// </summary>
        [GhostField(Quantization = 10, Smoothing = SmoothingAction.Clamp)]
        public float AimWorldZ;

        /// <summary>
        /// <c>GhostInstance.ghostId</c> of the sticky lock (0 = none). Clients LookAt that
        /// ghost's live display pose so the mesh stays on the target while the hull moves.
        /// The lead intercept in <see cref="AimWorldX"/> is for bullets, not the mesh.
        /// </summary>
        [GhostField]
        public int TargetGhostId;
    }
}
