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

        /// <summary>
        /// Cannon lasers went inactive. Cleared only when the pool is strictly
        /// above <c>CannonLaserMath.RechargeRatio</c> of MaxEnergy. Releasing
        /// Fire does not reset it. Ghosted so client beams hide during the wait.
        /// </summary>
        [GhostField] public bool CannonLaserLockout;

        /// <summary>
        /// True while at least one cannon is burning a live lock or Shift mouse
        /// stream. Ghosted so client beams hide when the last target dies even
        /// if Fire is still held (no interpolation — bool snaps).
        /// </summary>
        [GhostField] public bool CannonLaserPulseOn;
    }

    /// <summary>
    /// Server-only sticky auto-aim, one slot per weapon mount. Each muzzle searches
    /// when Fire is pressed (ships / turrets / moon shields first, asteroids only
    /// when no enemy is in range) and keeps an enemy lock until it dies, leaves
    /// range, or Fire is released. An asteroid lock yields as soon as any enemy
    /// enters that muzzle's range, even if the rock is closer.
    /// Target == this hull means no current lock (hull-forward fire).
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

        /// <summary>
        /// Cannon burn subject on a planet lock.
        /// <see cref="CannonLaserHitApply.FocusMoon"/> or <c>pad slot + 1</c>.
        /// Ships and asteroids stay <see cref="CannonLaserHitApply.FocusWholeEntity"/>.
        /// Server only — this buffer is not ghosted.
        /// </summary>
        public int Focus;
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

        /// <summary>
        /// MEGA unique-weapon class (<see cref="ShipWeaponKind"/>). Ghosted so predicted
        /// clients keep cannon-laser barrels after mount-buffer rollback (mount
        /// <c>WeaponKind</c> is not a GhostField).
        /// </summary>
        [GhostField]
        public byte WeaponKind;

        /// <summary>
        /// Seconds this barrel has been burning its current lock (0–<c>RampDurationSeconds</c>).
        /// Restarts at 0 when the burn subject changes (50% DPS): a new entity, or a
        /// different pad / moon on the same planet. The same subject keeps charge.
        /// </summary>
        [GhostField(Quantization = 100, Smoothing = SmoothingAction.Clamp)]
        public float CannonLaserRampSeconds;

        /// <summary>
        /// Increments once per server tick this barrel actually applies a laser slice.
        /// Clients turn the delta into the floating damage number. A local
        /// <c>Time.deltaTime</c> estimate reaches 0 HP a round-trip before
        /// <c>AsteroidDestroyedRpc</c>, so the counter used to freeze while the
        /// rock was still on screen.
        /// </summary>
        [GhostField]
        public uint CannonLaserBurnSequence;
    }
}
