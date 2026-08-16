using TitanOrbit.ECS;
using TitanOrbit.Game;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Client glue for MEGA gun Take Control / kick / lock.
    /// Local Host writes ServerWorld directly; dedicated clients send RPCs.
    /// </summary>
    public static class MegaShipGunRpcClient
    {
        /// <summary>Requests Take Control of one MEGA mount.</summary>
        public static void RequestEnter(int megaOwnerNetworkId, byte mountIndex)
        {
            if (megaOwnerNetworkId <= 0)
                return;

            if (!TitanOrbit.NetCode.TitanOrbitSessionManager.IsDedicatedOnlineClient
                && EcsGameBridge.ServerWorld != null && EcsGameBridge.ServerWorld.IsCreated)
            {
                MegaShipGunControlSystem.TryEnterForNetworkId(
                    EcsGameBridge.ServerWorld.EntityManager,
                    EcsGameBridge.GetLocalNetworkId(),
                    megaOwnerNetworkId,
                    mountIndex);
            }

            if (EcsGameBridge.IsLocalHost() || !EcsGameBridge.IsNetworkInGame())
                return;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new EnterMegaGunCommand
            {
                MegaOwnerNetworkId = megaOwnerNetworkId,
                MountIndex = mountIndex,
            });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>MEGA owner kicks one mount (255 = all).</summary>
        public static void RequestKick(byte mountIndex)
        {
            if (!TitanOrbit.NetCode.TitanOrbitSessionManager.IsDedicatedOnlineClient
                && EcsGameBridge.ServerWorld != null && EcsGameBridge.ServerWorld.IsCreated)
            {
                MegaShipGunControlSystem.TryKickForNetworkId(
                    EcsGameBridge.ServerWorld.EntityManager,
                    EcsGameBridge.GetLocalNetworkId(),
                    mountIndex);
            }

            if (EcsGameBridge.IsLocalHost() || !EcsGameBridge.IsNetworkInGame())
                return;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new KickMegaGunnerCommand { MountIndex = mountIndex });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>MEGA owner locks or unlocks all gun pads.</summary>
        public static void RequestSetLocked(bool locked)
        {
            if (!TitanOrbit.NetCode.TitanOrbitSessionManager.IsDedicatedOnlineClient
                && EcsGameBridge.ServerWorld != null && EcsGameBridge.ServerWorld.IsCreated)
            {
                MegaShipGunControlSystem.TrySetLockedForNetworkId(
                    EcsGameBridge.ServerWorld.EntityManager,
                    EcsGameBridge.GetLocalNetworkId(),
                    locked);
            }

            if (EcsGameBridge.IsLocalHost() || !EcsGameBridge.IsNetworkInGame())
                return;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new SetMegaGunsLockedCommand { Locked = locked });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }
    }
}
