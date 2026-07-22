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
    /// and when the ship is inside a friendly territory triangle (engine/thruster grow like a speed
    /// upgrade — NGO feel). Watches ShipAttributeUpgradeState + territory multiplier on the linked
    /// ship entity. Attached by EcsWorldVisualizer; <b>cosmetic only</b>.
    /// <para>
    /// Territory mult is <see cref="PlanetConnectionGraphCache.LocalOwnerTerritoryMult"/> — sticky and
    /// written only on first-time predicting ticks so NetCode resim / triangle-edge noise cannot
    /// blink engine scale every frame.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(95)]
    public class ShipComponentAttributeScaleApplier : MonoBehaviour
    {
        /// <summary>Ignore tiny float noise; only re-apply on clear boosted↔normal transitions.</summary>
        const float TerritoryMultApplyEpsilon = 0.02f;

        Entity _shipEntity;
        string _familyPrefix = "AstroEagle";
        bool _hasWeaponComponentEnergy;
        bool _initialized;

        ShipComponentAttributeScaleLogic.ScaleGroup _cockpit;
        ShipComponentAttributeScaleLogic.ScaleGroup _wing;
        ShipComponentAttributeScaleLogic.ScaleGroup _weapon;
        ShipComponentAttributeScaleLogic.ScaleGroup _engine;
        ShipComponentAttributeScaleLogic.ScaleGroup _thruster;
        ShipComponentAttributeScaleLogic.ScaleGroup _part;

        ShipAttributeUpgradeState _lastApplied;
        float _lastTerritoryMult = -1f;

        /// <summary>Links to ship entity, caches chassis transform groups, applies initial scale.</summary>
        public void Bind(Entity shipEntity, string familyPrefix, ShipFamilyDefinition family)
        {
            _shipEntity = shipEntity;
            if (!string.IsNullOrWhiteSpace(familyPrefix))
                _familyPrefix = familyPrefix.Trim();
            _hasWeaponComponentEnergy = ShipComponentAttributeScaleLogic.FamilyHasWeaponComponentEnergy(family);
            _lastApplied = default;
            _lastTerritoryMult = -1f;
            RebuildCache();
        }

        /// <summary>Scans hull hierarchy for component transforms and stores base scales/positions.</summary>
        void RebuildCache()
        {
            var stats = ChassisComponentStats.FromTransform(transform, _familyPrefix);

            _cockpit = ShipComponentAttributeScaleLogic.BuildGroup(stats.cockpitTransforms);
            _wing = ShipComponentAttributeScaleLogic.BuildGroup(stats.wingTransforms);
            _weapon = ShipComponentAttributeScaleLogic.BuildGroup(stats.weaponTransforms);
            _engine = ShipComponentAttributeScaleLogic.BuildGroup(stats.engineTransforms);
            _thruster = ShipComponentAttributeScaleLogic.BuildGroup(stats.thrusterTransforms);
            _part = ShipComponentAttributeScaleLogic.BuildGroup(stats.partTransforms);

            Transform hull = transform.Find("Hull");
            if (hull != null)
            {
                _cockpit.Transforms.Add(hull);
                _cockpit.BaseScales.Add(hull.localScale);
                _cockpit.BasePositions.Add(hull.localPosition);
            }

            _initialized = _cockpit.Transforms.Count > 0
                || _wing.Transforms.Count > 0
                || _weapon.Transforms.Count > 0
                || _engine.Transforms.Count > 0
                || _thruster.Transforms.Count > 0
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
            float territoryMult = 1f;
            if (em.HasComponent<GhostOwnerIsLocal>(_shipEntity) &&
                em.IsComponentEnabled<GhostOwnerIsLocal>(_shipEntity))
                territoryMult = PlanetConnectionGraphCache.LocalOwnerTerritoryMult;

            // Skip when neither upgrades nor a meaningful territory step changed.
            bool attrsSame = attrs.Equals(_lastApplied);
            bool territorySame = math.abs(territoryMult - _lastTerritoryMult) < TerritoryMultApplyEpsilon;
            if (!force && attrsSame && territorySame)
                return;

            _lastApplied = attrs;
            _lastTerritoryMult = territoryMult;
            ShipComponentAttributeScaleLogic.Apply(
                attrs,
                _cockpit,
                _wing,
                _weapon,
                _engine,
                _thruster,
                _part,
                _hasWeaponComponentEnergy,
                territoryMult);
        }

        void LateUpdate() => TryApplyAttributeScale();
    }
}
