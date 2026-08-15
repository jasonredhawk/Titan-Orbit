using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server → client notify for mine explosions. Mirrors <see cref="BulletNetNotify"/>:
    /// in-process bridge for host + broadcast <see cref="MineExplodeRpc"/>.
    /// </summary>
    public static class MineNetNotify
    {
        /// <summary>
        /// Broadcasts a mine burst and mirrors into the host VFX bridge when ClientWorld exists.
        /// </summary>
        public static void SendExplode(ref EntityCommandBuffer ecb, in DeployedMineElement mine)
        {
            float3 pos = mine.Position;
            pos.y = 0f;

            var req = new MineExplosionBridge.Request
            {
                Sequence = mine.Sequence,
                Position = pos,
                OwnerTeam = mine.OwnerTeam,
                ItemLevel = mine.ItemLevel,
                VisualScale = mine.VisualScale > 0.01f ? mine.VisualScale : 1f,
                ExplosionVfxScale = mine.ExplosionVfxScale > 0f ? mine.ExplosionVfxScale : 2f,
                Damage = mine.Damage,
            };

            // --- Host in-process (Editor / listen-server) ---
            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                MineExplosionBridge.Enqueue(req);

            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new MineExplodeRpc
            {
                Sequence = mine.Sequence,
                Position = pos,
                OwnerTeam = mine.OwnerTeam,
                ItemLevel = mine.ItemLevel,
                VisualScale = req.VisualScale,
                ExplosionVfxScale = req.ExplosionVfxScale,
                Damage = mine.Damage,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }
    }
}
