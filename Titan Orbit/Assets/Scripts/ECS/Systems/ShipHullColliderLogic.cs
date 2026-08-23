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
    /// Compared each frame by <see cref="ShipHullColliderSyncSystem"/> so chassis and
    /// bottom-bar attribute upgrades rebuild hull shape.
    /// </summary>
    public struct ShipHullColliderState : IComponentData
    {
        /// <summary>Last chassis id whose prefab was baked into PhysicsCollider.</summary>
        public FixedString64Bytes ChassisId;
        /// <summary>Last <see cref="ShipState.ShipLevel"/> used for chassis resolve.</summary>
        public int AppliedShipLevel;
        /// <summary>Last upgrade-tree branch index used for chassis resolve.</summary>
        public int AppliedBranchIndex;
        /// <summary>
        /// Sum of ghosted <see cref="ShipAttributeUpgradeState"/> levels at last bake.
        /// When this changes, part meshes and collider children must grow together.
        /// </summary>
        public int AppliedAttributeSum;
        /// <summary>
        /// Last <see cref="MegaShipCatalog.HullColliderRevision"/> baked for a MEGA.
        /// 0 on older hulls so the next catalog pass rebuilds from each part's authored colliders.
        /// </summary>
        public int AppliedMegaColliderRevision;
        /// <summary>
        /// Last <see cref="ShipHullColliderLogic.HullMaterialRevision"/> baked into this hull.
        /// Bump the constant when restitution/friction changes so live ships rebuild.
        /// </summary>
        public int AppliedHullMaterialRevision;
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
    /// Builds Unity Physics compound colliders from chassis prefab colliders (per-component
    /// Box/Mesh/etc.) and applies them to ship ghost entities. Visual proxies keep those
    /// UnityEngine colliders disabled for Inspector gizmos; the live hull is this ECS blob.
    /// </summary>
    public static class ShipHullColliderLogic
    {
        static readonly Material HullMaterial = CreateHullMaterial();

        /// <summary>
        /// Bump when hull material or solid envelope bake changes so
        /// <see cref="ShipHullColliderSyncSystem"/> rebuilds existing compounds.
        /// </summary>
        public const int HullMaterialRevision = 3;

        /// <summary>
        /// Replaces the ship's physics collider with a compound built from the chassis prefab.
        /// Falls back to the existing collider when the prefab has no usable collider sources.
        /// <para>
        /// [TITAN-ORBIT] Bake uses level-1 <see cref="BodyCollisionMath.ShipPresentationScale"/> only
        /// for the whole-hull presentation shrink. Tier growth lives on <c>LocalTransform.Scale</c>
        /// (+10%/level via <see cref="BodyCollisionMath.GetShipTierScale"/>).
        /// Bottom-bar attribute grow is applied to the temporary prefab hierarchy first
        /// (<see cref="ShipComponentAttributeScaleLogic.ApplyToHierarchy"/>) so child collider
        /// sizes/poses match the grown proxy meshes on server and client.
        /// </para>
        /// </summary>
        /// <param name="em">World EntityManager (server or client).</param>
        /// <param name="shipEntity">Ship ghost that owns PhysicsCollider.</param>
        /// <param name="chassisPrefab">Upgrade-tree hull prefab for this chassis id.</param>
        /// <param name="motorMass">Ship motor mass for PhysicsMass rebuild after collider swap.</param>
        /// <param name="attrs">
        /// Bottom-bar upgrade levels. Default (all zeros) = authored prefab size only.
        /// </param>
        /// <param name="familyPrefix">
        /// USC family token for part classification (e.g. AstroEagle). Empty → parsed from prefab name.
        /// </param>
        /// <summary>Overload without attribute grow — authored prefab size only.</summary>
        public static bool TryApplyChassisCollider(
            EntityManager em,
            Entity shipEntity,
            GameObject chassisPrefab,
            float motorMass)
        {
            var zeroAttrs = default(ShipAttributeUpgradeState);
            return TryApplyChassisCollider(
                em, shipEntity, chassisPrefab, motorMass, zeroAttrs, familyPrefix: null);
        }

        /// <summary>
        /// Rebuilds the hull collider with bottom-bar attribute part grow applied to the bake hierarchy.
        /// </summary>
        public static bool TryApplyChassisCollider(
            EntityManager em,
            Entity shipEntity,
            GameObject chassisPrefab,
            float motorMass,
            in ShipAttributeUpgradeState attrs,
            string familyPrefix)
        {
            if (chassisPrefab == null || !em.Exists(shipEntity))
                return false;

            // Level-1 presentation bake — tier size is LocalTransform.Scale (see ShipStatApplyLogic).
            float presentationScale = BodyCollisionMath.ShipPresentationScale;
            if (!TryBuildCompoundCollider(
                    chassisPrefab,
                    presentationScale,
                    attrs,
                    familyPrefix,
                    out var compound))
                return false;

            ReplacePhysicsCollider(em, shipEntity, compound, motorMass);
            return true;
        }

        /// <summary>
        /// MEGA hulls are nested StarSparrow module prefabs. Each module already has
        /// Collider / Collider2 / … boxes (and occasional capsules). Instantiate once so
        /// those nested colliders are visible, then bake them into the ghost PhysicsCollider.
        /// Do not invent a hull sphere or renderer AABB — walking the prefab asset sees
        /// stripped transforms and would fall back to the ghost-baked sphere.
        /// </summary>
        public static bool TryApplyMegaPartColliders(
            EntityManager em,
            Entity shipEntity,
            GameObject chassisPrefab,
            float motorMass)
        {
            if (chassisPrefab == null || !em.Exists(shipEntity))
                return false;

            float presentationScale = BodyCollisionMath.ShipPresentationScale;
            if (!TryBuildMegaPartCompound(chassisPrefab, presentationScale, out var compound))
                return false;

            ReplacePhysicsCollider(em, shipEntity, compound, motorMass);
            return true;
        }

        /// <summary>
        /// Instantiates the MEGA chassis so nested PrefabInstance colliders exist, then
        /// converts each enabled non-trigger UnityEngine collider. Presentation scale is
        /// applied once (DecomposeMatrix) so part boxes match the drawn hull.
        /// </summary>
        static bool TryBuildMegaPartCompound(
            GameObject chassisPrefab,
            float presentationScale,
            out BlobAssetReference<PhysicsColliderBlob> compound)
        {
            compound = default;
            var instances = new List<CompoundCollider.ColliderBlobInstance>(64);
            GameObject instance = null;

            try
            {
                // Nested module colliders are stripped on the prefab asset until Instantiate.
                instance = Object.Instantiate(chassisPrefab);
                instance.SetActive(false);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                Transform root = instance.transform;

                var colliders = root.GetComponentsInChildren<UnityEngine.Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    var collider = colliders[i];
                    if (collider == null || !collider.enabled || collider.isTrigger)
                        continue;

                    if (!TryCreateChildCollider(
                            collider, root, presentationScale,
                            out var childBlob, out var childPose,
                            scaleSizeByPresentation: false))
                        continue;

                    instances.Add(new CompoundCollider.ColliderBlobInstance
                    {
                        Collider = childBlob,
                        CompoundFromChild = childPose,
                    });
                }

                if (instances.Count == 0)
                    return false;

                AppendSolidHullEnvelope(instances);

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
        /// Unity Physics hull material. Ship↔ship bounce is the solver (restitution / friction),
        /// not a post-pass impulse. Collision events still fire for ramming damage.
        /// Asteroid materials keep restitution 0 so custom rock bounce is unchanged
        /// (GeometricMean with this value is still 0).
        /// </summary>
        public static Material CreateHullMaterial()
        {
            var material = Material.Default;
            material.CollisionResponse = CollisionResponsePolicy.CollideRaiseCollisionEvents;
            material.Restitution = 0.55f;
            material.Friction = 0.45f;
            material.RestitutionCombinePolicy = Material.CombinePolicy.GeometricMean;
            material.FrictionCombinePolicy = Material.CombinePolicy.Maximum;
            return material;
        }

        /// <summary>
        /// Instantiates the chassis, grows attribute-scale parts, then converts UnityEngine colliders
        /// into a Unity Physics compound blob at presentation scale.
        /// </summary>
        static bool TryBuildCompoundCollider(
            GameObject chassisPrefab,
            float presentationScale,
            in ShipAttributeUpgradeState attrs,
            string familyPrefix,
            out BlobAssetReference<PhysicsColliderBlob> compound)
        {
            compound = default;
            var instances = new List<CompoundCollider.ColliderBlobInstance>(16);
            GameObject instance = null;

            try
            {
                // Nested StarSparrow Collider / Collider2 boxes are stripped on the prefab
                // asset until Instantiate — same reason MEGA always clones. Walking the
                // asset left regular ships on the tiny ghost sphere.
                instance = Object.Instantiate(chassisPrefab);
                instance.SetActive(false);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                Transform root = instance.transform;

                if (ShipStatApplyLogic.SumAttributeLevels(attrs) > 0)
                {
                    string prefix = ResolveFamilyPrefix(chassisPrefab, familyPrefix);
                    ShipComponentAttributeScaleLogic.ApplyToHierarchy(
                        root,
                        prefix,
                        attrs,
                        territoryMovementMult: 1f);
                }

                foreach (var collider in root.GetComponentsInChildren<UnityEngine.Collider>(true))
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

                AppendSolidHullEnvelope(instances);

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
        /// Heuristic component mass from BoxCollider extents after attribute mesh grow, × ship-tier scale.
        /// Instantiates a temporary prefab hierarchy (destroyed before return). Falls back to 0 when
        /// the prefab has no usable boxes (caller may use legacy transform-scale mass).
        /// </summary>
        /// <param name="chassisPrefab">Upgrade-tree hull prefab.</param>
        /// <param name="attrs">Bottom-bar levels used by <see cref="ShipComponentAttributeScaleLogic"/>.</param>
        /// <param name="familyPrefix">USC family token (e.g. AstroEagle); empty → parse from prefab name.</param>
        /// <param name="shipLevel">Ship tier for <see cref="BodyCollisionMath.GetShipTierScale"/>.</param>
        /// <param name="applyAttributeScale">
        /// When false, skips attribute grow (level-1 / zero-ability reference mass).
        /// </param>
        public static float ComputeLiveHullComponentMass(
            GameObject chassisPrefab,
            in ShipAttributeUpgradeState attrs,
            string familyPrefix,
            int shipLevel,
            bool applyAttributeScale)
        {
            if (chassisPrefab == null)
                return 0f;

            GameObject instance = null;
            try
            {
                // --- Temp hierarchy (destroyed in finally) ---
                // [UNITY] Instantiate so we can mutate localScale without dirtying the asset prefab.
                instance = Object.Instantiate(chassisPrefab);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                var root = instance.transform;

                // --- Bottom-bar attribute grow (same math as proxy meshes / collider bake) ---
                if (applyAttributeScale)
                {
                    string prefix = ResolveFamilyPrefix(chassisPrefab, familyPrefix);
                    ShipComponentAttributeScaleLogic.ApplyToHierarchy(
                        root,
                        prefix,
                        attrs,
                        territoryMovementMult: 1f);
                }

                // --- Sum box volumes (world extents after grow) ---
                // [UNITY] Qualify UnityEngine.BoxCollider — this file also imports Unity.Physics
                // which has its own BoxCollider type (ambiguous otherwise).
                float volumeSum = 0f;
                var boxes = instance.GetComponentsInChildren<UnityEngine.BoxCollider>(true);
                for (int i = 0; i < boxes.Length; i++)
                {
                    UnityEngine.BoxCollider box = boxes[i];
                    if (box == null || !box.enabled || box.isTrigger)
                        continue;

                    // [UNITY] lossyScale folds parent scales so grown parts count correctly.
                    Vector3 lossy = box.transform.lossyScale;
                    Vector3 size = box.size;
                    float sx = Mathf.Abs(size.x * lossy.x);
                    float sy = Mathf.Abs(size.y * lossy.y);
                    float sz = Mathf.Abs(size.z * lossy.z);
                    volumeSum += sx * sy * sz;
                }

                if (volumeSum <= 0.0001f)
                    return 0f;

                // [TITAN-ORBIT] Tier scale once (plan: volume × attribute × GetShipTierScale) — not cubed.
                float tierScale = applyAttributeScale
                    ? BodyCollisionMath.GetShipTierScale(shipLevel)
                    : BodyCollisionMath.GetShipTierScale(1);
                return Mathf.Max(0f, volumeSum * tierScale);
            }
            finally
            {
                if (instance != null)
                    Object.Destroy(instance);
            }
        }

        /// <summary>
        /// USC family token before the first underscore (AstroEagle_Wing_2 → AstroEagle).
        /// Falls back to the prefab name, then AstroEagle.
        /// </summary>
        static string ResolveFamilyPrefix(GameObject chassisPrefab, string familyPrefix)
        {
            if (!string.IsNullOrWhiteSpace(familyPrefix))
                return familyPrefix.Trim();

            string name = chassisPrefab != null ? chassisPrefab.name : null;
            if (string.IsNullOrEmpty(name))
                return "AstroEagle";

            // [UNITY] Prefab instances may be named "Hull (Clone)" — strip the clone suffix.
            const string cloneSuffix = "(Clone)";
            if (name.EndsWith(cloneSuffix))
                name = name.Substring(0, name.Length - cloneSuffix.Length).TrimEnd();

            int underscore = name.IndexOf('_');
            if (underscore > 0)
                return name.Substring(0, underscore);
            return name;
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

        /// <summary>
        /// One padded AABB box covering every authored part. StarSparrow hulls are many
        /// small boxes with gaps; discrete physics tunnels through those holes. This
        /// envelope is the gameplay solid — part boxes stay for visual-fit contacts.
        /// Pad is a few centimeters so it is not a proximity field.
        /// </summary>
        static void AppendSolidHullEnvelope(List<CompoundCollider.ColliderBlobInstance> instances)
        {
            if (instances == null || instances.Count == 0)
                return;

            const float envelopePadXz = 0.04f;
            Aabb hull = Aabb.Empty;
            bool any = false;
            for (int i = 0; i < instances.Count; i++)
            {
                var inst = instances[i];
                if (!inst.Collider.IsCreated)
                    continue;

                hull.Include(inst.Collider.Value.CalculateAabb(inst.CompoundFromChild));
                any = true;
            }

            if (!any || !hull.IsValid)
                return;

            float3 size = hull.Extents;
            size.x = math.max(size.x + envelopePadXz, 0.15f);
            size.z = math.max(size.z + envelopePadXz, 0.15f);
            size.y = math.max(size.y, 0.1f);

            var blob = Unity.Physics.BoxCollider.Create(
                new BoxGeometry
                {
                    Center = hull.Center,
                    Size = size,
                    Orientation = quaternion.identity,
                },
                TitanOrbitPhysicsLayers.Ship,
                HullMaterial);
            if (!blob.IsCreated)
                return;

            instances.Add(new CompoundCollider.ColliderBlobInstance
            {
                Collider = blob,
                CompoundFromChild = RigidTransform.identity,
            });
        }

        static bool TryCreateChildCollider(
            UnityEngine.Collider unityCollider,
            Transform root,
            float presentationScale,
            out BlobAssetReference<PhysicsColliderBlob> blob,
            out RigidTransform childPose,
            bool scaleSizeByPresentation = true)
        {
            blob = default;
            childPose = RigidTransform.identity;

            Matrix4x4 relative = root.worldToLocalMatrix * unityCollider.transform.localToWorldMatrix;
            DecomposeMatrix(relative, presentationScale, out float3 position, out quaternion orientation, out float3 lossyScale);
            // DecomposeMatrix already folds presentationScale into position + lossyScale.
            // Regular ships keep a second size multiply (legacy bake). MEGA part boxes
            // skip it so the compound AABB matches the drawn hull.
            float sizeScale = scaleSizeByPresentation ? presentationScale : 1f;

            switch (unityCollider)
            {
                case UnityEngine.BoxCollider box:
                {
                    float3 size = math.abs((float3)box.size * lossyScale) * sizeScale;
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
                    childPose = new RigidTransform(orientation, position + math.mul(orientation, (float3)box.center * lossyScale * sizeScale));
                    break;
                }
                case UnityEngine.SphereCollider sphere:
                {
                    float radius = math.max(0.001f, sphere.radius * math.cmax(lossyScale) * sizeScale);
                    blob = Unity.Physics.SphereCollider.Create(
                        new SphereGeometry { Center = float3.zero, Radius = radius },
                        TitanOrbitPhysicsLayers.Ship,
                        HullMaterial);
                    childPose = new RigidTransform(orientation, position + math.mul(orientation, (float3)sphere.center * lossyScale * sizeScale));
                    break;
                }
                case UnityEngine.CapsuleCollider capsule:
                {
                    float radius = math.max(0.001f, capsule.radius * math.max(lossyScale.x, lossyScale.z) * sizeScale);
                    float height = math.max(radius * 2f, capsule.height * lossyScale.y * sizeScale);
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
                    childPose = new RigidTransform(capsuleOrientation, position + math.mul(orientation, (float3)capsule.center * lossyScale * sizeScale));
                    break;
                }
                case UnityEngine.MeshCollider meshCollider:
                {
                    // Convex only — non-convex MeshCollider cannot become a Unity Physics collider blob.
                    if (meshCollider.sharedMesh == null || !meshCollider.convex)
                        return false;

                    // --- Scale mesh verts to match grown part hierarchy ---
                    // [PHYSICS] MeshCollider.Create(Mesh) ignores Transform lossyScale. Attribute
                    // grow (and presentation bake) live in lossyScale — bake scaled verts so the
                    // hull matches Box/Sphere/Capsule paths.
                    if (!TryCreateScaledConvexMesh(
                            meshCollider.sharedMesh,
                            lossyScale * sizeScale,
                            out blob))
                        return false;

                    childPose = new RigidTransform(orientation, position);
                    break;
                }
                default:
                    return false;
            }

            return blob.IsCreated;
        }

        /// <summary>
        /// Builds a convex Unity Physics mesh collider from <paramref name="mesh"/> with
        /// per-axis <paramref name="scale"/> applied to every vertex (attribute grow + presentation).
        /// </summary>
        static bool TryCreateScaledConvexMesh(
            Mesh mesh,
            float3 scale,
            out BlobAssetReference<PhysicsColliderBlob> blob)
        {
            blob = default;
            if (mesh == null)
                return false;

            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;
            if (verts == null || tris == null || verts.Length < 3 || tris.Length < 3)
                return false;

            // --- Copy + scale verts (Temp alloc — bake is infrequent: chassis / ability buy) ---
            var nativeVerts = new NativeArray<float3>(verts.Length, Allocator.Temp);
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 v = verts[i];
                nativeVerts[i] = new float3(v.x * scale.x, v.y * scale.y, v.z * scale.z);
            }

            var nativeTris = new NativeArray<int3>(tris.Length / 3, Allocator.Temp);
            for (int t = 0, i = 0; t < nativeTris.Length; t++, i += 3)
                nativeTris[t] = new int3(tris[i], tris[i + 1], tris[i + 2]);

            blob = Unity.Physics.MeshCollider.Create(
                nativeVerts,
                nativeTris,
                TitanOrbitPhysicsLayers.Ship,
                HullMaterial);

            nativeVerts.Dispose();
            nativeTris.Dispose();
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
