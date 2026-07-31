using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side component mesh scaling on ship proxies when bottom-bar attribute upgrades change,
    /// and when the ship is inside a friendly territory triangle (Engine/Thrust mounts grow like a
    /// speed upgrade — NGO feel). Watches ShipAttributeUpgradeState + territory multiplier on the
    /// linked ship entity. Attached by EcsWorldVisualizer; <b>cosmetic only</b>.
    /// <para>
    /// Growth rates come from <c>ShipFamilyPartCalcProfileSet.asset</c> Part Profiles
    /// (<c>perLevel / base</c> via <see cref="ShipComponentAttributeScaleLogic.BuildRatesFromProfileSet"/>).
    /// Multiple drivers on one part share growth (<c>1/N</c> each) and add — they do not multiply.
    /// Tail mounts grow from RotationSpeed; Engine and Thruster buckets share MovementSpeed.
    /// </para>
    /// <para>
    /// Territory mult is <see cref="PlanetConnectionGraphCache.LocalOwnerTerritoryMult"/> — sticky and
    /// written only on first-time predicting ticks so NetCode resim / triangle-edge noise cannot
    /// blink engine scale every frame.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Scaling thruster mounts also parents jet flame prefabs from
    /// <see cref="ShipPropulsionVisualApplier"/>. After a territory step we call
    /// <see cref="ShipPropulsionVisualApplier.ForceRefreshEmission"/> so flames re-<c>Play()</c>
    /// without the player releasing thrust.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(95)]
    public class ShipComponentAttributeScaleApplier : MonoBehaviour
    {
        /// <summary>Ignore tiny float noise; only re-apply on clear boosted↔normal transitions.</summary>
        const float TerritoryMultApplyEpsilon = 0.02f;

        /// <summary>Linked ship ghost entity — source of ShipAttributeUpgradeState.</summary>
        Entity _shipEntity;
        /// <summary>USC family prefix for legacy token filter (e.g. AstroEagle).</summary>
        string _familyPrefix = "AstroEagle";
        bool _initialized;

        /// <summary>
        /// Cached ProfileSet <c>perLevel/base</c> fractions per part group (version 1).
        /// Rebuilt on Bind / RebuildCache — not every LateUpdate.
        /// </summary>
        ShipComponentAttributeScaleLogic.ProfileScaleRates _rates;

        ShipComponentAttributeScaleLogic.ScaleGroup _cockpit;
        ShipComponentAttributeScaleLogic.ScaleGroup _wing;
        ShipComponentAttributeScaleLogic.ScaleGroup _weapon;
        ShipComponentAttributeScaleLogic.ScaleGroup _engine;
        ShipComponentAttributeScaleLogic.ScaleGroup _thruster;
        ShipComponentAttributeScaleLogic.ScaleGroup _tail;
        ShipComponentAttributeScaleLogic.ScaleGroup _part;

        ShipAttributeUpgradeState _lastApplied;
        float _lastTerritoryMult = -1f;

        /// <summary>
        /// Cached propulsion applier on the same hull proxy. Null until first territory/upgrade apply
        /// that needs a VFX refresh after mount scale.
        /// </summary>
        ShipPropulsionVisualApplier _propulsionVisual;

        /// <summary>Links to ship entity, caches chassis transform groups + ProfileSet rates, applies initial scale.</summary>
        public void Bind(Entity shipEntity, string familyPrefix, ShipFamilyDefinition family)
        {
            _shipEntity = shipEntity;
            if (!string.IsNullOrWhiteSpace(familyPrefix))
                _familyPrefix = familyPrefix.Trim();
            // Family is unused for rates — ProfileSet Part Profiles are the shared source of truth.
            _ = family;
            _lastApplied = default;
            _lastTerritoryMult = -1f;
            RebuildCache();
        }

        /// <summary>Scans hull hierarchy, loads ProfileSet rates, stores base scales/positions.</summary>
        void RebuildCache()
        {
            // --- ProfileSet percent-of-base rates (version 1) ---
            // [TITAN-ORBIT] EvaluateAtVersion fills *PerLevel when zero — same as Scan.
            var profileSet = ShipFamilyPartCalcProfileSet.LoadShared();
            _rates = ShipComponentAttributeScaleLogic.BuildRatesFromProfileSet(profileSet);

            var stats = ChassisComponentStats.FromTransform(transform, _familyPrefix);

            // --- Legacy USC modules only for attribute grow ---
            // [TITAN-ORBIT] FromTransform uses ProfileSet (EngineComp, CockpitCover, Body→Hull…).
            // Bottom-bar mesh scale must match classic Family_Wing / Family_Engine / Family_Tail
            // tokens or every ability tick grows cosmetics that never used to scale.
            _cockpit = ShipComponentAttributeScaleLogic.BuildGroup(
                ChassisComponentStats.FilterLegacyAttributeScaleTransforms(
                    stats.cockpitTransforms, _familyPrefix, "Cockpit"));
            _wing = ShipComponentAttributeScaleLogic.BuildGroup(
                ChassisComponentStats.FilterLegacyAttributeScaleTransforms(
                    stats.wingTransforms, _familyPrefix, "Wing"));
            _weapon = ShipComponentAttributeScaleLogic.BuildGroup(stats.weaponTransforms);
            _engine = ShipComponentAttributeScaleLogic.BuildGroup(
                ChassisComponentStats.FilterLegacyAttributeScaleTransforms(
                    stats.engineTransforms, _familyPrefix, "Engine"));
            _thruster = ShipComponentAttributeScaleLogic.BuildGroup(
                ChassisComponentStats.FilterLegacyAttributeScaleTransforms(
                    stats.thrusterTransforms, _familyPrefix, "Thruster"));
            // Tail + Fin share the Tail Part Profile (turnSpeed → RotationSpeed).
            _tail = ShipComponentAttributeScaleLogic.BuildGroup(
                ChassisComponentStats.FilterLegacyTailAttributeScaleTransforms(
                    stats.tailTransforms, _familyPrefix));
            _part = ShipComponentAttributeScaleLogic.BuildGroup(
                ChassisComponentStats.FilterLegacyAttributeScaleTransforms(
                    stats.partTransforms, _familyPrefix, "Part"));

            // --- Optional Hull root (cockpit body grow) ---
            // [TITAN-ORBIT] Some chassis put the body under a Hull node. Append then prune so we
            // do not scale Hull and a nested cockpit child together (world scale would compound).
            Transform hull = transform.Find("Hull");
            if (hull != null)
            {
                _cockpit.Transforms.Add(hull);
                _cockpit.BaseScales.Add(hull.localScale);
                _cockpit.BasePositions.Add(hull.localPosition);
                ShipComponentAttributeScaleLogic.PruneNestedTransforms(ref _cockpit);
            }

            // --- Cross-bucket nesting (Cover under Wing, EngineComp under Engine, …) ---
            // [TITAN-ORBIT] Part Calc ProfileSet classifies many cosmetics into scale buckets.
            // Scaling a child after its parent already grew multiplies in world space — felt like
            // every ability upgrade made the whole ship swell. Outermost mounts only.
            ShipComponentAttributeScaleLogic.PruneNestedAcrossGroups(
                ref _cockpit,
                ref _wing,
                ref _weapon,
                ref _engine,
                ref _thruster,
                ref _tail,
                ref _part);

            _initialized = _cockpit.Transforms.Count > 0
                || _wing.Transforms.Count > 0
                || _weapon.Transforms.Count > 0
                || _engine.Transforms.Count > 0
                || _thruster.Transforms.Count > 0
                || _tail.Transforms.Count > 0
                || _part.Transforms.Count > 0;

            TryApplyAttributeScale(force: true);
        }

        /// <summary>
        /// Applies mesh scale when upgrades or the cached territory mult change.
        /// Remotes use 1× territory (only the local owner cache is meaningful for thruster grow).
        /// </summary>
        void TryApplyAttributeScale(bool force = false)
        {
            if (!_initialized || _shipEntity == Entity.Null)
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!em.Exists(_shipEntity))
                return;

            if (em.HasComponent<ShipState>(_shipEntity))
            {
                var ship = em.GetComponentData<ShipState>(_shipEntity);
                if (ship.IsDead)
                    return;
            }

            if (!em.HasComponent<ShipAttributeUpgradeState>(_shipEntity))
                return;

            var attrs = em.GetComponentData<ShipAttributeUpgradeState>(_shipEntity);

            // --- Territory thruster grow (sticky cache from predicted drive) ---
            // [TITAN-ORBIT] LocalOwnerTerritoryMult is only published for the local owner.
            // Prefer GhostOwnerIsLocal, but also accept LocalPlayerShipTag — NetCode can briefly
            // disable GhostOwnerIsLocal around Instantiates while the tag still marks our hull.
            float territoryMult = 1f;
            bool isLocalOwner =
                (em.HasComponent<GhostOwnerIsLocal>(_shipEntity) &&
                 em.IsComponentEnabled<GhostOwnerIsLocal>(_shipEntity)) ||
                em.HasComponent<LocalPlayerShipTag>(_shipEntity);
            if (isLocalOwner)
                territoryMult = PlanetConnectionGraphCache.LocalOwnerTerritoryMult;

            // Skip when neither upgrades nor a meaningful territory step changed.
            bool attrsSame = attrs.Equals(_lastApplied);
            bool territorySame = math.abs(territoryMult - _lastTerritoryMult) < TerritoryMultApplyEpsilon;
            if (!force && attrsSame && territorySame)
                return;

            // --- Did territory (or first bind) actually step? ---
            // [TITAN-ORBIT] Only this path rescales thruster mounts that parent jet particles.
            // Upgrade-only applies can also move mounts, so refresh whenever we Apply.
            bool territoryStepped = !territorySame;

            _lastApplied = attrs;
            _lastTerritoryMult = territoryMult;
            ShipComponentAttributeScaleLogic.Apply(
                attrs,
                _rates,
                _cockpit,
                _wing,
                _weapon,
                _engine,
                _thruster,
                _tail,
                _part,
                territoryMult);

            // --- Keep thruster jets alive after mount scale ---
            // [HYBRID] Propulsion LateUpdate (order 100) also self-heals stuck ParticleSystems;
            // ForceRefresh makes the next pass skip its "unchanged" early-out immediately.
            if (territoryStepped || force)
                NotifyPropulsionAfterMountScale();
        }

        /// <summary>
        /// Asks the sibling propulsion applier to re-apply emission after we mutated thruster/engine mounts.
        /// Safe when propulsion is missing (proxy without jets).
        /// </summary>
        void NotifyPropulsionAfterMountScale()
        {
            // --- Lazy cache (same GameObject as this applier) ---
            // [UNITY] GetComponent once — Bind/RebuildCache can run before propulsion Bind attaches.
            if (_propulsionVisual == null)
                _propulsionVisual = GetComponent<ShipPropulsionVisualApplier>();

            if (_propulsionVisual != null)
                _propulsionVisual.ForceRefreshEmission();
        }

        void LateUpdate() => TryApplyAttributeScale();
    }
}
