using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Forces <see cref="RpcCollection.DynamicAssemblyList"/> on every simulation world
    /// before the first RPC handshake.
    /// <para>
    /// The Multiplayer Center sample sets this flag, and the dedicated server keeps that
    /// sample. WebGL drops every <c>Unity.Multiplayer.Center</c> system, so the browser
    /// client stayed at 0 while the server sent 1. NetCode then disconnected the Relay
    /// handshake (<c>InvalidRpc</c>) and the loading bar sat at the 20% warmup tail until
    /// the connect watch returned to Join Game.
    /// </para>
    /// <para>
    /// Both sides must send the same bit. With the flag on, WebGL and the Linux server
    /// can differ in registered assemblies; unknown RPC hashes are skipped in
    /// <c>RpcSystem</c> instead of dropping the connection.
    /// </para>
    /// World: ClientSimulation and ServerSimulation. Group: InitializationSystemGroup,
    /// created after <see cref="RpcSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    public partial struct TitanOrbitRpcDynamicAssemblyListSystem : ISystem
    {
        /// <summary>
        /// Sets the handshake bit, then disables this system. Must run in OnCreate,
        /// before <see cref="RpcSystem"/> finalizes the collection on its first update.
        /// </summary>
        /// <param name="state">This world's system state.</param>
        public void OnCreate(ref SystemState state)
        {
            SystemAPI.GetSingletonRW<RpcCollection>().ValueRW.DynamicAssemblyList = true;
            Debug.Log("[TitanOrbitRpc] DynamicAssemblyList=1 world=" + state.WorldUnmanaged.Name);
            state.Enabled = false;
        }
    }
}
