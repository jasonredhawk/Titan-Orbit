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
        /// [TITAN-ORBIT] Shift held (does <b>not</b> require thrust).
        /// Regular ships: OVERDRIVE intent. Motor latch keys off this + energy; burst
        /// speed/drain only while Thrust is also held.
        /// MEGA hulls: no overdrive. This bit locks heading and aims all MEGA
        /// guns at the mouse world point (<see cref="AimPlanarDir"/> ×
        /// <see cref="AimDistance"/>). Fire is still required to shoot.
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

        /// <summary>
        /// [NETCODE] InputEvent — ALT / FireRocket. Server <c>ShipRocketFireSystem</c> consumes
        /// one store rocket charge (unless infinite-rocket debug) and spawns a homing shot.
        /// </summary>
        [GhostField]
        public InputEvent FireRocket;

        /// <summary>
        /// When true, skip space-brake deceleration (frictionless coast). Default
        /// <c>false</c> so <c>default(ShipInput)</c> / baked ghosts still brake.
        /// Left Ctrl toggles this via <c>PlayerInputHandler</c>.
        /// </summary>
        [GhostField]
        public bool DisableSpaceBrakes;

        /// <summary>When true at a landed moon, gems transfer to the planet (manual or auto-deposit toggle).</summary>
        [GhostField]
        public bool WantDepositGems;

        /// <summary>
        /// Which rocket HUD row to fire (0 = first pack). Client <c>RocketSlotSelection</c>
        /// updates this every tick; the server maps it onto the equipment buffer.
        /// Kept last so older command layouts still line up on <see cref="DisableSpaceBrakes"/>.
        /// </summary>
        [GhostField]
        public int SelectedRocketSlot;

        /// <summary>
        /// [NETCODE] InputEvent — ALT while the loadout caret is on a mine pack.
        /// Server <c>ShipMineDeploySystem</c> consumes one store mine charge (unless
        /// infinite-mine debug) and appends a deployed mine.
        /// Appended after <see cref="SelectedRocketSlot"/> so older command layouts still line up.
        /// </summary>
        [GhostField]
        public InputEvent PlaceMine;

        /// <summary>
        /// Which mine HUD row to place (0 = first pack). Client <c>MineSlotSelection</c>
        /// updates this every tick; the server maps it onto the equipment buffer.
        /// </summary>
        [GhostField]
        public int SelectedMineSlot;

        /// <summary>
        /// Planar distance from the local hull (or turret pad) to the mouse world
        /// point, in world units. <see cref="AimPlanarDir"/> stays a unit vector so
        /// existing yaw / turret code does not change.
        /// <para>
        /// [TITAN-ORBIT] MEGA Shift auto-guns need this so each muzzle aims at the
        /// same world point (streams converge) instead of sharing one direction
        /// (parallel volleys that miss the cursor on wide hulls).
        /// </para>
        /// Appended after <see cref="SelectedMineSlot"/> so older command layouts
        /// still line up on the fields above.
        /// </summary>
        [GhostField(Quantization = 10)]
        public float AimDistance;

        public FixedString512Bytes ToFixedString() =>
            $"ShipInput[t={Thrust},o={Overdrive},f={Fire.Count},c={CycleBullet.Count},r={FireRocket.Count},m={PlaceMine.Count},b={!DisableSpaceBrakes},d={WantDepositGems},s={SelectedRocketSlot},n={SelectedMineSlot},ad={AimDistance}]";
    }
}
