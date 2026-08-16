using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: enter MEGA gun pads via RPC, exit on RMB thrust, kick/lock from the owner,
    /// and force-eject when the MEGA dies. Local Host also calls the public Try* helpers
    /// because SendRpc on ServerWorld never becomes ReceiveRpcCommandRequest.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class MegaShipGunControlSystem : SystemBase
    {
        /// <summary>Need map size for enter-range checks.</summary>
        protected override void OnCreate()
        {
            RequireForUpdate<MapStateSingleton>();
        }

        /// <summary>Process enter / kick / lock RPCs and thrust exits.</summary>
        protected override void OnUpdate()
        {
            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;

            var em = EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<EnterMegaGunCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(em, req.ValueRO.SourceConnection);
                TryEnterForNetworkId(em, networkId, cmd.ValueRO.MegaOwnerNetworkId, cmd.ValueRO.MountIndex);
                ecb.DestroyEntity(entity);
            }

            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<KickMegaGunnerCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(em, req.ValueRO.SourceConnection);
                TryKickForNetworkId(em, networkId, cmd.ValueRO.MountIndex);
                ecb.DestroyEntity(entity);
            }

            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<SetMegaGunsLockedCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(em, req.ValueRO.SourceConnection);
                TrySetLockedForNetworkId(em, networkId, cmd.ValueRO.Locked);
                ecb.DestroyEntity(entity);
            }

            foreach (var (control, input, shipEntity) in SystemAPI
                         .Query<RefRO<ShipMegaGunControlState>, RefRO<ShipInput>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!control.ValueRO.IsControlling)
                    continue;

                bool megaGone = !MegaShipGunnerLogic.TryFindMegaByOwnerNetworkId(
                    em, control.ValueRO.MegaOwnerNetworkId, out Entity mega)
                    || !em.GetComponentData<MegaShipState>(mega).IsMega
                    || em.GetComponentData<ShipState>(mega).IsDead;

                if (megaGone || input.ValueRO.Thrust)
                    MegaShipGunnerLogic.Exit(em, shipEntity);
                else if (em.HasComponent<Unity.Physics.PhysicsVelocity>(shipEntity))
                    em.SetComponentData(shipEntity, Unity.Physics.PhysicsVelocity.Zero);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>Local Host / RPC: occupy a MEGA mount if the sender is in range.</summary>
        public static bool TryEnterForNetworkId(
            EntityManager em,
            int networkId,
            int megaOwnerNetworkId,
            byte mountIndex)
        {
            if (networkId <= 0 || megaOwnerNetworkId <= 0)
                return false;
            if (!TryFindShipByNetworkId(em, networkId, out Entity gunner))
                return false;
            if (!MegaShipGunnerLogic.TryFindMegaByOwnerNetworkId(em, megaOwnerNetworkId, out Entity mega))
                return false;
            if (!em.HasComponent<LocalTransform>(gunner) || !em.HasComponent<LocalTransform>(mega))
                return false;
            if (!em.CreateEntityQuery(typeof(MapStateSingleton))
                    .TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return false;

            var gunnerXf = em.GetComponentData<LocalTransform>(gunner);
            var megaXf = em.GetComponentData<LocalTransform>(mega);
            if (!em.HasBuffer<ShipWeaponMountElement>(mega))
                return false;

            var mounts = em.GetBuffer<ShipWeaponMountElement>(mega);
            if (mountIndex >= mounts.Length)
                return false;

            float3 pad = MegaShipGunnerLogic.GetMountWorldPosition(megaXf, mounts[mountIndex]);
            float dist = ToroidalMapEcs.ToroidalDistance(
                gunnerXf.Position, pad, map.MapWidth, map.MapHeight);
            if (dist > MegaShipGunnerLogic.EnterRadius)
                return false;

            return MegaShipGunnerLogic.TryEnter(em, gunner, mega, mountIndex);
        }

        /// <summary>Owner kick (255 = all pads).</summary>
        public static bool TryKickForNetworkId(EntityManager em, int networkId, byte mountIndex)
        {
            if (!MegaShipGunnerLogic.TryFindMegaByOwnerNetworkId(em, networkId, out Entity mega))
                return false;
            MegaShipGunnerLogic.Kick(em, mega, mountIndex);
            return true;
        }

        /// <summary>Owner lock / unlock all pads. Lock also ejects current gunners.</summary>
        public static bool TrySetLockedForNetworkId(EntityManager em, int networkId, bool locked)
        {
            if (!MegaShipGunnerLogic.TryFindMegaByOwnerNetworkId(em, networkId, out Entity megaEntity))
                return false;
            if (!em.HasComponent<MegaShipState>(megaEntity))
                return false;

            var megaState = em.GetComponentData<MegaShipState>(megaEntity);
            megaState.GunsLocked = locked;
            em.SetComponentData(megaEntity, megaState);
            if (locked)
                MegaShipGunnerLogic.EjectAllGunners(em, megaEntity);
            return true;
        }

        static int GetSenderNetworkId(EntityManager em, Entity connection)
        {
            if (connection == Entity.Null || !em.HasComponent<NetworkId>(connection))
                return 0;
            return em.GetComponentData<NetworkId>(connection).Value;
        }

        static bool TryFindShipByNetworkId(EntityManager em, int networkId, out Entity ship)
        {
            ship = Entity.Null;
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                ship = entities[i];
                return true;
            }

            return false;
        }
    }
}
