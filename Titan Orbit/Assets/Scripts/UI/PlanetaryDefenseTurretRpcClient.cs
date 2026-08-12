using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Client glue for taking control of a planetary defense turret.
    /// Local Host writes the ServerWorld directly (SendRpc on server never becomes
    /// <see cref="ReceiveRpcCommandRequest"/>); dedicated online clients send
    /// <see cref="EnterPlanetaryDefenseTurretCommand"/>.
    /// </summary>
    public static class PlanetaryDefenseTurretRpcClient
    {
        /// <summary>
        /// Requests enter for the given planet / slot. No-op when ids are invalid.
        /// </summary>
        /// <param name="planetId">Stable <see cref="PlanetState.PlanetId"/>.</param>
        /// <param name="slotIndex">0-based defense slot index.</param>
        public static void RequestEnterTurret(int planetId, byte slotIndex)
        {
            if (planetId <= 0)
                return;

            // --- Local Host: apply on ServerWorld immediately ---
            if (!TitanOrbitSessionManager.IsDedicatedOnlineClient)
                TryEnterOnServerWorld(planetId, slotIndex);

            // Dedicated online: RPC to remote authority. Local Host already wrote server.
            if (EcsGameBridge.IsLocalHost() || !EcsGameBridge.IsNetworkInGame())
                return;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new EnterPlanetaryDefenseTurretCommand
            {
                PlanetId = planetId,
                SlotIndex = slotIndex,
            });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>Direct enter on Local Host ServerWorld.</summary>
        static void TryEnterOnServerWorld(int planetId, byte slotIndex)
        {
            var world = EcsGameBridge.ServerWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!em.CreateEntityQuery(typeof(MapStateSingleton))
                    .TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;

            int networkId = EcsGameBridge.GetLocalNetworkId();
            if (networkId <= 0)
                return;

            var familyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            var defaultConfig = PlanetaryDefenseConfig.LoadDefault();
            PlanetaryDefenseTurretControlSystem.TryEnterForNetworkId(
                em, networkId, planetId, slotIndex,
                map.MapWidth, map.MapHeight, familyConfig, defaultConfig);
        }
    }
}
