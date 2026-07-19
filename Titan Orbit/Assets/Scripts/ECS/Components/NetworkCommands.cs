using Unity.Collections;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] RPC commands (Remote Procedure Calls) — one-shot network messages outside ghost
    /// replication. Each struct implements <c>IRpcCommand</c>; clients send requests, server systems
    /// validate and reply. Handlers: <see cref="TeamManagementSystem"/>,
    /// <see cref="RejoinShipManagementSystem"/>, moon orbit store systems, attribute upgrade systems.
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
    /// </summary>
    public struct TeamChoiceResultRpc : IRpcCommand
    {
        /// <summary>[NETCODE] Target player's network id.</summary>
        public int NetworkId;

        /// <summary>[TITAN-ORBIT] Team actually assigned (may differ if request was invalid).</summary>
        public byte AssignedTeam;

        /// <summary>[STANDARD] 1 = success, 0 = failure.</summary>
        public byte Success;

        /// <summary>[TITAN-ORBIT] Human-readable rejection or confirmation message for lobby UI.</summary>
        public FixedString128Bytes Message;
    }

    /// <summary>[NETCODE] Client sets display name shown in scoreboard and HUD.</summary>
    public struct SetPlayerNameCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] UTF-8 display name (length capped by FixedString64).</summary>
        public FixedString64Bytes DisplayName;
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
    /// [NETCODE] Client purchases a non-ship store item at a home planet moon store.
    /// </summary>
    public struct PurchaseStoreItemCommand : IRpcCommand
    {
        /// <summary>[TITAN-ORBIT] Home planet id hosting the store.</summary>
        public int HomePlanetId;

        /// <summary>[TITAN-ORBIT] Opaque item type id from store catalog.</summary>
        public int ItemType;
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
}
