using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Applies weapon mounts, wing tractor beams, and hull colliders when chassis/level/branch
    /// changes (and, in Entities Graphics mode, when bottom-bar attribute sum changes so the
    /// PhysicsCollider grows with part meshes). Runs on server and client so both worlds share
    /// the same sim attachment buffers.
    /// Weapon and wing locals are preferred from a live prefab bake (hull-root unscaled) so fire
    /// muzzles and tractor reach match the visible upgrade-tree hull without requiring a manual
    /// catalog re-bake menu pass.
    /// <para>
    /// Re-applies only when the chassis identity changes — not when a hull simply has zero wings
    /// (that used to Instantiates the prefab every frame and could stall join loading near 92%).
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipStatApplySystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipChassisCatalogApplySystem : ISystem
    {
        /// <summary>Ship that needs catalog data applied after the query pass completes.</summary>
        struct PendingCatalogApply
        {
            public Entity Entity;
            public int BranchIndex;
        }

        /// <summary>Scratch list for runtime weapon bake — reused to avoid per-apply List alloc noise.</summary>
        static readonly List<ShipWeaponMountBakeData> WeaponBakeScratch =
            new List<ShipWeaponMountBakeData>(8);

        /// <summary>Scratch list for runtime wing bake — reused to avoid per-apply List alloc noise.</summary>
        static readonly List<ShipWingTractorBeamBakeData> WingBakeScratch =
            new List<ShipWingTractorBeamBakeData>(16);

        /// <summary>
        /// Per tick: find ships whose chassis key changed, then write mounts/wings/colliders.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Join-crash gate (client only) ---
            // [TITAN-ORBIT] Ship WithEntityAccess during post–Join Team Instantiates Crash!!!.
            // Server always applies — tractor reach and fire mounts are authoritative sim data.
            // Do NOT gate on TransformQuarantine or UseEntitiesGraphicsForShips: quarantine is
            // session-long on Windows, and hybrid mode still needs upgrade-tree wing buffers.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            var catalog = ShipChassisVisualCatalog.Instance;
            var config = ShipStatApplyLogic.Config;
            if (catalog == null || config == null)
                return;

            var em = state.EntityManager;
            var pending = new NativeList<PendingCatalogApply>(Allocator.Temp);

            // --- Pass 1: read-only query — collect ships that need catalog rebuild ---
            foreach (var (ship, loadout, entity) in SystemAPI
                         .Query<RefRO<ShipState>, RefRO<ShipLoadoutState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                int branch = ship.ValueRO.BranchIndex;
                if (NeedsCatalogApply(em, catalog, config, entity, ship.ValueRO, branch))
                {
                    pending.Add(new PendingCatalogApply
                    {
                        Entity = entity,
                        BranchIndex = branch,
                    });
                }
            }

            foreach (var (ship, entity) in SystemAPI
                         .Query<RefRO<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithNone<ShipLoadoutState>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                int branch = ship.ValueRO.BranchIndex;
                if (NeedsCatalogApply(em, catalog, config, entity, ship.ValueRO, branch))
                {
                    pending.Add(new PendingCatalogApply
                    {
                        Entity = entity,
                        BranchIndex = branch,
                    });
                }
            }

            // --- Pass 2: structural changes (buffers, colliders, tracking component) ---
            // Server-only moon re-pin after collider swap (client TransformQuarantine forbids planet gather).
            bool serverReattach = state.World.IsServer();
            float mapW = 0f;
            float mapH = 0f;
            double moonElapsed = SystemAPI.Time.ElapsedTime;
            bool haveMapSize = false;
            if (serverReattach)
            {
                haveMapSize = ShipMoonDockAttachLogic.TryGetMapSize(em, out mapW, out mapH);
                int hz = 0;
                if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                    hz = tickRate.SimulationTickRate;
                if (SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime))
                    moonElapsed = PlanetGemMoonOrbitClock.GetElapsedSeconds(
                        networkTime, hz, includeTickFraction: false);
            }

            for (int i = 0; i < pending.Length; i++)
            {
                var work = pending[i];
                if (!em.Exists(work.Entity) || !em.HasComponent<ShipState>(work.Entity))
                    continue;

                var ship = em.GetComponentData<ShipState>(work.Entity);
                TryApplyCatalogData(em, config, catalog, work.Entity, ship, work.BranchIndex);

                // [TITAN-ORBIT] New hull collider while docked can Physics-eject the ship — re-pin.
                // Skip when map size is missing (do not invent a period for moon attach math).
                if (serverReattach && haveMapSize)
                {
                    ShipMoonDockAttachLogic.TryReattachFullyDockedShip(
                        em, work.Entity, mapW, mapH, moonElapsed);
                }
            }

            pending.Dispose();

            // --- Pass 3: refill undercounted wing+weapon buffers (chassis already applied) ---
            // Empty buffers are valid. Only refill when the prefab has more bodies than the buffer.
            var refill = new NativeList<PendingCatalogApply>(4, Allocator.Temp);
            foreach (var (ship, entity) in SystemAPI
                         .Query<RefRO<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;
                if (!em.HasComponent<ShipHullColliderState>(entity))
                    continue;

                if (!NeedsAttachmentRefill(em, config, entity, ship.ValueRO))
                    continue;

                refill.Add(new PendingCatalogApply
                {
                    Entity = entity,
                    BranchIndex = ship.ValueRO.BranchIndex,
                });
            }

            for (int i = 0; i < refill.Length; i++)
            {
                var work = refill[i];
                if (!em.Exists(work.Entity) || !em.HasComponent<ShipState>(work.Entity))
                    continue;
                TryRefillEmptyAttachmentBuffers(
                    em, catalog, config, work.Entity, em.GetComponentData<ShipState>(work.Entity));
            }

            refill.Dispose();
        }

        /// <summary>
        /// True when chassis id / level / branch differs from the last applied
        /// <see cref="ShipHullColliderState"/>, or (Entities Graphics mode only) when bottom-bar
        /// attribute sum changed and the hull collider must grow with the parts.
        /// </summary>
        static bool NeedsCatalogApply(
            EntityManager em,
            ShipChassisVisualCatalog catalog,
            PlanetShipFamilyConfig config,
            Entity entity,
            in ShipState ship,
            int branchIndex)
        {
            if (!ShipStatApplyLogic.TryResolveChassisId(
                    em,
                    entity,
                    ship.Team,
                    ship.ShipLevel,
                    branchIndex,
                    out string chassisId,
                    allowFallback: true))
                return false;

            // Catalog entry OR tier prefab is enough — wings can live-bake from the prefab alone.
            bool hasCatalog = catalog.TryGetEntry(chassisId, out _);
            bool hasPrefab = ResolveChassisPrefab(config, chassisId) != null;
            if (!hasCatalog && !hasPrefab)
                return false;

            int attributeSum = 0;
            if (em.HasComponent<ShipAttributeUpgradeState>(entity))
                attributeSum = ShipStatApplyLogic.SumAttributeLevels(
                    em.GetComponentData<ShipAttributeUpgradeState>(entity));

            if (!em.HasComponent<ShipHullColliderState>(entity))
                return true;

            var applied = em.GetComponentData<ShipHullColliderState>(entity);
            var chassisKey = new FixedString64Bytes(chassisId);
            if (!applied.ChassisId.Equals(chassisKey)
                || applied.AppliedShipLevel != ship.ShipLevel
                || applied.AppliedBranchIndex != branchIndex)
                return true;

            // --- Attribute grow → collider rebuild (EG only) ---
            // [TITAN-ORBIT] ShipHullColliderSyncSystem early-outs when UseEntitiesGraphicsForShips
            // is true, so this catalog path owns attribute hull rebuilds in EG mode. Hybrid mode
            // leaves attribute dirty to ShipHullColliderSyncSystem to avoid double Instantiates.
            // Do NOT treat empty wing buffers as full catalog dirty here — Pass 3 refills
            // attachments without collider Instantiates (empty-as-dirty used to stall join).
            if (TitanOrbitPresentationConfig.UseEntitiesGraphicsForShips
                && applied.AppliedAttributeSum != attributeSum)
                return true;

            return false;
        }

        /// <summary>
        /// True when this chassis was already applied but wing/weapon buffers have fewer slots
        /// than the prefab. Empty is valid (unarmed / no wings) — do not re-bake every tick.
        /// </summary>
        static bool NeedsAttachmentRefill(
            EntityManager em,
            PlanetShipFamilyConfig config,
            Entity entity,
            in ShipState ship)
        {
            if (!em.HasComponent<ShipHullColliderState>(entity))
                return false;

            if (!ShipStatApplyLogic.TryResolveChassisId(
                    em,
                    entity,
                    ship.Team,
                    ship.ShipLevel,
                    ship.BranchIndex,
                    out string chassisId,
                    allowFallback: true))
                return false;

            var applied = em.GetComponentData<ShipHullColliderState>(entity);
            if (!applied.ChassisId.Equals(new FixedString64Bytes(chassisId)))
                return false;

            GameObject chassisPrefab = ResolveChassisPrefab(config, chassisId);
            if (chassisPrefab == null)
                return false;

            int currentWings = em.HasBuffer<ShipWingTractorBeamElement>(entity)
                ? em.GetBuffer<ShipWingTractorBeamElement>(entity).Length
                : 0;
            int currentWeapons = em.HasBuffer<ShipWeaponMountElement>(entity)
                ? em.GetBuffer<ShipWeaponMountElement>(entity).Length
                : 0;
            int expectedWings = ShipChassisPrefabBakeUtility.CountDistinctWingBodies(chassisPrefab);
            int expectedWeapons = ShipChassisPrefabBakeUtility.CountDistinctWeaponBodies(chassisPrefab);
            return expectedWings > currentWings || expectedWeapons > currentWeapons;
        }

        /// <summary>
        /// When chassis identity is already applied but wing/weapon buffers are empty or undercounted,
        /// refill them from the tier prefab / catalog without rebuilding PhysicsCollider.
        /// </summary>
        static void TryRefillEmptyAttachmentBuffers(
            EntityManager em,
            ShipChassisVisualCatalog catalog,
            PlanetShipFamilyConfig config,
            Entity entity,
            in ShipState ship)
        {
            if (!ShipStatApplyLogic.TryResolveChassisId(
                    em,
                    entity,
                    ship.Team,
                    ship.ShipLevel,
                    ship.BranchIndex,
                    out string chassisId,
                    allowFallback: true))
                return;

            // Only refill when the applied chassis key still matches — otherwise Pass 1/2 owns it.
            var applied = em.GetComponentData<ShipHullColliderState>(entity);
            if (!applied.ChassisId.Equals(new FixedString64Bytes(chassisId)))
                return;

            catalog.TryGetEntry(chassisId, out var entry);
            GameObject chassisPrefab = ResolveChassisPrefab(config, chassisId);
            if (entry == null && chassisPrefab == null)
                return;

            int currentWings = em.HasBuffer<ShipWingTractorBeamElement>(entity)
                ? em.GetBuffer<ShipWingTractorBeamElement>(entity).Length
                : 0;
            int currentWeapons = em.HasBuffer<ShipWeaponMountElement>(entity)
                ? em.GetBuffer<ShipWeaponMountElement>(entity).Length
                : 0;
            int expectedWings = chassisPrefab != null
                ? ShipChassisPrefabBakeUtility.CountDistinctWingBodies(chassisPrefab)
                : 0;
            int expectedWeapons = chassisPrefab != null
                ? ShipChassisPrefabBakeUtility.CountDistinctWeaponBodies(chassisPrefab)
                : 0;

            if (expectedWeapons > currentWeapons)
            {
                ApplyWeaponMounts(em, entity, entry, chassisPrefab);
                ShipStatApplyLogic.TryApplyPerMountWeaponCombat(
                    em, entity, chassisId, ship.ShipLevel);
                if (em.HasComponent<MegaShipState>(entity)
                    && em.GetComponentData<MegaShipState>(entity).IsMega)
                    MegaShipStatApplyLogic.ResizeGunnerSlots(em, entity);
                TryApplyMegaWeaponMountStats(em, entity);
            }

            if (expectedWings > currentWings)
                ApplyWingTractorBeams(em, entity, entry, chassisPrefab);
        }

        /// <summary>
        /// Writes mounts, wings, and optional hull collider for one ship entity.
        /// </summary>
        static void TryApplyCatalogData(
            EntityManager em,
            PlanetShipFamilyConfig config,
            ShipChassisVisualCatalog catalog,
            Entity entity,
            in ShipState ship,
            int branchIndex)
        {
            if (!ShipStatApplyLogic.TryResolveChassisId(
                    em,
                    entity,
                    ship.Team,
                    ship.ShipLevel,
                    branchIndex,
                    out string chassisId,
                    allowFallback: true))
                return;

            catalog.TryGetEntry(chassisId, out var entry);
            GameObject chassisPrefab = ResolveChassisPrefab(config, chassisId);

            // [TITAN-ORBIT] Weapons: live prefab bake first so multi-cannon upgrade hulls fire from
            // every Weapon child. Stale catalog WeaponMounts often had 0–1 entries while the GO
            // showed 4 barrels — server then only simulated a single muzzle.
            ApplyWeaponMounts(em, entity, entry, chassisPrefab);

            if (em.HasComponent<MegaShipState>(entity)
                && em.GetComponentData<MegaShipState>(entity).IsMega)
            {
                MegaShipStatApplyLogic.ResizeGunnerSlots(em, entity);
            }

            // [TITAN-ORBIT] Pose bake clears combat fields — refill per-barrel firePower / fireRate
            // from family Weapon stats × transform scale × ship level (same helper as ShipStatApply).
            ShipStatApplyLogic.TryApplyPerMountWeaponCombat(em, entity, chassisId, ship.ShipLevel);
            TryApplyMegaWeaponMountStats(em, entity);

            // [TITAN-ORBIT] Wings: live prefab bake first so server pull radius matches the upgrade
            // hull the client draws beams from. Stale catalog lists caused “beam shows, no pull.”
            ApplyWingTractorBeams(em, entity, entry, chassisPrefab);

            // --- Attribute levels for hull collider part grow ---
            var attrs = default(ShipAttributeUpgradeState);
            int attributeSum = 0;
            if (em.HasComponent<ShipAttributeUpgradeState>(entity))
            {
                attrs = em.GetComponentData<ShipAttributeUpgradeState>(entity);
                attributeSum = ShipStatApplyLogic.SumAttributeLevels(attrs);
            }

            if (chassisPrefab != null)
            {
                float motorMass = 1f;
                if (em.HasComponent<ShipMotorConfig>(entity))
                    motorMass = em.GetComponentData<ShipMotorConfig>(entity).Mass;

                // [TITAN-ORBIT] Bake hierarchy with same attribute scale as proxy meshes so
                // grown wings/engines collide at their visible size (not authored-only).
                string familyPrefix = ResolveFamilyPrefix(chassisId);
                bool isMega = em.HasComponent<MegaShipState>(entity)
                    && em.GetComponentData<MegaShipState>(entity).IsMega;
                if (isMega)
                    ShipHullColliderLogic.TryApplyMegaBoundsCollider(em, entity, chassisPrefab, motorMass);
                else
                    ShipHullColliderLogic.TryApplyChassisCollider(
                        em, entity, chassisPrefab, motorMass, attrs, familyPrefix);
            }

            var hullState = new ShipHullColliderState
            {
                ChassisId = new FixedString64Bytes(chassisId),
                AppliedShipLevel = ship.ShipLevel,
                AppliedBranchIndex = branchIndex,
                AppliedAttributeSum = attributeSum,
            };

            if (em.HasComponent<ShipHullColliderState>(entity))
                em.SetComponentData(entity, hullState);
            else
                em.AddComponentData(entity, hullState);
        }

        /// <summary>USC family token from chassis id (AstroEagle_Tier2 → AstroEagle).</summary>
        static string ResolveFamilyPrefix(string chassisId)
        {
            if (string.IsNullOrEmpty(chassisId))
                return "AstroEagle";
            int underscore = chassisId.IndexOf('_');
            if (underscore > 0)
                return chassisId.Substring(0, underscore);
            return chassisId;
        }

        /// <summary>
        /// MEGA barrels use catalog unique-component stats (including short <c>bulletRange</c>),
        /// not the store-planet family's per-mount combat table.
        /// </summary>
        static void TryApplyMegaWeaponMountStats(EntityManager em, Entity entity)
        {
            if (!em.HasComponent<MegaShipState>(entity))
                return;

            var mega = em.GetComponentData<MegaShipState>(entity);
            if (!mega.IsMega)
                return;

            var catalog = MegaShipCatalog.Load();
            if (catalog == null || !catalog.TryGetEntry(mega.CatalogIndex, out MegaShipCatalogEntry entry))
                return;

            MegaShipStatApplyLogic.ApplyCatalogWeaponMountStats(em, entity, catalog, entry);
        }

        static void ApplyWeaponMounts(
            EntityManager em,
            Entity entity,
            ShipChassisVisualEntry entry,
            GameObject chassisPrefab)
        {
            if (!em.HasBuffer<ShipWeaponMountElement>(entity))
                em.AddBuffer<ShipWeaponMountElement>(entity);

            var buffer = em.GetBuffer<ShipWeaponMountElement>(entity);
            buffer.Clear();

            // --- Path A: live prefab bake (authoritative for upgrade-tree hulls) ---
            if (ShipChassisPrefabBakeUtility.TryBakeWeaponMounts(chassisPrefab, WeaponBakeScratch))
            {
                for (int i = 0; i < WeaponBakeScratch.Count; i++)
                    buffer.Add(ToWeaponElement(WeaponBakeScratch[i]));
                SortWeaponMountBufferByCannonIndex(buffer);
                return;
            }

            // --- Path B: catalog ScriptableObject fallback ---
            if (entry?.WeaponMounts == null)
                return;

            for (int i = 0; i < entry.WeaponMounts.Count; i++)
                buffer.Add(ToWeaponElement(entry.WeaponMounts[i]));
            SortWeaponMountBufferByCannonIndex(buffer);
        }

        /// <summary>
        /// [TITAN-ORBIT] Stable round-robin order — buffer index 0,1,2… matches live GO
        /// <c>CannonIndex</c> sort in <c>BulletMuzzlePresentation</c>.
        /// </summary>
        static void SortWeaponMountBufferByCannonIndex(DynamicBuffer<ShipWeaponMountElement> buffer)
        {
            int n = buffer.Length;
            if (n <= 1)
                return;

            // --- Insertion sort (mount counts are tiny, usually ≤ 8) ---
            for (int i = 1; i < n; i++)
            {
                var key = buffer[i];
                int j = i - 1;
                while (j >= 0 && buffer[j].CannonIndex > key.CannonIndex)
                {
                    buffer[j + 1] = buffer[j];
                    j--;
                }

                buffer[j + 1] = key;
            }
        }

        /// <summary>Maps bake DTO → runtime weapon mount buffer element.</summary>
        static ShipWeaponMountElement ToWeaponElement(in ShipWeaponMountBakeData mount) =>
            new ShipWeaponMountElement
            {
                LocalPosition = mount.LocalPosition,
                LocalRotation = ShipChassisPrefabBakeUtility.ToPlanarYawLocalRotation(mount.LocalRotation),
                DirectionAngleDeg = mount.DirectionAngleDeg,
                CannonIndex = mount.CannonIndex,
            };

        /// <summary>
        /// Fills <see cref="ShipWingTractorBeamElement"/> from a live chassis prefab bake when possible,
        /// else from the catalog entry list.
        /// </summary>
        static void ApplyWingTractorBeams(
            EntityManager em,
            Entity entity,
            ShipChassisVisualEntry entry,
            GameObject chassisPrefab)
        {
            if (!em.HasBuffer<ShipWingTractorBeamElement>(entity))
                em.AddBuffer<ShipWingTractorBeamElement>(entity);

            var buffer = em.GetBuffer<ShipWingTractorBeamElement>(entity);
            buffer.Clear();

            // --- Path A: live prefab bake (authoritative for upgrade-tree hulls) ---
            if (ShipChassisPrefabBakeUtility.TryBakeWingTractorBeams(chassisPrefab, WingBakeScratch))
            {
                for (int i = 0; i < WingBakeScratch.Count; i++)
                    buffer.Add(ToWingElement(WingBakeScratch[i]));
                return;
            }

            // --- Path B: catalog ScriptableObject fallback ---
            if (entry?.WingTractorBeams == null)
                return;

            for (int i = 0; i < entry.WingTractorBeams.Count; i++)
                buffer.Add(ToWingElement(entry.WingTractorBeams[i]));
        }

        /// <summary>Family ladder prefab, or MEGA catalog prefab for <c>MEGA_###</c> ids.</summary>
        static GameObject ResolveChassisPrefab(PlanetShipFamilyConfig config, string chassisId)
        {
            var tier = config != null ? config.GetTierEntryForChassisId(chassisId) : null;
            if (tier != null && tier.prefab != null)
                return tier.prefab;

            if (MegaShipCatalog.IsMegaChassisId(chassisId))
            {
                var mega = MegaShipCatalog.Load();
                if (mega != null)
                    return mega.GetPrefabByChassisId(chassisId);
            }

            return null;
        }

        /// <summary>Maps bake DTO → runtime buffer element.</summary>
        static ShipWingTractorBeamElement ToWingElement(in ShipWingTractorBeamBakeData wing) =>
            new ShipWingTractorBeamElement
            {
                LocalPosition = wing.LocalPosition,
                TractorBeamDistance = wing.TractorBeamDistance,
                TractorBeamDistancePerLevel = wing.TractorBeamDistancePerLevel,
                TractorBeamPower = wing.TractorBeamPower,
                TractorBeamPowerPerLevel = wing.TractorBeamPowerPerLevel,
                MaxGems = wing.MaxGems,
                MaxGemsPerLevel = wing.MaxGemsPerLevel,
            };
    }
}
