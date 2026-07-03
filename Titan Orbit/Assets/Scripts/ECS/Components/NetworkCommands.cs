using Unity.Collections;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>RPC-style command from client to server for team selection.</summary>
    public struct RequestTeamCommand : IRpcCommand
    {
        public int NetworkId;
        public byte RequestedTeam;
    }

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

    public struct SetWantDepositGemsCommand : IRpcCommand
    {
        public bool WantDeposit;
    }

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

    public struct PurchaseAttributeUpgradeCommand : IRpcCommand
    {
        public int AttributeIndex;
    }
}
