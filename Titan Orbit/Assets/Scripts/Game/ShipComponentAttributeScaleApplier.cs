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
    /// when the ship is inside a friendly territory triangle (Engine/Thruster mounts grow), and when
    /// OVERDRIVE is active (Thruster mounts bloom with the baked overdrive speed mul).
    /// Attached by EcsWorldVisualizer.
    /// <para>
    /// Growth rates come from <c>ShipFamilyPartCalcProfileSet.asset</c> Part Profiles
    /// (<c>perLevel / base</c> via <see cref="ShipComponentAttributeScaleLogic.BuildRatesFromProfileSet"/>).
    /// Collider rebuilds via <see cref="ShipHullColliderLogic"/> use attribute size only
    /// (territory / overdrive are presentation-only).
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Territory boosts are <b>smoothed</b>; OVERDRIVE thruster bloom <b>snaps</b>
    /// with <see cref="ShipOverdriveTuning.IsBurstActive"/> (same rule as the motor — pending
    /// Shift/Thrust + ghosted energy/lockout). Thruster VFX is <b>not</b> ForceRefresh'd on boost
    /// changes — that restart was the blink; <see cref="ShipPropulsionVisualApplier"/> self-heals.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(95)]
    public class ShipComponentAttributeScaleApplier : MonoBehaviour
    {
        /// <summary>How fast territory display muls approach their targets (mult units per second).</summary>
        const float TerritoryScaleTransitionPerSecond = 2.5f;

        /// <summary>Treat display and target as equal within this (avoid endless Apply writes).</summary>
        const float BoostDisplayEpsilon = 0.002f;

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

        /// <summary>Instant target from ECS / graph cache (may jump).</summary>
        float _targetTerritoryMult = 1f;
        /// <summary>Instant OVERDRIVE target (1 or baked speed mul).</summary>
        float _targetOverdriveMult = 1f;

        /// <summary>Smoothed territory mul actually applied to meshes this frame.</summary>
        float _displayTerritoryMult = 1f;
        /// <summary>Smoothed overdrive mul actually applied to thruster meshes.</summary>
        float _displayOverdriveMult = 1f;

        /// <summary>Last display muls written to Transform — skip Apply when unchanged.</summary>
        float _lastAppliedTerritoryMult = -1f;
        float _lastAppliedOverdriveMult = -1f;

        /// <summary>
        /// Cached propulsion applier on the same hull proxy. Null until first upgrade apply
        /// that needs a VFX refresh after mount scale (attribute grow only — not boost lerp).
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
            _targetTerritoryMult = 1f;
            _targetOverdriveMult = 1f;
            _displayTerritoryMult = 1f;
            _displayOverdriveMult = 1f;
            _lastAppliedTerritoryMult = -1f;
            _lastAppliedOverdriveMult = -1f;
            RebuildCache();
        }

        /// <summary>
        /// Scans hull hierarchy via shared <see cref="ShipComponentAttributeScaleLogic.BuildGroupsFromHierarchy"/>
        /// (same grouping as PhysicsCollider bake), loads ProfileSet rates, stores base scales/positions.
        /// </summary>
        void RebuildCache()
        {
            // --- ProfileSet percent-of-base rates (version 1) ---
            var profileSet = ShipFamilyPartCalcProfileSet.LoadShared();
            _rates = ShipComponentAttributeScaleLogic.BuildRatesFromProfileSet(profileSet);

            // --- Same USC groups as ShipHullColliderLogic bake ---
            ShipComponentAttributeScaleLogic.BuildGroupsFromHierarchy(
                transform,
                _familyPrefix,
                out _cockpit,
                out _wing,
                out _weapon,
                out _engine,
                out _thruster,
                out _tail,
                out _part);

            _initialized = (_cockpit.Transforms != null && _cockpit.Transforms.Count > 0)
                || (_wing.Transforms != null && _wing.Transforms.Count > 0)
                || (_weapon.Transforms != null && _weapon.Transforms.Count > 0)
                || (_engine.Transforms != null && _engine.Transforms.Count > 0)
                || (_thruster.Transforms != null && _thruster.Transforms.Count > 0)
                || (_tail.Transforms != null && _tail.Transforms.Count > 0)
                || (_part.Transforms != null && _part.Transforms.Count > 0);

            TryApplyAttributeScale(force: true);
        }

        /// <summary>
        /// Resolves boost targets, smoothly approaches them, and writes mesh scales.
        /// Remotes use 1× territory / overdrive (only the local owner cache is meaningful).
        /// </summary>
        /// <param name="force">Snap display to targets and rebuild (Bind / cache rebuild).</param>
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

            // --- Resolve instant targets (local owner only) ---
            float targetTerritory = 1f;
            float targetOverdrive = 1f;
            bool isLocalOwner =
                (em.HasComponent<GhostOwnerIsLocal>(_shipEntity) &&
                 em.IsComponentEnabled<GhostOwnerIsLocal>(_shipEntity)) ||
                em.HasComponent<LocalPlayerShipTag>(_shipEntity);
            if (isLocalOwner)
            {
                // Sticky graph cache — still can step when sticky expires; we smooth below.
                targetTerritory = Mathf.Max(1f, PlanetConnectionGraphCache.LocalOwnerTerritoryMult);

                // OVERDRIVE bloom: same IsBurstActive rule as the motor (pending Shift/Thrust +
                // ghosted energy / OverdriveLockout). Snapped below — not eased.
                if (em.HasComponent<ShipState>(_shipEntity))
                {
                    var ship = em.GetComponentData<ShipState>(_shipEntity);
                    bool thrustHeld;
                    bool shiftHeld;
                    if (ShipPendingInput.HasValue)
                    {
                        thrustHeld = ShipPendingInput.Latest.Thrust;
                        shiftHeld = ShipPendingInput.Latest.Overdrive;
                    }
                    else if (em.HasComponent<ShipInput>(_shipEntity))
                    {
                        var input = em.GetComponentData<ShipInput>(_shipEntity);
                        thrustHeld = input.Thrust;
                        shiftHeld = input.Overdrive;
                    }
                    else
                    {
                        thrustHeld = false;
                        shiftHeld = false;
                    }

                    // Presentation never runs the orbit motor — useOrbit false for bloom.
                    // MEGAs have no overdrive (Shift is heading-lock); never bloom them.
                    bool isMega = em.HasComponent<MegaShipState>(_shipEntity)
                        && em.GetComponentData<MegaShipState>(_shipEntity).IsMega;
                    if (!isMega && ShipOverdriveTuning.IsBurstActive(
                            shiftHeld,
                            thrustHeld,
                            useOrbit: false,
                            ship.CurrentEnergy,
                            ship.OverdriveLockout))
                    {
                        if (em.HasComponent<ShipMotorConfig>(_shipEntity))
                        {
                            var motor = em.GetComponentData<ShipMotorConfig>(_shipEntity);
                            targetOverdrive = ShipOverdriveTuning.ResolveSpeedMultiplier(motor);
                        }
                        else
                            targetOverdrive = ShipOverdriveTuning.SpeedMultiplier;
                    }
                }
            }

            _targetTerritoryMult = targetTerritory;
            _targetOverdriveMult = targetOverdrive;

            // --- Territory: smooth; OVERDRIVE: snap (synced with speed hard-cap on OD exit) ---
            if (force)
            {
                _displayTerritoryMult = _targetTerritoryMult;
                _displayOverdriveMult = _targetOverdriveMult;
            }
            else
            {
                float step = TerritoryScaleTransitionPerSecond * Time.deltaTime;
                _displayTerritoryMult = Mathf.MoveTowards(
                    _displayTerritoryMult, _targetTerritoryMult, step);
                _displayOverdriveMult = _targetOverdriveMult;
            }

            bool attrsSame = attrs.Equals(_lastApplied);
            bool displaySettled =
                math.abs(_displayTerritoryMult - _targetTerritoryMult) < BoostDisplayEpsilon &&
                math.abs(_displayOverdriveMult - _targetOverdriveMult) < BoostDisplayEpsilon;
            bool displayUnchanged =
                math.abs(_displayTerritoryMult - _lastAppliedTerritoryMult) < BoostDisplayEpsilon &&
                math.abs(_displayOverdriveMult - _lastAppliedOverdriveMult) < BoostDisplayEpsilon;

            // Skip Transform writes when upgrades idle and display already matches last apply.
            if (!force && attrsSame && displaySettled && displayUnchanged)
                return;

            bool attrsChanged = !attrsSame;
            _lastApplied = attrs;
            _lastAppliedTerritoryMult = _displayTerritoryMult;
            _lastAppliedOverdriveMult = _displayOverdriveMult;

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
                _displayTerritoryMult,
                _displayOverdriveMult);

            // --- VFX: never ForceRefresh on boost lerp ---
            // [TITAN-ORBIT] ForceRefreshEmission Stop+Clear+Play caused thruster blink whenever
            // territory/overdrive stepped. Propulsion LateUpdate already restarts stopped particles
            // while thrust is held. Only nudge after attribute mesh grow (upgrade tick / bind).
            if (attrsChanged || force)
                NotifyPropulsionAfterMountScale();
        }

        /// <summary>
        /// Asks the sibling propulsion applier to re-apply emission after attribute mount grow.
        /// Not used for territory/overdrive smooth transitions.
        /// </summary>
        void NotifyPropulsionAfterMountScale()
        {
            if (_propulsionVisual == null)
                _propulsionVisual = GetComponent<ShipPropulsionVisualApplier>();

            if (_propulsionVisual != null)
                _propulsionVisual.ForceRefreshEmission();
        }

        void LateUpdate() => TryApplyAttributeScale();
    }
}
