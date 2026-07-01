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
}
