using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;
using Material = Unity.Physics.Material;
using PhysicsColliderBlob = Unity.Physics.Collider;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Tracks which chassis visual prefab last built the ship's <see cref="PhysicsCollider"/>.
    /// Compared each frame by <see cref="ShipHullColliderSyncSystem"/> so upgrades rebuild hull shape.
    /// </summary>
    public struct ShipHullColliderState : IComponentData
    {
        public FixedString64Bytes ChassisId;
        public int AppliedShipLevel;
        public int AppliedBranchIndex;
    }

    /// <summary>
    /// Tracks a runtime-built hull collider blob on this ship so we never dispose baked SubScene
    /// colliders when chassis upgrades replace <see cref="PhysicsCollider"/>.
    /// </summary>
    public struct ShipRuntimeHullColliderBlob : ICleanupComponentData
    {
        public BlobAssetReference<PhysicsColliderBlob> Value;
    }

    /// <summary>
    /// Builds Unity Physics compound colliders from USC chassis prefab colliders (per-component
    /// Box/Mesh/etc.) and applies them to ship ghost entities. Visual proxies intentionally strip
    /// colliders — authoritative hull shape lives only on the ECS entity.
    /// </summary>
    public static class ShipHullColliderLogic
    {
        static readonly Material HullMaterial = CreateHullMaterial();

        /// <summary>
        /// Replaces the ship's physics collider with a compound built from the chassis prefab.
        /// Falls back to the existing collider when the prefab has no usable collider sources.
        /// <para>
        /// [TITAN-ORBIT] Bake uses level-1 <see cref="BodyCollisionMath.ShipPresentationScale"/> only.
        /// Whole-hull tier growth lives on <c>LocalTransform.Scale</c> (+10%/level via
        /// <see cref="BodyCollisionMath.GetShipTierScale"/>) so PhysX / visual / muzzle stay aligned
        /// without rebuilding a different mesh density per tier.
        /// </para>
        /// </summary>
        public static bool TryApplyChassisCollider(
            EntityManager em,
            Entity shipEntity,
            GameObject chassisPrefab,
            float motorMass)
        {
            if (chassisPrefab == null || !em.Exists(shipEntity))
                return false;

            // Level-1 presentation bake — tier size is LocalTransform.Scale (see ShipStatApplyLogic).
            float presentationScale = BodyCollisionMath.ShipPresentationScale;
            if (!TryBuildCompoundCollider(chassisPrefab, presentationScale, out var compound))
                return false;

            ReplacePhysicsCollider(em, shipEntity, compound, motorMass);
            return true;
        }

        static Material CreateHullMaterial()
        {
            var material = Material.Default;
            material.Restitution = 0.15f;
            material.Friction = 0.05f;
            return material;
        }

        static bool TryBuildCompoundCollider(
            GameObject chassisPrefab,
            float presentationScale,
            out BlobAssetReference<PhysicsColliderBlob> compound)
        {
            compound = default;
            var instances = new List<CompoundCollider.ColliderBlobInstance>(16);
            GameObject instance = null;

            try
            {
                instance = Object.Instantiate(chassisPrefab);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                var root = instance.transform;

                foreach (var collider in instance.GetComponentsInChildren<UnityEngine.Collider>(true))
                {
                    if (collider == null || !collider.enabled || collider.isTrigger)
                        continue;

                    if (TryCreateChildCollider(collider, root, presentationScale, out var childBlob, out var childPose))
                    {
                        instances.Add(new CompoundCollider.ColliderBlobInstance
                        {
                            Collider = childBlob,
                            CompoundFromChild = childPose,
                        });
                    }
                }

                if (instances.Count == 0)
                    AppendRendererFallbackBoxes(root, presentationScale, instances);

                if (instances.Count == 0)
                    return false;

                if (instances.Count == 1)
                {
                    compound = instances[0].Collider;
                    return compound.IsCreated;
                }

                var native = new NativeArray<CompoundCollider.ColliderBlobInstance>(instances.Count, Allocator.Temp);
                for (int i = 0; i < instances.Count; i++)
                    native[i] = instances[i];

                compound = CompoundCollider.Create(native);
                native.Dispose();
                return compound.IsCreated;
            }
            finally
            {
                if (instance != null)
                    Object.Destroy(instance);
            }
        }

        /// <summary>
        /// When designers have not added colliders yet, approximate each renderer with a box.
        /// </summary>
        static void AppendRendererFallbackBoxes(
            Transform root,
            float presentationScale,
            List<CompoundCollider.ColliderBlobInstance> instances)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;

                Bounds bounds = renderer.bounds;
                if (bounds.size.sqrMagnitude < 1e-6f)
                    continue;

                Vector3 localCenter = root.InverseTransformPoint(bounds.center) * presentationScale;
                Vector3 localSize = bounds.size * presentationScale;
                if (localSize.x < 0.01f || localSize.y < 0.01f || localSize.z < 0.01f)
                    continue;

                var geometry = new BoxGeometry
                {
                    Center = (float3)localCenter,
                    Size = (float3)localSize,
                    Orientation = quaternion.identity,
                };

                var blob = Unity.Physics.BoxCollider.Create(geometry, TitanOrbitPhysicsLayers.Ship, HullMaterial);
                if (!blob.IsCreated)
                    continue;

                instances.Add(new CompoundCollider.ColliderBlobInstance
                {
                    Collider = blob,
                    CompoundFromChild = RigidTransform.identity,
                });
            }
        }

        static bool TryCreateChildCollider(
            UnityEngine.Collider unityCollider,
            Transform root,
            float presentationScale,
            out BlobAssetReference<PhysicsColliderBlob> blob,
            out RigidTransform childPose)
        {
            blob = default;
            childPose = RigidTransform.identity;

            Matrix4x4 relative = root.worldToLocalMatrix * unityCollider.transform.localToWorldMatrix;
            DecomposeMatrix(relative, presentationScale, out float3 position, out quaternion orientation, out float3 lossyScale);

            switch (unityCollider)
            {
                case UnityEngine.BoxCollider box:
                {
                    float3 size = math.abs((float3)box.size * lossyScale) * presentationScale;
                    if (math.any(size < 0.001f))
                        return false;

                    blob = Unity.Physics.BoxCollider.Create(
                        new BoxGeometry
                        {
                            Center = float3.zero,
                            Size = size,
                            Orientation = quaternion.identity,
                        },
                        TitanOrbitPhysicsLayers.Ship,
                        HullMaterial);
                    childPose = new RigidTransform(orientation, position + math.mul(orientation, (float3)box.center * lossyScale * presentationScale));
                    break;
                }
                case UnityEngine.SphereCollider sphere:
                {
                    float radius = math.max(0.001f, sphere.radius * math.cmax(lossyScale) * presentationScale);
                    blob = Unity.Physics.SphereCollider.Create(
                        new SphereGeometry { Center = float3.zero, Radius = radius },
                        TitanOrbitPhysicsLayers.Ship,
                        HullMaterial);
                    childPose = new RigidTransform(orientation, position + math.mul(orientation, (float3)sphere.center * lossyScale * presentationScale));
                    break;
                }
                case UnityEngine.CapsuleCollider capsule:
                {
                    float radius = math.max(0.001f, capsule.radius * math.max(lossyScale.x, lossyScale.z) * presentationScale);
                    float height = math.max(radius * 2f, capsule.height * lossyScale.y * presentationScale);
                    int direction = math.clamp(capsule.direction, 0, 2);
                    quaternion capsuleOrientation = direction switch
                    {
                        0 => math.mul(orientation, quaternion.Euler(math.radians(0f), 0f, math.radians(90f))),
                        2 => math.mul(orientation, quaternion.Euler(math.radians(90f), 0f, 0f)),
                        _ => orientation,
                    };
                    blob = Unity.Physics.CapsuleCollider.Create(
                        new CapsuleGeometry
                        {
                            Vertex0 = new float3(0f, height * 0.5f - radius, 0f),
                            Vertex1 = new float3(0f, -height * 0.5f + radius, 0f),
                            Radius = radius,
                        },
                        TitanOrbitPhysicsLayers.Ship,
                        HullMaterial);
                    childPose = new RigidTransform(capsuleOrientation, position + math.mul(orientation, (float3)capsule.center * lossyScale * presentationScale));
                    break;
                }
                case UnityEngine.MeshCollider meshCollider:
                {
                    if (meshCollider.sharedMesh == null || !meshCollider.convex)
                        return false;

                    blob = Unity.Physics.MeshCollider.Create(
                        meshCollider.sharedMesh,
                        TitanOrbitPhysicsLayers.Ship,
                        HullMaterial);
                    childPose = new RigidTransform(orientation, position);
                    break;
                }
                default:
                    return false;
            }

            return blob.IsCreated;
        }

        static void DecomposeMatrix(
            Matrix4x4 matrix,
            float presentationScale,
            out float3 position,
            out quaternion orientation,
            out float3 lossyScale)
        {
            float3x3 rotScale = new float3x3((float4x4)matrix);
            lossyScale = new float3(
                math.length(rotScale.c0),
                math.length(rotScale.c1),
                math.length(rotScale.c2));
            lossyScale = math.max(lossyScale, new float3(1e-6f));

            rotScale.c0 /= lossyScale.x;
            rotScale.c1 /= lossyScale.y;
            rotScale.c2 /= lossyScale.z;

            orientation = new quaternion(rotScale);
            position = (float3)matrix.GetPosition() * presentationScale;
            lossyScale *= presentationScale;
        }

        static void ReplacePhysicsCollider(EntityManager em, Entity entity, BlobAssetReference<PhysicsColliderBlob> newCollider, float motorMass)
        {
            // --- Release previous runtime hull only ---
            // [UNITY PHYSICS] Baked ghost colliders are deserialized/shared — never call Dispose on them.
            if (em.HasComponent<ShipRuntimeHullColliderBlob>(entity))
            {
                var owned = em.GetComponentData<ShipRuntimeHullColliderBlob>(entity);
                if (owned.Value.IsCreated && owned.Value != newCollider)
                    owned.Value.Dispose();
            }

            if (em.HasComponent<PhysicsCollider>(entity))
                em.SetComponentData(entity, new PhysicsCollider { Value = newCollider });
            else
                em.AddComponentData(entity, new PhysicsCollider { Value = newCollider });

            var runtimeBlob = new ShipRuntimeHullColliderBlob { Value = newCollider };
            if (em.HasComponent<ShipRuntimeHullColliderBlob>(entity))
                em.SetComponentData(entity, runtimeBlob);
            else
                em.AddComponentData(entity, runtimeBlob);

            if (!em.HasComponent<PhysicsMass>(entity))
                return;

            float mass = math.max(0.5f, motorMass);
            var physicsMass = PhysicsMass.CreateDynamic(newCollider.Value.MassProperties, mass);
            em.SetComponentData(entity, physicsMass);
        }
    }

    /// <summary>
    /// Disposes runtime ship hull collider blobs when the physics collider is removed or the world shuts down.
    /// Paired with <see cref="ShipHullColliderLogic.ReplacePhysicsCollider"/>.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipRuntimeHullColliderCleanupSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (blob, entity) in SystemAPI
                         .Query<RefRW<ShipRuntimeHullColliderBlob>>()
                         .WithNone<PhysicsCollider>()
                         .WithEntityAccess())
            {
                if (blob.ValueRO.Value.IsCreated)
                    blob.ValueRW.Value.Dispose();

                ecb.RemoveComponent<ShipRuntimeHullColliderBlob>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        public void OnDestroy(ref SystemState state)
        {
            foreach (var blob in SystemAPI.Query<RefRW<ShipRuntimeHullColliderBlob>>())
            {
                if (blob.ValueRO.Value.IsCreated)
                    blob.ValueRW.Value.Dispose();
            }
        }
    }
}
