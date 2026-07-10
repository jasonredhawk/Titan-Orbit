using Unity.Collections;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// RPC commands (Remote Procedure Calls) sent from client to server or server to client.
    /// IRpcCommand — NetCode interface for one-shot network messages outside ghost replication.
    /// Ship-related commands handle team pick, upgrades, rejoin, and gem deposit toggles.
    /// Handlers live in server systems (TeamManagementSystem, RejoinShipManagementSystem, etc.).
    /// </summary>

    /// <summary>Client requests a team assignment at spawn. Handled by TeamManagementSystem.</summary>
    public struct RequestTeamCommand : IRpcCommand
    {
        public int NetworkId;
        public byte RequestedTeam;
    }

    /// <summary>Server confirms or rejects team choice; client reads this in TeamChoiceResultClientSystem.</summary>
    public struct TeamChoiceResultRpc : IRpcCommand
    {
        public int NetworkId;
        public byte AssignedTeam;
        public byte Success;
        public FixedString128Bytes Message;
    }

    public struct SetPlayerNameCommand : IRpcCommand
    {
        public FixedString64Bytes DisplayName;
    }

    public struct RequestContributedGemsCommand : IRpcCommand
    {
        public int HomePlanetId;
    }

    public struct ContributedGemsResultRpc : IRpcCommand
    {
        public float Amount;
    }

    /// <summary>Client toggles auto-deposit gems while docked at a moon.</summary>
    public struct SetWantDepositGemsCommand : IRpcCommand
    {
        public bool WantDeposit;
    }

    /// <summary>
    /// Client purchases a ship upgrade at an orbit station store. Server validates gems and level.
    /// </summary>
    public struct PurchaseShipUpgradeCommand : IRpcCommand
    {
        public int StorePlanetId;
        public int TargetLevel;
        public int TargetBranchIndex;
    }

    public struct PurchaseStoreItemCommand : IRpcCommand
    {
        public int HomePlanetId;
        public int ItemType;
    }

    public struct OrbitStoreResultRpc : IRpcCommand
    {
        public byte Success;
        public FixedString128Bytes Message;
    }

    /// <summary>Client buys a stat attribute upgrade (speed, health, etc.) from HUD.</summary>
    public struct PurchaseAttributeUpgradeCommand : IRpcCommand
    {
        public int AttributeIndex;
    }

    /// <summary>
    /// Client reconnected to a match that still has their ship — resume control without re-picking team.
    /// Handled by RejoinShipManagementSystem.
    /// </summary>
    public struct ResumeExistingShipCommand : IRpcCommand { }

    /// <summary>
    /// Client wants a new ship and team; server destroys the persisted ship and clears CommandTarget.
    /// Handled by RejoinShipManagementSystem.
    /// </summary>
    public struct AbandonShipForRejoinCommand : IRpcCommand { }

    /// <summary>Server response to resume/abandon rejoin choice. Handled by RejoinShipResultClientSystem.</summary>
    public struct RejoinShipResultRpc : IRpcCommand
    {
        public byte Success;
        /// <summary>1 = resume existing, 2 = abandon for fresh team pick.</summary>
        public byte Choice;
        public byte AssignedTeam;
        public FixedString128Bytes Message;
    }
}
