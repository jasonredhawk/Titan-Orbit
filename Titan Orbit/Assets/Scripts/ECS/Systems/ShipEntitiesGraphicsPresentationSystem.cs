using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client-only Entities Graphics presentation for ships. Spawns local child entities with
    /// <see cref="MaterialMeshInfo"/> parented to the ship ghost (bank pivot → mesh parts).
    /// [TITAN-ORBIT] Display pose smoothing for the local owner lives in <c>ShipVisualSyncSystem</c>;
    /// this system only builds/tears down mesh entities. Hierarchy destroy copies <see cref="Child"/>
    /// ids before <c>DestroyEntity</c> (basics41 hitch from invalidated buffers).
    /// Runs in <see cref="PresentationSystemGroup"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class ShipEntitiesGraphicsPresentationSystem : SystemBase
    {
        readonly List<Entity> _partsToDestroy = new List<Entity>(64);
        readonly HashSet<Entity> _aliveShips = new HashSet<Entity>();
        readonly List<Entity> _shipsToClearVisuals = new List<Entity>(16);
        readonly List<PendingVisualResync> _shipsToResync = new List<PendingVisualResync>(16);

        EntityQuery _visualPartsQuery;
        EntityQuery _bankPivotQuery;

        struct PendingVisualResync
        {
            public Entity ShipEntity;
            public int BranchIndex;
        }

        protected override void OnCreate()
        {
            _visualPartsQuery = GetEntityQuery(ComponentType.ReadOnly<ShipVisualPartTag>());
            _bankPivotQuery = GetEntityQuery(ComponentType.ReadOnly<ShipVisualBankPivotTag>());
        }

        protected override void OnUpdate()
        {
            if (!TitanOrbitPresentationConfig.UseEntitiesGraphicsForShips)
                return;

            var catalog = ShipChassisVisualCatalog.Instance;
            if (catalog == null)
                return;

            _aliveShips.Clear();
            _shipsToClearVisuals.Clear();
            _shipsToResync.Clear();

            // --- Pass 1: decide which ships need visual work ---
            foreach (var (ship, loadout, entity) in SystemAPI
                         .Query<RefRO<ShipState>, RefRO<ShipLoadoutState>>()
                         .WithAll<ShipTag, LocalTransform>()
                         .WithEntityAccess())
            {
                CollectShipVisualWork(entity, ship.ValueRO, loadout.ValueRO.BranchIndex, catalog);
            }

            foreach (var (ship, entity) in SystemAPI
                         .Query<RefRO<ShipState>>()
                         .WithAll<ShipTag, LocalTransform>()
                         .WithNone<ShipLoadoutState>()
                         .WithEntityAccess())
            {
                CollectShipVisualWork(entity, ship.ValueRO, branchIndex: 0, catalog);
            }

            // --- Pass 2: structural changes outside entity queries ---
            for (int i = 0; i < _shipsToClearVisuals.Count; i++)
                DestroyVisualPartsForShip(_shipsToClearVisuals[i]);

            for (int i = 0; i < _shipsToResync.Count; i++)
            {
                var work = _shipsToResync[i];
                if (!EntityManager.Exists(work.ShipEntity) || !EntityManager.HasComponent<ShipState>(work.ShipEntity))
                    continue;

                var ship = EntityManager.GetComponentData<ShipState>(work.ShipEntity);
                ApplyShipVisualResync(work.ShipEntity, ship, work.BranchIndex, catalog);
            }

            CleanupOrphanVisualParts();
        }

        void CollectShipVisualWork(
            Entity shipEntity,
            in ShipState ship,
            int branchIndex,
            ShipChassisVisualCatalog catalog)
        {
            _aliveShips.Add(shipEntity);

            // --- Hide owned hull until team/resume confirm ---
            // [TITAN-ORBIT] Persisted GhostOwner ships replicate during map load. Until
            // TeamChoiceConfirmed, do not build Entities Graphics parts for "my" NetworkId —
            // otherwise the player sees their ship before Join Team / rejoin UI.
            if (ship.IsDead || ship.AwaitingTeamSelection || IsSuppressedLocalOwnedShip(shipEntity))
            {
                _shipsToClearVisuals.Add(shipEntity);
                return;
            }

            if (!ShipStatApplyLogic.TryResolveChassisId(ship.Team, ship.ShipLevel, branchIndex, out string chassisId))
                return;

            if (!catalog.TryGetEntry(chassisId, out var entry) || entry.RenderParts == null || entry.RenderParts.Count == 0)
                return;

            if (EntityManager.HasComponent<ShipClientVisualState>(shipEntity))
            {
                var applied = EntityManager.GetComponentData<ShipClientVisualState>(shipEntity);
                var chassisKey = new FixedString64Bytes(chassisId);
                if (applied.ChassisId.Equals(chassisKey)
                    && applied.AppliedShipLevel == ship.ShipLevel
                    && applied.AppliedBranchIndex == branchIndex
                    && applied.AppliedTeam == ship.Team)
                {
                    return;
                }
            }

            _shipsToResync.Add(new PendingVisualResync
            {
                ShipEntity = shipEntity,
                BranchIndex = branchIndex,
            });
        }

        void ApplyShipVisualResync(
            Entity shipEntity,
            in ShipState ship,
            int branchIndex,
            ShipChassisVisualCatalog catalog)
        {
            if (!ShipStatApplyLogic.TryResolveChassisId(ship.Team, ship.ShipLevel, branchIndex, out string chassisId))
                return;

            if (!catalog.TryGetEntry(chassisId, out var entry) || entry.RenderParts == null || entry.RenderParts.Count == 0)
                return;

            DestroyVisualPartsForShip(shipEntity);
            Entity bankPivot = CreateBankPivot(shipEntity);
            SpawnVisualParts(bankPivot, shipEntity, entry);

            var visualState = new ShipClientVisualState
            {
                ChassisId = new FixedString64Bytes(chassisId),
                AppliedShipLevel = ship.ShipLevel,
                AppliedBranchIndex = branchIndex,
                AppliedTeam = ship.Team,
            };

            if (EntityManager.HasComponent<ShipClientVisualState>(shipEntity))
                EntityManager.SetComponentData(shipEntity, visualState);
            else
                EntityManager.AddComponentData(shipEntity, visualState);
        }

        /// <summary>
        /// True when this ghost is owned by the local NetworkId (or GhostOwnerIsLocal) and team
        /// flow has not confirmed Join Team / resume yet.
        /// </summary>
        bool IsSuppressedLocalOwnedShip(Entity shipEntity)
        {
            if (!ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return false;

            // --- GhostOwnerIsLocal (NetCode enableable) ---
            if (EntityManager.HasComponent<GhostOwnerIsLocal>(shipEntity) &&
                EntityManager.IsComponentEnabled<GhostOwnerIsLocal>(shipEntity))
                return true;

            // --- GhostOwner.NetworkId match ---
            if (!EntityManager.HasComponent<GhostOwner>(shipEntity))
                return false;

            int localId = GetLocalNetworkId();
            if (localId <= 0)
                return false;

            return EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId == localId;
        }

        /// <summary>Reads this client's NetworkId from the in-game connection.</summary>
        int GetLocalNetworkId()
        {
            foreach (var netId in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>())
                return netId.ValueRO.Value;
            return -1;
        }

        /// <summary>
        /// Bank pivot parented directly to the ship ghost. Mesh parts parent to this pivot.
        /// </summary>
        Entity CreateBankPivot(Entity shipEntity)
        {
            EnsureShipHasLocalToWorld(shipEntity);

            var pivot = EntityManager.CreateEntity();
            EntityManager.AddComponentData(pivot, new Parent { Value = shipEntity });
            AddHierarchyTransform(pivot, LocalTransform.FromPositionRotation(float3.zero, quaternion.identity));
            EntityManager.AddComponentData(pivot, new ShipVisualBankPivotTag { ShipEntity = shipEntity });
            EntityManager.AddComponentData(pivot, new ShipVisualBankState());
            return pivot;
        }

        void EnsureShipHasLocalToWorld(Entity shipEntity)
        {
            if (!EntityManager.HasComponent<LocalTransform>(shipEntity))
                return;

            if (EntityManager.HasComponent<LocalToWorld>(shipEntity))
                return;

            var shipTransform = EntityManager.GetComponentData<LocalTransform>(shipEntity);
            EntityManager.AddComponentData(shipEntity, new LocalToWorld { Value = shipTransform.ToMatrix() });
        }

        void AddHierarchyTransform(Entity entity, in LocalTransform localTransform)
        {
            EntityManager.AddComponentData(entity, localTransform);
            EntityManager.AddComponentData(entity, new LocalToWorld { Value = localTransform.ToMatrix() });
        }

        void SpawnVisualParts(Entity parentEntity, Entity shipEntity, ShipChassisVisualEntry entry)
        {
            var renderDescription = new RenderMeshDescription(
                ShadowCastingMode.On,
                receiveShadows: true);

            for (int i = 0; i < entry.RenderParts.Count; i++)
            {
                var part = entry.RenderParts[i];
                if (part.Mesh == null || part.Material == null)
                    continue;

                var child = EntityManager.CreateEntity();
                EntityManager.AddComponentData(child, new Parent { Value = parentEntity });
                AddPartLocalTransform(child, part);
                EntityManager.AddComponentData(child, new ShipVisualPartTag { ShipEntity = shipEntity });

                var renderMeshArray = new RenderMeshArray(new[] { part.Material }, new[] { part.Mesh });
                var materialMeshInfo = MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0);
                RenderMeshUtility.AddComponents(
                    child,
                    EntityManager,
                    in renderDescription,
                    renderMeshArray,
                    materialMeshInfo);
            }
        }

        void AddPartLocalTransform(Entity child, ShipChassisRenderPart part)
        {
            float3 scale = part.LocalScale;
            bool isUniformScale = math.abs(scale.x - scale.y) < 1e-4f && math.abs(scale.y - scale.z) < 1e-4f;

            if (isUniformScale)
            {
                var localTransform = LocalTransform.FromPositionRotationScale(
                    part.LocalPosition,
                    part.LocalRotation,
                    scale.x);
                AddHierarchyTransform(child, localTransform);
                return;
            }

            var transform = LocalTransform.FromPositionRotation(
                part.LocalPosition,
                part.LocalRotation);
            AddHierarchyTransform(child, transform);
            EntityManager.AddComponentData(child, new PostTransformMatrix
            {
                Value = float4x4.Scale(scale),
            });

            if (EntityManager.HasComponent<LocalToWorld>(child))
            {
                var localToWorld = EntityManager.GetComponentData<LocalToWorld>(child);
                localToWorld.Value = math.mul(transform.ToMatrix(), float4x4.Scale(scale));
                EntityManager.SetComponentData(child, localToWorld);
            }
        }

        void CollectVisualPartsToDestroy(Func<ShipVisualPartTag, bool> predicate)
        {
            _partsToDestroy.Clear();
            using var entities = _visualPartsQuery.ToEntityArray(Allocator.Temp);
            using var tags = _visualPartsQuery.ToComponentDataArray<ShipVisualPartTag>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                if (predicate(tags[i]))
                    _partsToDestroy.Add(entities[i]);
            }
        }

        void DestroyVisualPartsForShip(Entity shipEntity)
        {
            DestroyBankPivotForShip(shipEntity);

            CollectVisualPartsToDestroy(tag => tag.ShipEntity == shipEntity);
            for (int i = 0; i < _partsToDestroy.Count; i++)
                EntityManager.DestroyEntity(_partsToDestroy[i]);

            if (EntityManager.HasComponent<ShipClientVisualState>(shipEntity))
                EntityManager.RemoveComponent<ShipClientVisualState>(shipEntity);
        }

        void DestroyBankPivotForShip(Entity shipEntity)
        {
            using var pivots = _bankPivotQuery.ToEntityArray(Allocator.Temp);
            using var tags = _bankPivotQuery.ToComponentDataArray<ShipVisualBankPivotTag>(Allocator.Temp);

            for (int i = 0; i < pivots.Length; i++)
            {
                if (tags[i].ShipEntity != shipEntity)
                    continue;

                DestroyEntityHierarchy(pivots[i]);
            }
        }

        /// <summary>
        /// Destroys <paramref name="root"/> and all <see cref="Child"/> descendants.
        /// Copies the child list first — destroying a child invalidates the parent's
        /// <see cref="Child"/> buffer (basics41: ObjectDisposedException / hitch spam).
        /// </summary>
        void DestroyEntityHierarchy(Entity root)
        {
            if (!EntityManager.Exists(root))
                return;

            // --- Collect deepest-first, then destroy (no live buffer iteration across DestroyEntity) ---
            var destroyOrder = new NativeList<Entity>(16, Allocator.Temp);
            CollectDestroyOrderDepthFirst(root, ref destroyOrder);
            for (int i = 0; i < destroyOrder.Length; i++)
            {
                if (EntityManager.Exists(destroyOrder[i]))
                    EntityManager.DestroyEntity(destroyOrder[i]);
            }

            destroyOrder.Dispose();
        }

        /// <summary>Walks the Child hierarchy and appends entities leaves-first, then parents.</summary>
        void CollectDestroyOrderDepthFirst(Entity root, ref NativeList<Entity> destroyOrder)
        {
            if (!EntityManager.Exists(root))
                return;

            if (EntityManager.HasBuffer<Child>(root))
            {
                var children = EntityManager.GetBuffer<Child>(root);
                int childCount = children.Length;
                // Snapshot child entities before any recursive destroy mutates structural data.
                var childSnapshot = new NativeArray<Entity>(childCount, Allocator.Temp);
                for (int i = 0; i < childCount; i++)
                    childSnapshot[i] = children[i].Value;

                for (int i = 0; i < childCount; i++)
                    CollectDestroyOrderDepthFirst(childSnapshot[i], ref destroyOrder);

                childSnapshot.Dispose();
            }

            destroyOrder.Add(root);
        }

        void CleanupOrphanVisualParts()
        {
            CollectVisualPartsToDestroy(tag =>
            {
                var ship = tag.ShipEntity;
                return !EntityManager.Exists(ship) || !_aliveShips.Contains(ship);
            });

            for (int i = 0; i < _partsToDestroy.Count; i++)
                EntityManager.DestroyEntity(_partsToDestroy[i]);

            using var pivots = _bankPivotQuery.ToEntityArray(Allocator.Temp);
            using var pivotTags = _bankPivotQuery.ToComponentDataArray<ShipVisualBankPivotTag>(Allocator.Temp);
            for (int i = 0; i < pivots.Length; i++)
            {
                var ship = pivotTags[i].ShipEntity;
                if (!EntityManager.Exists(ship) || !_aliveShips.Contains(ship))
                    DestroyEntityHierarchy(pivots[i]);
            }
        }
    }
}
