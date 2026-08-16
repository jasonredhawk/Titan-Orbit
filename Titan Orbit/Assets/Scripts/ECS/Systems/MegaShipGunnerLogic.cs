using TitanOrbit.Core;
using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server helpers for MEGA gun pads: enter, exit, kick, lock.
    /// Friendlies approach a mount, Take Control, and aim+fire that barrel.
    /// The MEGA owner pilots the hull and may kick or lock pads.
    /// Paired with <see cref="ShipMegaGunControlState"/> and planetary turret possession.
    /// </summary>
    public static class MegaShipGunnerLogic
    {
        /// <summary>How close a friendly must be (toroidal XZ) to a mount to Take Control.</summary>
        public const float EnterRadius = 6f;

        /// <summary>
        /// True when this EntityManager is a client world and Instantiates/join gates forbid gathers.
        /// Server worlds always return false so enter/kick/eject cannot be blocked by the client latch.
        /// </summary>
        static bool ShouldRefuseClientGathers(EntityManager em)
        {
            var world = em.World;
            if (world != null && world.IsServer())
                return false;

            return ClientJoinSettleCache.ShouldSkipShipEntityQueries ||
                   ClientJoinSettleCache.ShouldSkipMapBodyQueries;
        }

        /// <summary>
        /// True when this ship ghost is stowed on a MEGA gun pad.
        /// Used by motor freeze, nameplates, rockets/mines, and hybrid hull hide.
        /// </summary>
        public static bool IsControllingMegaGun(EntityManager em, Entity shipEntity)
        {
            return shipEntity != Entity.Null
                   && em.Exists(shipEntity)
                   && em.HasComponent<ShipMegaGunControlState>(shipEntity)
                   && em.GetComponentData<ShipMegaGunControlState>(shipEntity).IsControlling;
        }

        /// <summary>
        /// Finds the closest enterable MEGA mount for a friendly ship. Returns false when
        /// guns are locked, the hull is hostile, or no free pad is in range.
        /// </summary>
        public static bool TryFindClosestEnterableMount(
            EntityManager em,
            Entity gunnerShip,
            float3 gunnerPos,
            float mapW,
            float mapH,
            out Entity megaEntity,
            out byte mountIndex)
        {
            using var query = em.CreateEntityQuery(
                typeof(ShipTag), typeof(MegaShipState), typeof(LocalTransform), typeof(ShipWeaponMountElement));
            return TryFindClosestEnterableMount(
                em, query, gunnerShip, gunnerPos, mapW, mapH, out megaEntity, out mountIndex);
        }

        /// <summary>
        /// Same as <see cref="TryFindClosestEnterableMount(EntityManager, Entity, float3, float, float, out Entity, out byte)"/>
        /// but reuses a cached query (client HUD must not CreateEntityQuery every LateUpdate).
        /// </summary>
        public static bool TryFindClosestEnterableMount(
            EntityManager em,
            EntityQuery megaQuery,
            Entity gunnerShip,
            float3 gunnerPos,
            float mapW,
            float mapH,
            out Entity megaEntity,
            out byte mountIndex)
        {
            megaEntity = Entity.Null;
            mountIndex = 0;
            if (ShouldRefuseClientGathers(em))
                return false;
            if (!em.HasComponent<ShipState>(gunnerShip))
                return false;

            var gunner = em.GetComponentData<ShipState>(gunnerShip);
            if (gunner.Team == TeamId.None || gunner.IsDead)
                return false;

            int gunnerNet = 0;
            if (em.HasComponent<GhostOwner>(gunnerShip))
                gunnerNet = em.GetComponentData<GhostOwner>(gunnerShip).NetworkId;

            float best = float.MaxValue;
            using var entities = megaQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                Entity mega = entities[i];
                if (mega == gunnerShip)
                    continue;
                if (!em.HasComponent<MegaShipState>(mega))
                    continue;

                var megaState = em.GetComponentData<MegaShipState>(mega);
                if (!megaState.IsMega || megaState.GunsLocked)
                    continue;

                var megaShip = em.GetComponentData<ShipState>(mega);
                if (megaShip.Team != gunner.Team || megaShip.IsDead)
                    continue;

                int megaOwnerNet = 0;
                if (em.HasComponent<GhostOwner>(mega))
                    megaOwnerNet = em.GetComponentData<GhostOwner>(mega).NetworkId;
                if (megaOwnerNet > 0 && megaOwnerNet == gunnerNet)
                    continue;

                if (!em.HasBuffer<ShipWeaponMountElement>(mega) || !em.HasBuffer<MegaShipGunnerSlotElement>(mega))
                    continue;

                var xf = em.GetComponentData<LocalTransform>(mega);
                if (ToroidalMapEcs.ToroidalDistance(gunnerPos, xf.Position, mapW, mapH) > EnterRadius + 14f)
                    continue;

                var mounts = em.GetBuffer<ShipWeaponMountElement>(mega);
                var gunners = em.GetBuffer<MegaShipGunnerSlotElement>(mega);

                int count = math.min(mounts.Length, gunners.Length);
                for (int m = 0; m < count; m++)
                {
                    if (gunners[m].OccupiedByNetworkId != 0)
                        continue;

                    float3 world = GetMountWorldPosition(xf, mounts[m]);
                    float dist = ToroidalMapEcs.ToroidalDistance(gunnerPos, world, mapW, mapH);
                    if (dist > EnterRadius || dist >= best)
                        continue;

                    best = dist;
                    megaEntity = mega;
                    mountIndex = (byte)m;
                }
            }

            return megaEntity != Entity.Null;
        }

        /// <summary>
        /// World position of a weapon mount on a live MEGA transform.
        /// MEGA visuals already live at <see cref="LocalTransform.Scale"/> — do not also
        /// multiply by <see cref="TitanOrbit.Simulation.BodyCollisionMath.ShipPresentationScale"/>
        /// (that path is for regular hybrid hulls and parks MEGA muzzles inside the mesh).
        /// </summary>
        public static float3 GetMountWorldPosition(in LocalTransform xf, in ShipWeaponMountElement mount)
        {
            float3 local = mount.LocalPosition * math.max(0.01f, xf.Scale);
            return xf.Position + math.rotate(xf.Rotation, local);
        }

        /// <summary>Stows the gunner and occupies the mount. Returns false on validation failure.</summary>
        public static bool TryEnter(
            EntityManager em,
            Entity gunnerShip,
            Entity megaEntity,
            byte mountIndex)
        {
            if (!em.HasComponent<MegaShipState>(megaEntity) || !em.HasComponent<ShipState>(gunnerShip))
                return false;

            var mega = em.GetComponentData<MegaShipState>(megaEntity);
            if (!mega.IsMega || mega.GunsLocked)
                return false;

            var megaShip = em.GetComponentData<ShipState>(megaEntity);
            var gunner = em.GetComponentData<ShipState>(gunnerShip);
            if (megaShip.Team != gunner.Team || megaShip.IsDead || gunner.IsDead)
                return false;

            if (!em.HasBuffer<MegaShipGunnerSlotElement>(megaEntity))
                return false;

            var gunners = em.GetBuffer<MegaShipGunnerSlotElement>(megaEntity);
            if (mountIndex >= gunners.Length)
                return false;
            if (gunners[mountIndex].OccupiedByNetworkId != 0)
                return false;

            int networkId = 0;
            if (em.HasComponent<GhostOwner>(gunnerShip))
                networkId = em.GetComponentData<GhostOwner>(gunnerShip).NetworkId;
            if (networkId <= 0)
                return false;

            int megaOwnerNet = 0;
            if (em.HasComponent<GhostOwner>(megaEntity))
                megaOwnerNet = em.GetComponentData<GhostOwner>(megaEntity).NetworkId;

            var slot = gunners[mountIndex];
            slot.OccupiedByNetworkId = networkId;
            gunners[mountIndex] = slot;

            if (em.HasComponent<ShipMegaGunControlState>(gunnerShip))
            {
                em.SetComponentData(gunnerShip, new ShipMegaGunControlState
                {
                    IsControlling = true,
                    MegaOwnerNetworkId = megaOwnerNet,
                    MountIndex = mountIndex,
                });
            }

            return true;
        }

        /// <summary>Releases a gunner from a MEGA mount (exit, kick, or MEGA death).</summary>
        public static void Exit(EntityManager em, Entity gunnerShip)
        {
            if (!em.HasComponent<ShipMegaGunControlState>(gunnerShip))
                return;

            var control = em.GetComponentData<ShipMegaGunControlState>(gunnerShip);
            if (!control.IsControlling)
                return;

            if (TryFindMegaByOwnerNetworkId(em, control.MegaOwnerNetworkId, out Entity mega)
                && em.HasBuffer<MegaShipGunnerSlotElement>(mega))
            {
                var gunners = em.GetBuffer<MegaShipGunnerSlotElement>(mega);
                int networkId = 0;
                if (em.HasComponent<GhostOwner>(gunnerShip))
                    networkId = em.GetComponentData<GhostOwner>(gunnerShip).NetworkId;

                if (control.MountIndex < gunners.Length
                    && gunners[control.MountIndex].OccupiedByNetworkId == networkId)
                {
                    var slot = gunners[control.MountIndex];
                    slot.OccupiedByNetworkId = 0;
                    gunners[control.MountIndex] = slot;
                }
            }

            em.SetComponentData(gunnerShip, new ShipMegaGunControlState());
        }

        /// <summary>Owner kicks one mount (or all when mountIndex is 255).</summary>
        public static void Kick(EntityManager em, Entity megaEntity, byte mountIndex)
        {
            if (!em.HasBuffer<MegaShipGunnerSlotElement>(megaEntity))
                return;

            var gunners = em.GetBuffer<MegaShipGunnerSlotElement>(megaEntity);
            if (mountIndex == 255)
            {
                for (int i = 0; i < gunners.Length; i++)
                    KickSlot(em, gunners, i);
                return;
            }

            if (mountIndex < gunners.Length)
                KickSlot(em, gunners, mountIndex);
        }

        /// <summary>Ejects every gunner on this MEGA (death / hull restore).</summary>
        public static void EjectAllGunners(EntityManager em, Entity megaEntity)
        {
            Kick(em, megaEntity, 255);
        }

        /// <summary>Finds the MEGA ship owned by <paramref name="networkId"/>.</summary>
        public static bool TryFindMegaByOwnerNetworkId(EntityManager em, int networkId, out Entity megaEntity)
        {
            megaEntity = Entity.Null;
            if (networkId <= 0)
                return false;
            if (ShouldRefuseClientGathers(em))
                return false;

            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner), typeof(MegaShipState));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                if (!em.GetComponentData<MegaShipState>(entities[i]).IsMega)
                    continue;
                megaEntity = entities[i];
                return true;
            }

            return false;
        }

        static void KickSlot(EntityManager em, DynamicBuffer<MegaShipGunnerSlotElement> gunners, int index)
        {
            int occupant = gunners[index].OccupiedByNetworkId;
            var slot = gunners[index];
            slot.OccupiedByNetworkId = 0;
            gunners[index] = slot;
            if (occupant <= 0)
                return;

            if (TryFindShipByNetworkId(em, occupant, out Entity gunner)
                && em.HasComponent<ShipMegaGunControlState>(gunner))
            {
                em.SetComponentData(gunner, new ShipMegaGunControlState());
            }
        }

        static bool TryFindShipByNetworkId(EntityManager em, int networkId, out Entity ship)
        {
            ship = Entity.Null;
            if (ShouldRefuseClientGathers(em))
                return false;
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
