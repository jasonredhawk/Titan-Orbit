using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] RPC commands (Remote Procedure Calls) — one-shot network messages outside ghost
    /// replication. Each struct implements <c>IRpcCommand</c>; clients send requests, server systems
    /// validate and reply. Handlers: <see cref="TeamManagementSystem"/>,
    /// <see cref="RejoinShipManagementSystem"/>, <see cref="PlayerNameServerSystem"/>,
    /// moon orbit store systems, attribute upgrade systems.
    /// Ghost replication handles continuous state; RPCs handle discrete player actions.
    /// </summary>

    /// <summary>
    /// [NETCODE] Client requests a team assignment at spawn. Handled by <see cref="TeamManagementSystem"/>.
    /// </summary>
    public struct RequestTeamCommand : IRpcCommand
    {
        // --- Type members ---
        /// <summary>[NETCODE] Sending player's network id (server validates against connection).</summary>
        public int NetworkId;

        /// <summary>[TITAN-ORBIT] Requested team as byte (cast to <see cref="Core.TeamId"/>).</summary>
        public byte RequestedTeam;
    }

    /// <summary>
    /// [NETCODE] Server confirms or rejects team choice; client reads in <see cref="TeamChoiceResultClientSystem"/>.
    /// <para>
    /// [TITAN-ORBIT] <see cref="SpawnPosition"/> travels with the ack for logs / diagnostics.
    /// Join Team does <b>not</b> Instantiates a client predicted hull — GhostReceive delivers
    /// the server ship at this pose. Keep these fields: changing the RPC layout requires a
    /// matching Linux headless rebuild.
    /// </para>
    /// </summary>
    public struct TeamChoiceResultRpc : IRpcCommand
    {
        /// <summary>[NETCODE] Target player's network id.</summary>
        public int NetworkId;

        /// <summary>[TITAN-ORBIT] Team actually assigned (may differ if request was invalid).</summary>
        public byte AssignedTeam;

        /// <summary>[STANDARD] 1 = success, 0 = failure.</summary>
        public byte Success;

        /// <summary>
        /// [TITAN-ORBIT] 1 when <see cref="SpawnPosition"/> is the server home-ring spawn.
        /// 0 on failure, or when an older server omitted the pose (client finds the ring itself).
        /// </summary>
        public byte HasSpawnPos;

        /// <summary>
        /// [TITAN-ORBIT] Unbounded world spawn on the home orbit ring (same value the server
        /// wrote to the ship <c>LocalTransform</c>). Ignored when <see cref="HasSpawnPos"/> is 0.
        /// </summary>
        public float3 SpawnPosition;

        /// <summary>[TITAN-ORBIT] Human-readable rejection or confirmation message for lobby UI.</summary>
        public FixedString128Bytes Message;
    }

    /// <summary>
    /// [NETCODE] Client publishes the Main Menu display name after GoInGame.
    /// Server: <see cref="PlayerNameServerSystem"/> (NetworkId comes from the connection, not this
    /// payload — clients cannot spoof another player's name). Adding fields changes RPC layout:
    /// client and Linux headless must rebuild together.
    /// </summary>
    public struct SetPlayerNameCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] UTF-8 display name (length capped by FixedString64).</summary>
        public FixedString64Bytes DisplayName;

        /// <summary>
        /// Filename-stable badge id from Badge (N).png. 0 = none.
        /// Adding fields changes RPC layout: client and Linux headless must rebuild together.
        /// </summary>
        public int BadgeId;
    }

    /// <summary>
    /// [NETCODE] Server → all clients: one player's display name for nameplates and leaderboards.
    /// The match-singleton <see cref="PlayerNameElement"/> buffer is not a ghost (runtime entity,
    /// not a ghost prefab), so names travel on this RPC instead of snapshot replication.
    /// Late joiners get a one-shot dump tagged with <see cref="PlayerNameRosterSent"/>.
    /// </summary>
    public struct PlayerNameAnnounceRpc : IRpcCommand
    {
        /// <summary>[NETCODE] Owner connection id (GhostOwner.NetworkId on that player's ship).</summary>
        public int NetworkId;

        /// <summary>[TITAN-ORBIT] Sanitized UTF-8 display name.</summary>
        public FixedString64Bytes DisplayName;

        /// <summary>Filename-stable badge id from Badge (N).png. 0 = none.</summary>
        public int BadgeId;
    }

    /// <summary>
    /// [NETCODE] Client requests contributed-gem balance at a home planet orbit store.
    /// </summary>
    public struct RequestContributedGemsCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] <see cref="PlanetState.PlanetId"/> of the home planet store.</summary>
        public int HomePlanetId;
    }

    /// <summary>
    /// [NETCODE] Server replies with the requesting player's contributed gem total at the home planet.
    /// </summary>
    public struct ContributedGemsResultRpc : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Spendable contributed gem balance.</summary>
        public float Amount;
    }

    /// <summary>
    /// [NETCODE] Client toggles auto-deposit gems while docked at a moon. Server writes
    /// <see cref="ShipDepositIntent.WantDepositGems"/>.
    /// </summary>
    public struct SetWantDepositGemsCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Desired deposit toggle state.</summary>
        public bool WantDeposit;
    }

    /// <summary>
    /// [NETCODE] Client toggles Damage vs Heal bullets from the Orbit Menu.
    /// Server writes <see cref="ShipLoadoutState.HealingBulletsActive"/>.
    /// </summary>
    public struct SetHealingBulletsCommand : IRpcCommand
    {
        /// <summary>True = fire the shared EnergySpheres heal bank.</summary>
        public bool HealingActive;
    }

    /// <summary>
    /// [NETCODE] Client requests to take control of a built planetary defense turret pad.
    /// Server validates zone / team / occupancy, then sets <see cref="ShipTurretControlState"/>
    /// and <see cref="PlanetaryDefenseSlotElement.OccupiedByNetworkId"/>. Exit is not an RPC —
    /// server ejects when <see cref="ShipInput.Thrust"/> is held while controlling.
    /// </summary>
    public struct EnterPlanetaryDefenseTurretCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Stable <see cref="PlanetState.PlanetId"/> hosting the pad.</summary>
        public int PlanetId;

        /// <summary>[TITAN-ORBIT] 0-based defense slot index on that planet.</summary>
        public byte SlotIndex;
    }

    /// <summary>
    /// [NETCODE] Client purchases a ship upgrade at an orbit station store. Server validates gems,
    /// level prerequisites, and branch availability.
    /// </summary>
    public struct PurchaseShipUpgradeCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Planet id of the store moon.</summary>
        public int StorePlanetId;

        /// <summary>[TITAN-ORBIT] Target ship level after purchase.</summary>
        public int TargetLevel;

        /// <summary>[TITAN-ORBIT] Index into ship family upgrade branch array.</summary>
        public int TargetBranchIndex;
    }

    /// <summary>
    /// [NETCODE] Client purchases a non-ship store item at a home planet moon store
    /// (drones / rockets / mines — not ship-family components).
    /// </summary>
    public struct PurchaseStoreItemCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Home planet id hosting the store.</summary>
        public int HomePlanetId;

        /// <summary>[TITAN-ORBIT] Opaque item type id from store catalog.</summary>
        public int ItemType;
    }

    /// <summary>
    /// [NETCODE] Client purchases a ship-family extra component by stable component id.
    /// Fills one equipment slot; server validates gems, empty slot, and catalog entry.
    /// </summary>
    public struct PurchaseStoreComponentCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Home planet id hosting the store.</summary>
        public int HomePlanetId;

        /// <summary>[TITAN-ORBIT] Stable component id from ShipFamilyDefinition (e.g. Engine_02).</summary>
        public FixedString64Bytes ComponentId;
    }

    /// <summary>
    /// [NETCODE] Client pays for a card spin at the docked store planet (three weighted offers).
    /// </summary>
    public struct CardSpinCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Planet id of the moon store where the player is docked.</summary>
        public int StorePlanetId;
    }

    /// <summary>
    /// [NETCODE] Client takes one card from the current spin offer into an empty card slot (spin already paid).
    /// </summary>
    public struct TakeSpinCardCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Planet id of the docked store (ownership / origin checks).</summary>
        public int StorePlanetId;

        /// <summary>[TITAN-ORBIT] Stable card id that must be in the server pending offer.</summary>
        public FixedString64Bytes CardId;
    }

    /// <summary>
    /// [NETCODE] Client removes an equipped upgrade card at the given slot index (free discard).
    /// </summary>
    public struct RemoveEquippedCardCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Index into the ship's EquippedCardElement buffer.</summary>
        public int SlotIndex;
    }

    /// <summary>
    /// [NETCODE] Client removes an equipped store item / component at the given slot index (free discard).
    /// </summary>
    public struct RemoveEquippedEquipmentCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Index into the ship's EquippedEquipmentElement buffer.</summary>
        public int SlotIndex;
    }

    /// <summary>
    /// [NETCODE] Server → purchasing client: three card ids from a paid spin (empty string = empty slot).
    /// </summary>
    public struct CardSpinOfferRpc : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Offer slot 0 stable card id.</summary>
        public FixedString64Bytes CardId0;

        /// <summary>[TITAN-ORBIT] Offer slot 1 stable card id.</summary>
        public FixedString64Bytes CardId1;

        /// <summary>[TITAN-ORBIT] Offer slot 2 stable card id.</summary>
        public FixedString64Bytes CardId2;

        /// <summary>[STANDARD] 1 = offer filled, 0 = spin failed / empty pool.</summary>
        public byte Success;
    }

    /// <summary>[NETCODE] Server success/failure reply for orbit store purchases.</summary>
    public struct OrbitStoreResultRpc : IRpcCommand
    {
        /// <summary>[STANDARD] 1 = purchase succeeded, 0 = rejected.</summary>
        public byte Success;

        /// <summary>[TITAN-ORBIT] Failure reason or confirmation text for orbit UI.</summary>
        public FixedString128Bytes Message;
    }

    /// <summary>
    /// [NETCODE] Client buys a stat attribute upgrade (speed, health, etc.) from HUD upgrade panel.
    /// </summary>
    public struct PurchaseAttributeUpgradeCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Index into ship attribute upgrade table.</summary>
        public int AttributeIndex;
    }

    /// <summary>
    /// [NETCODE] Client reconnected to a match that still has their ship — resume control without
    /// re-picking team. Handled by <see cref="RejoinShipManagementSystem"/>.
    /// </summary>
    public struct ResumeExistingShipCommand : IRpcCommand { }

    /// <summary>
    /// [NETCODE] Client wants a new ship and team; server destroys the persisted ship and clears
    /// CommandTarget. Handled by <see cref="RejoinShipManagementSystem"/>.
    /// </summary>
    public struct AbandonShipForRejoinCommand : IRpcCommand { }

    /// <summary>
    /// [NETCODE] Server response to resume/abandon rejoin choice. Handled by
    /// <see cref="RejoinShipResultClientSystem"/>.
    /// </summary>
    public struct RejoinShipResultRpc : IRpcCommand
    {
        /// <summary>[STANDARD] 1 = action succeeded, 0 = rejected.</summary>
        public byte Success;

        /// <summary>[TITAN-ORBIT] 1 = resume existing ship, 2 = abandon for fresh team pick.</summary>
        public byte Choice;

        /// <summary>[TITAN-ORBIT] Team assigned after abandon (only meaningful for choice 2).</summary>
        public byte AssignedTeam;

        /// <summary>[TITAN-ORBIT] Status message for rejoin dialog UI.</summary>
        public FixedString128Bytes Message;
    }

    /// <summary>
    /// [NETCODE] Server → all clients: spawn a cosmetic people-transport float.
    /// Ghost Instantiates are too slow under MaxSendChunks/Instantiates caps for ~1s flights;
    /// clients create local VFX from this RPC (see PeopleTransportSpawnRpcClientSystem).
    /// <para>
    /// Wire size is 62 bytes (includes <see cref="TargetPosition"/>). Client and Linux headless
    /// must share this layout — hash mismatch triggers RpcSystem skip (TitanOrbit patch) or disconnect.
    /// </para>
    /// </summary>
    public struct PeopleTransportSpawnRpc : IRpcCommand
    {
        /// <summary>Monotonic id for host queue + RPC dedupe.</summary>
        public uint Sequence;

        /// <summary>World spawn position (XZ plane).</summary>
        public float3 SpawnPosition;

        /// <summary>
        /// Baked destination at spawn time (ship hull or planet surface).
        /// Clients fly toward this even if ship/planet lookups fail — prevents instant despawn.
        /// </summary>
        public float3 TargetPosition;

        /// <summary>Initial planar velocity.</summary>
        public float3 Velocity;

        /// <summary>Cruise speed for magnet steering.</summary>
        public float CruiseSpeed;

        /// <summary>Population amount (drives visual scale).</summary>
        public float Amount;

        /// <summary>Load destination ship network id (0 for unload).</summary>
        public int TargetShipNetworkId;

        /// <summary>Source planet id (load / fallback).</summary>
        public int SourcePlanetId;

        /// <summary>Unload destination planet id.</summary>
        public int TargetPlanetId;

        /// <summary>1 = planet→ship load, 0 = ship→planet unload.</summary>
        public byte IsLoad;

        /// <summary>Owning team as byte.</summary>
        public byte Team;
    }

    /// <summary>
    /// [NETCODE] Server → all clients: authoritative people-transport pose / end-of-life.
    /// Server sim + bullets own the entity; clients only mirror this for VFX (no PeopleTransportGhost).
    /// Wire size ~32 bytes — must match Linux headless layout.
    /// </summary>
    public struct PeopleTransportPoseRpc : IRpcCommand
    {
        /// <summary>Same id as <see cref="PeopleTransportSpawnRpc.Sequence"/>.</summary>
        public uint Sequence;

        /// <summary>Server logical XZ position this tick.</summary>
        public float3 Position;

        /// <summary>Server planar velocity (client dead-reckons between pose RPCs).</summary>
        public float3 Velocity;

        /// <summary>
        /// <see cref="PeopleTransportPoseStatus"/> — Active / Consumed / Destroyed.
        /// </summary>
        public byte Status;
    }

    /// <summary>
    /// [NETCODE] Server → all clients: spawn a cosmetic bullet tracer (muzzle + in-flight).
    /// Bullets are not ghosts — <see cref="BulletSimulationSystem"/> owns damage; clients draw via
    /// <see cref="BulletVfxBridge"/> / <c>BulletVfxDriver</c>. Wire layout must match Linux headless.
    /// </summary>
    public struct BulletSpawnRpc : IRpcCommand
    {
        /// <summary>Monotonic shot id (host bridge + RPC dedupe).</summary>
        public uint Sequence;

        /// <summary>Muzzle world position at fire time (logical / unbounded XZ).</summary>
        public float3 SpawnPosition;

        /// <summary>Initial planar velocity (includes ship velocity).</summary>
        public float3 Velocity;

        /// <summary>Tracer lifetime in seconds.</summary>
        public float Lifetime;

        /// <summary>Max travel distance before cosmetic despawn.</summary>
        public float MaxDistance;

        /// <summary>Display damage (impact VFX intensity; server owns real damage).</summary>
        public float Damage;

        /// <summary>Shooter team as byte.</summary>
        public byte OwnerTeam;

        /// <summary>Shooter network id (anticipation adopt / local mute).</summary>
        public int OwnerNetworkId;

        /// <summary>Index into <c>BulletVfxBank</c> categories.</summary>
        public int BankIndex;

        /// <summary>Per-shot visual scale multiplier.</summary>
        public float ScaleMultiplier;

        /// <summary>
        /// Weapon mount index for this shot (0-based). Local clients reproject
        /// the tracer onto the matching live barrel so multi-cannon volleys do not snap to muzzle 0.
        /// Use <c>-1</c> for non-weapon origins (drone swarm) so VFX keep server SpawnPosition.
        /// </summary>
        public int MountIndex;

        /// <summary>
        /// [TITAN-ORBIT] Collision filter for cosmetic prediction (mining vs rocks, fighters vs ships).
        /// Must match server <see cref="BulletElement.DamageFilter"/>.
        /// </summary>
        public byte DamageFilter;

        /// <summary>1 = homing rocket tracer (client steers with the same turn cap).</summary>
        public byte Homing;

        /// <summary>Max yaw rate in degrees per second for homing tracers.</summary>
        public float TurnSpeedDeg;

        /// <summary>Toroidal search radius. 0 is sanitized to a positive default (never whole-map).</summary>
        public float AcquireRange;
    }

    /// <summary>
    /// [NETCODE] Server → all clients: impact VFX when an authoritative bullet hits.
    /// Wire layout must match Linux headless.
    /// </summary>
    public struct BulletHitRpc : IRpcCommand
    {
        /// <summary>Same id as <see cref="BulletSpawnRpc.Sequence"/>. 0 = ram/grind (no tracer).</summary>
        public uint Sequence;

        /// <summary>World hit position (logical / unbounded XZ).</summary>
        public float3 HitPosition;

        /// <summary>Damage for impact VFX intensity.</summary>
        public float Damage;

        /// <summary>Shooter team as byte.</summary>
        public byte OwnerTeam;

        /// <summary>Index into <c>BulletVfxBank</c> categories.</summary>
        public int BankIndex;

        /// <summary>Per-shot visual scale multiplier.</summary>
        public float ScaleMultiplier;

        /// <summary>
        /// Asteroid <see cref="AsteroidState.Health"/> after this hit, or &lt; 0 when the impact
        /// was not an asteroid (planet / ship / moon / transport / planetary defense).
        /// [TITAN-ORBIT] Clients must not guess HP Left from lagging ghost Health — use this.
        /// 0 means the rock was killed this hit (hide proxy immediately).
        /// </summary>
        public float AsteroidHealthAfter;

        /// <summary>
        /// Stable <see cref="PlanetState.PlanetId"/> when this hit damaged a planetary-defense
        /// turret slot; 0 when the impact was not PD.
        /// <para>
        /// [TITAN-ORBIT] Live turret HP is <b>this field</b>, applied on the client by
        /// <see cref="PlanetaryDefenseClientHealthSync"/> — the same HitRpc channel asteroids
        /// use. Planet ghost <see cref="PlanetaryDefenseSlotElement.Health"/> is layout seed
        /// only; it is not the combat HP stream.
        /// </para>
        /// </summary>
        public int PlanetaryDefensePlanetId;

        /// <summary>
        /// Slot index in the planet’s <see cref="PlanetaryDefenseSlotElement"/> buffer when
        /// <see cref="PlanetaryDefensePlanetId"/> &gt; 0; ignored otherwise.
        /// </summary>
        public byte PlanetaryDefenseSlotIndex;

        /// <summary>
        /// Turret slot Health after this hit when <see cref="PlanetaryDefensePlanetId"/> &gt; 0;
        /// 0 means the turret was destroyed this hit (slot reset to empty). Use &lt; 0 only when
        /// not a PD impact (PlanetId already 0 — field unused).
        /// </summary>
        public float PlanetaryDefenseHealthAfter;

        /// <summary>
        /// Shooter NetworkId for orphan-tracer reconcile when Sequence was never bound.
        /// 0 on ram/grind (Sequence 0).
        /// </summary>
        public int OwnerNetworkId;

        /// <summary>
        /// Weapon mount that fired this shot (−1 when unknown / non-weapon).
        /// Lets clients destroy the matching anticipation without a 12u nearest-fallback.
        /// </summary>
        public int MountIndex;
    }

    /// <summary>
    /// [NETCODE] Server → all clients: planet ownership flipped (capture or starting claim).
    /// Planet ghosts use low Importance / MaxSendRate under MaxSendChunks caps, so territory
    /// lines would lag several seconds on ghost snapshots alone. Clients apply this immediately
    /// (optimistic) and rebuild the connection graph / minimap without waiting for the ghost.
    /// Wire layout must match Linux headless.
    /// </summary>
    public struct PlanetOwnershipChangedRpc : IRpcCommand
    {
        /// <summary>Stable <see cref="PlanetState.PlanetId"/>.</summary>
        public int PlanetId;

        /// <summary>New owning team as byte (<see cref="TeamId"/>).</summary>
        public byte Team;

        /// <summary>Population after the flip (0 on capture).</summary>
        public int Population;

        /// <summary>Planet level at flip time (fingerprint / bonuses).</summary>
        public int PlanetLevel;

        /// <summary>
        /// Player who delivered the most troops during this capture (0 = starting claim / unknown).
        /// Immediate client label — do not wait on the rate-limited planet ghost.
        /// </summary>
        public int TopContributorNetworkId;
    }

    /// <summary>
    /// [NETCODE] Server → all clients: an asteroid was destroyed (bullet / mine / ram).
    /// Asteroids are not ghost-relevant under seed-hydrate — clients must destroy their local
    /// body immediately. HitRpc alone is not enough (mining/ram have no HitRpc; surface hits
    /// can miss a fixed MatchRadius). Wire layout must match Linux headless.
    /// </summary>
    public struct AsteroidDestroyedRpc : IRpcCommand
    {
        /// <summary>World position of the destroyed rock (Y forced to 0 on apply).</summary>
        public float3 Position;

        /// <summary>
        /// Uniform LocalTransform scale at destroy time — clients match with
        /// <c>AsteroidHitRadius(scale)</c> so large rocks are not missed.
        /// </summary>
        public float Scale;
    }

    /// <summary>
    /// [NETCODE] Server → all clients: an asteroid respawned after destroy.
    /// Asteroids are not ghost-relevant under seed-hydrate join — clients spawn a local body.
    /// Wire layout must match Linux headless.
    /// </summary>
    public struct AsteroidRespawnRpc : IRpcCommand
    {
        /// <summary>World position (Y forced to 0 on apply).</summary>
        public float3 Position;

        /// <summary>Uniform LocalTransform scale.</summary>
        public float Scale;

        /// <summary>Full gem capacity.</summary>
        public float GemValue;

        /// <summary>Full Health.</summary>
        public float MaxHealth;

        /// <summary>Designer Size for bounce mass.</summary>
        public float Size;
    }

    /// <summary>
    /// [NETCODE] Server → joining client: which seed-layout asteroid slots are currently alive.
    /// Bit i = 1 means the rock at blueprint asteroid index i still exists (or has respawned).
    /// Late joiners seed-hydrate t=0 then SoftDestroy dead slots. 16 ulongs cover 1024 rocks.
    /// </summary>
    public struct AsteroidOccupancyRpc : IRpcCommand
    {
        /// <summary>Match seed — ignore if it does not match the latched recipe.</summary>
        public uint MatchSeed;

        /// <summary>How many asteroid slots the bitmask describes (bits beyond this are unused).</summary>
        public int SlotCount;

        /// <summary>
        /// Occupancy words: slot i is alive when bit (i % 64) in word (i / 64) is 1.
        /// 16 ulongs = 1024 slots. IRpcCommand cannot carry a NativeArray, so the mask is flattened.
        /// </summary>
        public ulong Bits0;
        /// <summary>Occupancy word 1 (slots 64–127). See <see cref="Bits0"/>.</summary>
        public ulong Bits1;
        /// <summary>Occupancy word 2 (slots 128–191). See <see cref="Bits0"/>.</summary>
        public ulong Bits2;
        /// <summary>Occupancy word 3 (slots 192–255). See <see cref="Bits0"/>.</summary>
        public ulong Bits3;
        /// <summary>Occupancy word 4 (slots 256–319). See <see cref="Bits0"/>.</summary>
        public ulong Bits4;
        /// <summary>Occupancy word 5 (slots 320–383). See <see cref="Bits0"/>.</summary>
        public ulong Bits5;
        /// <summary>Occupancy word 6 (slots 384–447). See <see cref="Bits0"/>.</summary>
        public ulong Bits6;
        /// <summary>Occupancy word 7 (slots 448–511). See <see cref="Bits0"/>.</summary>
        public ulong Bits7;
        /// <summary>Occupancy word 8 (slots 512–575). See <see cref="Bits0"/>.</summary>
        public ulong Bits8;
        /// <summary>Occupancy word 9 (slots 576–639). See <see cref="Bits0"/>.</summary>
        public ulong Bits9;
        /// <summary>Occupancy word 10 (slots 640–703). See <see cref="Bits0"/>.</summary>
        public ulong Bits10;
        /// <summary>Occupancy word 11 (slots 704–767). See <see cref="Bits0"/>.</summary>
        public ulong Bits11;
        /// <summary>Occupancy word 12 (slots 768–831). See <see cref="Bits0"/>.</summary>
        public ulong Bits12;
        /// <summary>Occupancy word 13 (slots 832–895). See <see cref="Bits0"/>.</summary>
        public ulong Bits13;
        /// <summary>Occupancy word 14 (slots 896–959). See <see cref="Bits0"/>.</summary>
        public ulong Bits14;
        /// <summary>Occupancy word 15 (slots 960–1023). See <see cref="Bits0"/>.</summary>
        public ulong Bits15;
    }

    /// <summary>Server connection tag: occupancy RPC already queued for this joiner.</summary>
    public struct AsteroidOccupancySent : IComponentData { }

    /// <summary>Server connection tag: in-flight people-transport SpawnRpcs dumped once.</summary>
    public struct PeopleTransportCatchUpSent : IComponentData { }
}
