using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Tag on client-only gem entities created from spawn / burst / catch-up RPCs.
    /// These are not NetCode ghosts.
    /// </summary>
    public struct ClientSeedHydratedGem : IComponentData { }

    /// <summary>
    /// Instantiates gem prefabs on the client without registering them as ghosts.
    /// Keys live crystals by <see cref="GemState.SpawnId"/> for consume / tractor / value RPCs.
    /// </summary>
    public static class ClientLocalGemSpawn
    {
        static readonly Dictionary<int, Entity> BySpawnId = new Dictionary<int, Entity>(128);
        static readonly HashSet<int> ConsumedSpawnIds = new HashSet<int>();
        static readonly Dictionary<int, GemTractorLockRpc> PendingLocks = new Dictionary<int, GemTractorLockRpc>(16);
        static readonly GemSpawnRecipe[] BurstScratch = new GemSpawnRecipe[GemExplosionMath.AbsoluteMaxGemCount];

        /// <summary>Removes a despawned gem from the SpawnId map.</summary>
        public static void Unregister(int spawnId)
        {
            if (spawnId != 0)
                BySpawnId.Remove(spawnId);
        }

        /// <summary>Clears the map (world teardown / disconnect).</summary>
        public static void Clear()
        {
            BySpawnId.Clear();
            ConsumedSpawnIds.Clear();
            PendingLocks.Clear();
        }

        /// <summary>Looks up a live client gem by recipe id.</summary>
        public static bool TryGet(int spawnId, out Entity entity)
        {
            if (spawnId != 0 && BySpawnId.TryGetValue(spawnId, out entity) && entity != Entity.Null)
                return true;

            entity = Entity.Null;
            return false;
        }

        /// <summary>Hydrates one gem from a spawn recipe. No-op when SpawnId is already live.</summary>
        public static Entity SpawnFromRecipe(
            EntityManager em,
            Entity gemPrefab,
            in GemSpawnRecipe recipe,
            float integrateElapsedSeconds = 0f)
        {
            if (gemPrefab == Entity.Null || recipe.Value <= 0f)
                return Entity.Null;

            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            if (!GemSpawnMath.TryResolve(recipe, settings, out var resolved))
                return Entity.Null;

            if (ConsumedSpawnIds.Contains(resolved.SpawnId))
                return Entity.Null;
            if (TryGet(resolved.SpawnId, out Entity existing))
                return existing;

            float3 pos = resolved.Position;
            quaternion rot = quaternion.identity;
            float3 vel = resolved.Velocity;
            float3 ang = resolved.AngularVelocity;
            byte phase = GemMotionState.PhaseCoast;
            if (integrateElapsedSeconds > 0.0001f)
            {
                bool haveMap = ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH);
                GemMotionLogic.IntegrateElapsed(
                    ref pos,
                    ref rot,
                    ref vel,
                    ref ang,
                    ref phase,
                    integrateElapsedSeconds,
                    settings.LinearDamping,
                    settings.AngularDamping,
                    settings.StopSpeedThreshold,
                    mapW,
                    mapH,
                    haveMap);
            }

            return InstantiateResolved(em, gemPrefab, resolved, pos, rot, vel, ang, phase);
        }

        /// <summary>Expands a <see cref="GemBurstRpc"/> and hydrates each crystal.</summary>
        public static void SpawnBurst(
            EntityManager em,
            Entity gemPrefab,
            float3 origin,
            float remainingValue,
            uint seed,
            float spawnServerTime,
            bool isBonus,
            float integrateElapsedSeconds = 0f)
        {
            origin.y = 0f;
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            int count = GemBurstExpansion.FillRecipes(
                origin, remainingValue, seed, spawnServerTime, isBonus, settings, BurstScratch);
            for (int i = 0; i < count; i++)
                SpawnFromRecipe(em, gemPrefab, BurstScratch[i], integrateElapsedSeconds);
        }

        /// <summary>Hydrates from a late-join snapshot (pose is already current).</summary>
        public static Entity SpawnFromCatchUp(EntityManager em, Entity gemPrefab, in GemCatchUpRpc rpc)
        {
            if (gemPrefab == Entity.Null || rpc.SpawnId == 0 || rpc.Value <= 0f)
                return Entity.Null;
            if (ConsumedSpawnIds.Contains(rpc.SpawnId))
                return Entity.Null;
            if (TryGet(rpc.SpawnId, out Entity existing))
                return existing;

            var resolved = new GemSpawnResolved
            {
                SpawnId = rpc.SpawnId,
                Position = rpc.Position,
                Scale = rpc.Size > 0.01f ? rpc.Size : math.clamp(math.sqrt(rpc.Value) * 0.2f, 0.2f, 0.5f),
                Velocity = rpc.Velocity,
                AngularVelocity = rpc.AngularVelocity,
                Value = rpc.Value,
                IsBonusGem = rpc.IsBonusGem != 0,
                BurstIndex = rpc.BurstIndex,
                SpawnServerTime = rpc.SpawnServerTime,
                ExcludePickupNetworkId = rpc.ExcludePickupNetworkId,
                ExcludePickupUntilServerTime = rpc.ExcludePickupUntilServerTime,
            };

            byte phase = rpc.Phase;
            Entity e = InstantiateResolved(
                em,
                gemPrefab,
                resolved,
                rpc.Position,
                quaternion.identity,
                rpc.Velocity,
                rpc.AngularVelocity,
                phase);

            if (e != Entity.Null && em.HasComponent<GemMotionState>(e))
            {
                var motion = em.GetComponentData<GemMotionState>(e);
                motion.Phase = phase;
                motion.TractorShipId = rpc.TractorShipId;
                motion.TractorWingIndex = rpc.TractorWingIndex;
                motion.TractorLockTick = rpc.TractorLockTick;
                motion.TractorExtendDuration = rpc.TractorExtendDuration;
                em.SetComponentData(e, motion);
            }

            return e;
        }

        /// <summary>Applies a leftover value after a partial scoop.</summary>
        public static void ApplyValueChanged(EntityManager em, int spawnId, float remainingValue)
        {
            if (!TryGet(spawnId, out Entity e) || !em.Exists(e))
                return;
            if (!em.HasComponent<GemState>(e) || !em.HasComponent<LocalTransform>(e))
                return;

            if (remainingValue <= 0.001f)
            {
                DestroyLocal(em, e, spawnId);
                return;
            }

            float scale = math.clamp(math.sqrt(remainingValue) * 0.2f, 0.2f, 0.5f);
            var gem = em.GetComponentData<GemState>(e);
            gem.Value = remainingValue;
            gem.Size = scale;
            em.SetComponentData(e, gem);

            var lt = em.GetComponentData<LocalTransform>(e);
            em.SetComponentData(e, LocalTransform.FromPositionRotationScale(lt.Position, lt.Rotation, scale));
        }

        /// <summary>Hides and destroys a scooped or expired local gem.</summary>
        public static void Consume(EntityManager em, int spawnId)
        {
            if (spawnId == 0)
                return;
            ConsumedSpawnIds.Add(spawnId);
            PendingLocks.Remove(spawnId);
            if (!TryGet(spawnId, out Entity e))
                return;
            DestroyLocal(em, e, spawnId);
        }

        /// <summary>Applies a tractor lock / unlock to a live local gem.</summary>
        public static void ApplyTractorLock(EntityManager em, in GemTractorLockRpc rpc)
        {
            if (rpc.SpawnId == 0)
                return;

            // Lock can beat spawn on the wire — keep it until the crystal hydrates.
            if (!TryGet(rpc.SpawnId, out Entity e) || !em.Exists(e))
            {
                if (rpc.TractorShipId == 0)
                    PendingLocks.Remove(rpc.SpawnId);
                else
                    PendingLocks[rpc.SpawnId] = rpc;
                return;
            }

            PendingLocks.Remove(rpc.SpawnId);
            if (!em.HasComponent<GemMotionState>(e))
                return;

            var motion = em.GetComponentData<GemMotionState>(e);
            motion.TractorShipId = rpc.TractorShipId;
            motion.TractorWingIndex = rpc.TractorWingIndex;
            motion.TractorLockTick = rpc.TractorLockTick;
            motion.TractorExtendDuration = rpc.TractorExtendDuration;
            if (rpc.TractorShipId == 0)
            {
                motion.Phase = GemMotionState.PhaseCoast;
            }
            else
            {
                motion.Phase = rpc.Phase;
            }

            em.SetComponentData(e, motion);
        }

        static Entity InstantiateResolved(
            EntityManager em,
            Entity gemPrefab,
            in GemSpawnResolved resolved,
            float3 position,
            quaternion rotation,
            float3 velocity,
            float3 angularVelocity,
            byte phase)
        {
            Entity e = em.Instantiate(gemPrefab);
            ClientLocalMapBodySpawn.StripGhostNetworking(em, e);

            if (!em.HasComponent<ClientSeedHydratedGem>(e))
                em.AddComponent<ClientSeedHydratedGem>(e);

            em.SetComponentData(e, LocalTransform.FromPositionRotationScale(position, rotation, resolved.Scale));
            em.SetComponentData(e, new GemState
            {
                SpawnId = resolved.SpawnId,
                Value = resolved.Value,
                Size = resolved.Scale,
                DepositTeam = TeamId.None,
                SpawnServerTime = resolved.SpawnServerTime,
                IsBonusGem = resolved.IsBonusGem,
                ExcludePickupNetworkId = resolved.ExcludePickupNetworkId,
                ExcludePickupUntilServerTime = resolved.ExcludePickupUntilServerTime,
                IsConsumed = false,
            });
            em.SetComponentData(e, new GemKinematics
            {
                Velocity = velocity,
                AngularVelocity = angularVelocity,
            });
            em.SetComponentData(e, new GemMotionState
            {
                Phase = phase,
                BurstIndex = resolved.BurstIndex,
                TractorShipId = 0,
                TractorWingIndex = 0,
                TractorLockTick = 0,
                TractorExtendDuration = 0f,
            });

            BySpawnId[resolved.SpawnId] = e;
            GemClientEntityRegistry.NotifyInstantiated(e);
            ClientLocalMapBodySpawn.QueueHybridVisual(em, e);
            if (PendingLocks.TryGetValue(resolved.SpawnId, out var pendingLock))
            {
                PendingLocks.Remove(resolved.SpawnId);
                ApplyTractorLock(em, pendingLock);
            }

            return e;
        }

        static void DestroyLocal(EntityManager em, Entity e, int spawnId)
        {
            Unregister(spawnId);
            GemClientEntityRegistry.NotifyDestroyed(e);
            if (em.Exists(e))
            {
                if (em.HasComponent<GemState>(e))
                {
                    var gem = em.GetComponentData<GemState>(e);
                    gem.IsConsumed = true;
                    em.SetComponentData(e, gem);
                }

                em.DestroyEntity(e);
            }
        }
    }
}
