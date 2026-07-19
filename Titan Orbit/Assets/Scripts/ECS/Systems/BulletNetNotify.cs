using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server → client notify helpers for bullet VFX (spawn + hit).
    /// Mirrors <see cref="PeopleTransportNetNotify"/>: in-process bridge for host + broadcast RPC.
    /// </summary>
    public static class BulletNetNotify
    {
        /// <summary>
        /// Broadcasts a spawn and mirrors into the host VFX bridge when ClientWorld exists.
        /// </summary>
        public static void SendSpawn(
            ref EntityCommandBuffer ecb,
            in BulletElement bullet)
        {
            if (bullet.Sequence == 0)
                return;

            float3 spawnPos = bullet.Position;
            spawnPos.y = 0f;
            float3 velocity = bullet.Velocity;
            velocity.y = 0f;

            var req = new BulletVfxBridge.SpawnRequest
            {
                Sequence = bullet.Sequence,
                SpawnPosition = spawnPos,
                Velocity = velocity,
                Lifetime = bullet.Lifetime,
                MaxDistance = bullet.MaxDistance,
                Damage = bullet.Damage,
                OwnerTeam = bullet.OwnerTeam,
                OwnerNetworkId = bullet.OwnerNetworkId,
                BankIndex = bullet.BankIndex,
                ScaleMultiplier = bullet.ScaleMultiplier > 0f ? bullet.ScaleMultiplier : 1f,
                IsAnticipation = false,
                IsDisplaySpace = false,
            };

            // --- Host in-process (Editor / listen-server) ---
            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                BulletVfxBridge.TryEnqueueSpawn(req);

            // --- All remote clients (+ host client connection) ---
            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new BulletSpawnRpc
            {
                Sequence = bullet.Sequence,
                SpawnPosition = spawnPos,
                Velocity = velocity,
                Lifetime = bullet.Lifetime,
                MaxDistance = bullet.MaxDistance,
                Damage = bullet.Damage,
                OwnerTeam = bullet.OwnerTeam,
                OwnerNetworkId = bullet.OwnerNetworkId,
                BankIndex = bullet.BankIndex,
                ScaleMultiplier = bullet.ScaleMultiplier > 0f ? bullet.ScaleMultiplier : 1f,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>
        /// Broadcasts an impact and mirrors into the host VFX bridge when ClientWorld exists.
        /// </summary>
        public static void SendHit(
            ref EntityCommandBuffer ecb,
            in BulletElement bullet,
            float3 hitPosition)
        {
            if (bullet.Sequence == 0)
                return;

            hitPosition.y = 0f;
            var req = new BulletVfxBridge.HitRequest
            {
                Sequence = bullet.Sequence,
                HitPosition = hitPosition,
                Damage = bullet.Damage,
                OwnerTeam = bullet.OwnerTeam,
                BankIndex = bullet.BankIndex,
                ScaleMultiplier = bullet.ScaleMultiplier > 0f ? bullet.ScaleMultiplier : 1f,
            };

            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                BulletVfxBridge.EnqueueHit(req);

            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new BulletHitRpc
            {
                Sequence = bullet.Sequence,
                HitPosition = hitPosition,
                Damage = bullet.Damage,
                OwnerTeam = bullet.OwnerTeam,
                BankIndex = bullet.BankIndex,
                ScaleMultiplier = bullet.ScaleMultiplier > 0f ? bullet.ScaleMultiplier : 1f,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }
    }
}
