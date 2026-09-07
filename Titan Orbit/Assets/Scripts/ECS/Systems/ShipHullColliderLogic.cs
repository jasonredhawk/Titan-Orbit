using System.Collections.Generic;
using TitanOrbit.Core;
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

        /// <summary>
        /// Last <see cref="ShipState.Team"/> written into the sphere <see cref="CollisionFilter"/>.
        /// Friendly moon shields are excluded for this team.
        /// </summary>
        public byte AppliedTeam;

        /// <summary>
        /// Max of the cached covering ellipsoid extents (presentation space).
        /// World size is this × <c>LocalTransform.Scale</c>. 0 = not computed yet.
        /// </summary>
        public float AppliedCoveringRadius;

        /// <summary>Cached covering ellipsoid radius on X (presentation space).</summary>
        public float AppliedCoveringExtentX;

        /// <summary>Cached covering ellipsoid radius on Y (presentation space).</summary>
        public float AppliedCoveringExtentY;

        /// <summary>Cached covering ellipsoid radius on Z (presentation space).</summary>
        public float AppliedCoveringExtentZ;

        /// <summary>Cached covering ellipsoid center X. Presentation space.</summary>
        public float AppliedCoveringCenterX;

        /// <summary>Cached covering ellipsoid center Y. Presentation space.</summary>
        public float AppliedCoveringCenterY;

        /// <summary>Cached covering ellipsoid center Z. Presentation space.</summary>
        public float AppliedCoveringCenterZ;
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
    /// Single Unity Physics covering box per ship (X/Y/Z independent, max bevel so it reads as
    /// a stretched / flattened circle) that fits every chassis collider after attribute grow.
    /// Measurements are cached per prefab + attributes so choosing ships does not Instantiate
    /// again. Rebuilt only when chassis or upgrades change. World size then grows with
    /// <c>LocalTransform.Scale</c> (tier / MEGA catalog scale).
    /// </summary>
    public static class ShipHullColliderLogic
    {
        static readonly Material HullMaterial = CreateHullMaterial();

        /// <summary>
        /// Bump when hull material or covering-sphere bake changes so live ships rebuild once.
        /// </summary>
        public const int HullMaterialRevision = 8;

        struct CoveringBakeKey : System.IEquatable<CoveringBakeKey>
        {
            public int PrefabId;
            public int AttrHash;
            public byte Mega;

            public bool Equals(CoveringBakeKey other) =>
                PrefabId == other.PrefabId && AttrHash == other.AttrHash && Mega == other.Mega;

            public override bool Equals(object obj) => obj is CoveringBakeKey other && Equals(other);

            public override int GetHashCode() => PrefabId * 397 ^ AttrHash * 17 ^ Mega;
        }

        struct CoveringBakeValue
        {
            public float3 Center;
            public float3 Extents;
        }

        /// <summary>
        /// Measurement cache — same chassis + attributes is never Instantiated again this session.
        /// Browsing ship types used to clone MEGA hulls repeatedly and hitch worse each pick.
        /// </summary>
        static readonly Dictionary<CoveringBakeKey, CoveringBakeValue> CoveringBakeCache =
            new Dictionary<CoveringBakeKey, CoveringBakeValue>(32);

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
            return TryApplyCoveringHull(
                em, shipEntity, chassisPrefab, motorMass, attrs, familyPrefix,
                megaParts: false, cachedExtents: new float3(-1f), cachedCenter: float3.zero,
                out _, out _);
        }

        /// <summary>
        /// Fallback uniform covering sphere (level-1 hull radius). Prefer
        /// <see cref="EnsureCoveringCollider"/> when extents are known.
        /// </summary>
        public static bool EnsureSingleSphereCollider(
            EntityManager em,
            Entity shipEntity,
            float motorMass)
        {
            float radius = BodyCollisionMath.GetShipHullRadiusWorld(1f);
            return EnsureCoveringCollider(
                em, shipEntity, motorMass, new float3(radius), float3.zero);
        }

        /// <summary>
        /// Writes the covering box (X/Y/Z independent, rounded). Extents and center
        /// are presentation-space (level-1). Tier scale stays on <c>LocalTransform.Scale</c>.
        /// </summary>
        public static bool EnsureCoveringCollider(
            EntityManager em,
            Entity shipEntity,
            float motorMass,
            float3 localExtents,
            float3 localCenter)
        {
            if (!em.Exists(shipEntity))
                return false;

            localExtents = math.max(localExtents, new float3(0.02f));
            var team = TeamId.None;
            if (em.HasComponent<ShipState>(shipEntity))
                team = em.GetComponentData<ShipState>(shipEntity).Team;
            var wantFilter = TitanOrbitPhysicsLayers.ShipForTeam(team);

            if (em.HasComponent<PhysicsCollider>(shipEntity))
            {
                var existing = em.GetComponentData<PhysicsCollider>(shipEntity);
                if (existing.Value.IsCreated && CoveringMatches(existing, localCenter, localExtents))
                {
                    if (TitanOrbitPhysicsLayers.FiltersEqual(
                            existing.Value.Value.GetCollisionFilter(), wantFilter))
                        return true;

                    // Team-only: rewrite filter in place. Do not rebuild the blob.
                    existing.Value.Value.SetCollisionFilter(wantFilter);
                    return true;
                }
            }

            float mass = motorMass;
            if (mass <= 0f && em.HasComponent<ShipMotorConfig>(shipEntity))
                mass = em.GetComponentData<ShipMotorConfig>(shipEntity).Mass;

            var blob = CreateCoveringBox(localCenter, localExtents, wantFilter);
            if (!blob.IsCreated)
                return false;

            ReplacePhysicsCollider(em, shipEntity, blob, mass);
            return true;
        }

        /// <summary>
        /// Measures chassis part colliders (with attribute grow), caches a covering ellipsoid,
        /// and applies it. Uses <paramref name="cachedExtents"/> when the prefab walk fails
        /// so a prior bake is not lost.
        /// </summary>
        public static bool TryApplyCoveringHull(
            EntityManager em,
            Entity shipEntity,
            GameObject chassisPrefab,
            float motorMass,
            in ShipAttributeUpgradeState attrs,
            string familyPrefix,
            bool megaParts,
            float3 cachedExtents,
            float3 cachedCenter,
            out float3 usedCenter,
            out float3 usedExtents)
        {
            usedCenter = cachedCenter;
            usedExtents = cachedExtents;

            if (chassisPrefab != null
                && TryComputeCoveringHull(
                    chassisPrefab, attrs, familyPrefix, megaParts, out float3 measuredCenter, out float3 measuredExtents)
                && math.cmax(measuredExtents) > 0.01f)
            {
                usedCenter = measuredCenter;
                usedExtents = measuredExtents;
            }
            else if (math.cmax(cachedExtents) <= 0.01f)
            {
                usedCenter = float3.zero;
                float fallback = BodyCollisionMath.GetShipHullRadiusWorld(1f);
                usedExtents = new float3(fallback);
            }

            return EnsureCoveringCollider(em, shipEntity, motorMass, usedExtents, usedCenter);
        }

        /// <summary>
        /// Presentation-space ellipsoid that fits every enabled non-trigger collider on the
        /// chassis after <see cref="ShipComponentAttributeScaleLogic.ApplyToHierarchy"/>.
        /// Instantiates once (nested MEGA modules + attribute grow). Call only when
        /// <see cref="NeedsCoveringRecompute"/> is true.
        /// </summary>
        public static bool TryComputeCoveringHull(
            GameObject chassisPrefab,
            in ShipAttributeUpgradeState attrs,
            string familyPrefix,
            bool megaParts,
            out float3 localCenter,
            out float3 localExtents)
        {
            localCenter = float3.zero;
            localExtents = float3.zero;
            if (chassisPrefab == null)
                return false;

            var key = new CoveringBakeKey
            {
                PrefabId = chassisPrefab.GetInstanceID(),
                AttrHash = megaParts ? 0 : HashAttributes(attrs),
                Mega = megaParts ? (byte)1 : (byte)0,
            };
            if (CoveringBakeCache.TryGetValue(key, out var cached))
            {
                localCenter = cached.Center;
                localExtents = cached.Extents;
                return math.cmax(localExtents) > 0.01f;
            }

            float presentationScale = BodyCollisionMath.ShipPresentationScale;
            GameObject instance = null;
            try
            {
                // Walk the prefab asset unless we must mutate it (attribute grow) or MEGA
                // nested module colliders are stripped until Instantiate.
                bool needClone = megaParts || ShipStatApplyLogic.SumAttributeLevels(attrs) > 0;
                Transform root;
                if (needClone)
                {
                    instance = Object.Instantiate(chassisPrefab);
                    instance.SetActive(false);
                    instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    instance.transform.localScale = Vector3.one;
                    root = instance.transform;
                    if (!megaParts)
                    {
                        string prefix = ResolveFamilyPrefix(chassisPrefab, familyPrefix);
                        ShipComponentAttributeScaleLogic.ApplyToHierarchy(
                            root, prefix, attrs, territoryMovementMult: 1f);
                    }
                }
                else
                {
                    root = chassisPrefab.transform;
                }

                var hull = Aabb.Empty;
                bool any = false;
                var colliders = root.GetComponentsInChildren<UnityEngine.Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    var collider = colliders[i];
                    if (collider == null || !collider.enabled || collider.isTrigger)
                        continue;
                    if (!TryIncludeColliderAabb(
                            collider, root, presentationScale, scaleSizeByPresentation: false, ref hull))
                        continue;
                    any = true;
                }

                if (!any || !hull.IsValid)
                    return false;

                localCenter = hull.Center;
                localExtents = hull.Extents * 0.5f + 0.04f;
                if (math.cmax(localExtents) <= 0.01f)
                    return false;

                CoveringBakeCache[key] = new CoveringBakeValue
                {
                    Center = localCenter,
                    Extents = localExtents,
                };
                return true;
            }
            finally
            {
                DestroyCoveringInstance(instance);
            }
        }

        static int HashAttributes(in ShipAttributeUpgradeState attrs)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + attrs.FirePower;
                hash = hash * 31 + attrs.BulletSpeed;
                hash = hash * 31 + attrs.MaxHealth;
                hash = hash * 31 + attrs.HealthRegen;
                hash = hash * 31 + attrs.EnergyCapacity;
                hash = hash * 31 + attrs.EnergyRegen;
                hash = hash * 31 + attrs.MovementSpeed;
                hash = hash * 31 + attrs.RotationSpeed;
                hash = hash * 31 + attrs.GemCapacity;
                hash = hash * 31 + attrs.PeopleCapacity;
                return hash;
            }
        }

        static void DestroyCoveringInstance(GameObject instance)
        {
            if (instance == null)
                return;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Object.DestroyImmediate(instance);
                return;
            }
#endif
            // Play Mode: destroy now so browsing ships cannot pile deferred clones.
            Object.DestroyImmediate(instance);
        }

        static bool CoveringMatches(in PhysicsCollider existing, float3 center, float3 extents)
        {
            if (!existing.Value.IsCreated)
                return false;

            if (existing.Value.Value.Type == ColliderType.Box)
            {
                unsafe
                {
                    var box = (Unity.Physics.BoxCollider*)existing.Value.GetUnsafePtr();
                    BoxGeometry g = box->Geometry;
                    float3 size = extents * 2f;
                    return math.lengthsq(g.Center - center) < 1e-6f
                        && math.lengthsq(g.Size - size) < 1e-6f;
                }
            }

            Aabb aabb = existing.Value.Value.CalculateAabb();
            float3 half = aabb.Extents * 0.5f;
            return math.lengthsq(aabb.Center - center) < 1e-5f
                && math.lengthsq(half - extents) < 1e-5f;
        }

        /// <summary>
        /// Native box scaled independently on X/Y/Z (flat disk / long oval). Max bevel rounds
        /// it toward a stretched circle without ConvexCollider.Create.
        /// </summary>
        static BlobAssetReference<PhysicsColliderBlob> CreateCoveringBox(
            float3 center,
            float3 extents,
            CollisionFilter filter)
        {
            float3 size = extents * 2f;
            float bevel = math.max(0f, math.cmin(extents) * 0.9f);
            return Unity.Physics.BoxCollider.Create(
                new BoxGeometry
                {
                    Center = center,
                    Size = size,
                    Orientation = quaternion.identity,
                    BevelRadius = bevel,
                },
                filter,
                HullMaterial);
        }

        static bool TryIncludeColliderAabb(
            UnityEngine.Collider unityCollider,
            Transform root,
            float presentationScale,
            bool scaleSizeByPresentation,
            ref Aabb hull)
        {
            Matrix4x4 relative = root.worldToLocalMatrix * unityCollider.transform.localToWorldMatrix;
            DecomposeMatrix(relative, presentationScale, out float3 position, out quaternion orientation, out float3 lossyScale);
            float sizeScale = scaleSizeByPresentation ? presentationScale : 1f;

            switch (unityCollider)
            {
                case UnityEngine.BoxCollider box:
                {
                    float3 size = math.abs((float3)box.size * lossyScale) * sizeScale;
                    if (math.any(size < 0.001f))
                        return false;
                    float3 center = position + math.mul(orientation, (float3)box.center * lossyScale * sizeScale);
                    IncludeOrientedBox(ref hull, center, size * 0.5f, orientation);
                    return true;
                }
                case UnityEngine.SphereCollider sphere:
                {
                    float radius = math.max(0.001f, sphere.radius * math.cmax(lossyScale) * sizeScale);
                    float3 center = position + math.mul(orientation, (float3)sphere.center * lossyScale * sizeScale);
                    float3 e = new float3(radius, radius, radius);
                    hull.Include(center - e);
                    hull.Include(center + e);
                    return true;
                }
                case UnityEngine.CapsuleCollider capsule:
                {
                    float radius = math.max(0.001f, capsule.radius * math.max(lossyScale.x, lossyScale.z) * sizeScale);
                    float height = math.max(radius * 2f, capsule.height * lossyScale.y * sizeScale);
                    float3 center = position + math.mul(orientation, (float3)capsule.center * lossyScale * sizeScale);
                    float3 half = new float3(radius, height * 0.5f, radius);
                    quaternion capRot = capsule.direction switch
                    {
                        0 => math.mul(orientation, quaternion.Euler(0f, 0f, math.radians(90f))),
                        2 => math.mul(orientation, quaternion.Euler(math.radians(90f), 0f, 0f)),
                        _ => orientation,
                    };
                    IncludeOrientedBox(ref hull, center, half, capRot);
                    return true;
                }
                case UnityEngine.MeshCollider meshCollider:
                {
                    if (meshCollider.sharedMesh == null)
                        return false;
                    Bounds b = meshCollider.sharedMesh.bounds;
                    float3 center = position + math.mul(orientation, (float3)b.center * lossyScale * sizeScale);
                    float3 half = math.abs((float3)b.extents * lossyScale) * sizeScale;
                    IncludeOrientedBox(ref hull, center, half, orientation);
                    return true;
                }
                default:
                    return false;
            }
        }

        static void IncludeOrientedBox(ref Aabb hull, float3 center, float3 halfExtents, quaternion rotation)
        {
            for (int i = 0; i < 8; i++)
            {
                float3 local = new float3(
                    (i & 1) == 0 ? -halfExtents.x : halfExtents.x,
                    (i & 2) == 0 ? -halfExtents.y : halfExtents.y,
                    (i & 4) == 0 ? -halfExtents.z : halfExtents.z);
                hull.Include(center + math.mul(rotation, local));
            }
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
            var zeroAttrs = default(ShipAttributeUpgradeState);
            return TryApplyCoveringHull(
                em, shipEntity, chassisPrefab, motorMass, zeroAttrs, familyPrefix: null,
                megaParts: true, cachedExtents: new float3(-1f), cachedCenter: float3.zero,
                out _, out _);
        }

        /// <summary>
        /// True when chassis, branch, attribute grow, or bake revision changed — walk the
        /// prefab again. Ship level alone is not enough: tier size lives on
        /// <c>LocalTransform.Scale</c>. Team-only filter updates reuse the cache.
        /// </summary>
        public static bool NeedsCoveringRecompute(
            in ShipHullColliderState applied,
            in FixedString64Bytes chassisKey,
            int branchIndex,
            int attributeSum,
            bool isMega)
        {
            if (math.cmax(GetCachedCoveringExtents(applied)) <= 0.01f)
                return true;
            if (!applied.ChassisId.Equals(chassisKey))
                return true;
            if (applied.AppliedBranchIndex != branchIndex)
                return true;
            if (applied.AppliedAttributeSum != attributeSum)
                return true;
            if (applied.AppliedHullMaterialRevision != HullMaterialRevision)
                return true;
            if (isMega && applied.AppliedMegaColliderRevision != MegaShipCatalog.HullColliderRevision)
                return true;
            return false;
        }

        /// <summary>Cached covering ellipsoid center from the last bake.</summary>
        public static float3 GetCachedCoveringCenter(in ShipHullColliderState applied) =>
            new float3(applied.AppliedCoveringCenterX, applied.AppliedCoveringCenterY, applied.AppliedCoveringCenterZ);

        /// <summary>Cached covering ellipsoid radii (X/Y/Z). Falls back to uniform radius.</summary>
        public static float3 GetCachedCoveringExtents(in ShipHullColliderState applied)
        {
            var extents = new float3(
                applied.AppliedCoveringExtentX,
                applied.AppliedCoveringExtentY,
                applied.AppliedCoveringExtentZ);
            if (math.cmax(extents) > 0.01f)
                return extents;
            return new float3(applied.AppliedCoveringRadius);
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
#if UNITY_SERVER && !UNITY_EDITOR
                // Dedicated: walk the prefab asset only. Instantiates are 80–200ms and
                // Dedicated Server Optimizations strip the clone's meshes/colliders.
                Transform root = chassisPrefab.transform;
#else
                // Nested StarSparrow Collider / Collider2 boxes are stripped on the prefab
                // asset until Instantiate — same reason MEGA always clones. Walking the
                // asset left regular ships on the tiny ghost sphere.
                instance = Object.Instantiate(chassisPrefab);
                instance.SetActive(false);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                Transform root = instance.transform;
#endif

#if !UNITY_SERVER || UNITY_EDITOR
                if (instance != null && ShipStatApplyLogic.SumAttributeLevels(attrs) > 0)
                {
                    string prefix = ResolveFamilyPrefix(chassisPrefab, familyPrefix);
                    ShipComponentAttributeScaleLogic.ApplyToHierarchy(
                        root,
                        prefix,
                        attrs,
                        territoryMovementMult: 1f);
                }
#endif

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
                Transform root;
#if UNITY_SERVER && !UNITY_EDITOR
                // Dedicated: authored boxes on the prefab asset — no Instantiates.
                root = chassisPrefab.transform;
                applyAttributeScale = false;
#else
                // --- Temp hierarchy (destroyed in finally) ---
                // [UNITY] Instantiate so we can mutate localScale without dirtying the asset prefab.
                instance = Object.Instantiate(chassisPrefab);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                root = instance.transform;

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
#endif

                // --- Sum box volumes (world extents after grow) ---
                // [UNITY] Qualify UnityEngine.BoxCollider — this file also imports Unity.Physics
                // which has its own BoxCollider type (ambiguous otherwise).
                float volumeSum = 0f;
                var boxes = root.GetComponentsInChildren<UnityEngine.BoxCollider>(true);
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
