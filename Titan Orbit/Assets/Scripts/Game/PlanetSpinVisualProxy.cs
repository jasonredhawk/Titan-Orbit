using System.Collections.Generic;
using SpaceGraphicsToolkit;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Cosmetic spin for the planet mesh around the level-band tilt axis from
    /// <see cref="PlanetOrbitMath.GetLevelBandsSpinAxisLocal"/>.
    /// <para>
    /// Lives on <see cref="PlanetVisualBody"/> (the scaled child). Gem moon, stats labels,
    /// defense pads, and orbit-ring drawers stay on the unit-scale planet root (or as
    /// non-spinning siblings under the body) so they do not rotate with the mesh.
    /// </para>
    /// Render only — no sim impact.
    /// </summary>
    public class PlanetSpinVisualProxy : MonoBehaviour
    {
        /// <summary>Slow decorative rotation rate (degrees per second).</summary>
        const float SpinDegreesPerSecond = 2f;
        const string SpinPivotName = "PlanetSpinPivot";
        const string PlanetBodyName = "PlanetBody";

        /// <summary>
        /// Children that must not be reparented under the spin pivot.
        /// Orbit rings stay as a sibling under <see cref="PlanetVisualBody"/> (scaled, not spun).
        /// </summary>
        static readonly HashSet<string> NonSpinningChildNames = new HashSet<string>
        {
            "GemMoonVisual",
            "PopulationText",
            "PlanetStatsLabel",
            "PlanetRings",
            "PlanetaryDefense",
        };

        Transform _spinPivot;
        float3 _spinAxisLocal;

        void Awake() => EnsureHierarchy();

        /// <summary>Creates spin pivot, migrates PlanetBody, and reparents ring meshes once at load.</summary>
        void EnsureHierarchy()
        {
            // --- Ensure setup ---
            if (_spinPivot != null)
                return;

            _spinPivot = transform.Find(SpinPivotName);
            if (_spinPivot == null)
            {
                var pivotGo = new GameObject(SpinPivotName);
                _spinPivot = pivotGo.transform;
                _spinPivot.SetParent(transform, false);
            }

            MigratePlanetBodyToPivot();
            ReparentSpinningChildren();
            _spinAxisLocal = PlanetOrbitMath.GetLevelBandsSpinAxisLocal();
        }

        /// <summary>
        /// Moves SgtPlanet (and water helpers) under the spin pivot.
        /// Prefab authors put SgtPlanet on the planet root; with unit-scale roots the spin
        /// component lives on <see cref="PlanetVisualBody"/>, so we also search the parent root.
        /// </summary>
        void MigratePlanetBodyToPivot()
        {
            // --- MigratePlanetBodyToPivot ---
            if (_spinPivot.Find(PlanetBodyName) != null)
                return;

            // Prefer components on this object (body); fall back to unit-scale planet root.
            var sgt = GetComponent<SgtPlanet>();
            Transform waterHost = transform;
            if (sgt == null && transform.parent != null)
            {
                sgt = transform.parent.GetComponent<SgtPlanet>();
                if (sgt != null)
                    waterHost = transform.parent;
            }

            if (sgt == null)
                return;

            var bodyGo = new GameObject(PlanetBodyName);
            bodyGo.transform.SetParent(_spinPivot, false);
            var newSgt = bodyGo.AddComponent<SgtPlanet>();
            CopySgtPlanet(sgt, newSgt);

            var waterGradient = waterHost.GetComponent<SgtPlanetWaterGradient>();
            if (waterGradient != null)
            {
                var newGradient = bodyGo.AddComponent<SgtPlanetWaterGradient>();
                CopyWaterGradient(waterGradient, newGradient);
                Destroy(waterGradient);
            }

            var waterTexture = waterHost.GetComponent<SgtPlanetWaterTexture>();
            if (waterTexture != null)
            {
                var newTexture = bodyGo.AddComponent<SgtPlanetWaterTexture>();
                CopyWaterTexture(waterTexture, newTexture);
                Destroy(waterTexture);
            }

            Destroy(sgt);
        }

        static void CopySgtPlanet(SgtPlanet source, SgtPlanet destination)
        {
            // --- CopySgtPlanet ---
            destination.Mesh = source.Mesh;
            destination.MeshCollider = source.MeshCollider;
            destination.Radius = source.Radius;
            destination.Material = source.Material;
            destination.SharedMaterial = source.SharedMaterial;
            destination.CastShadows = source.CastShadows;
            destination.ReceiveShadows = source.ReceiveShadows;
            destination.WaterLevel = source.WaterLevel;
            destination.Displace = source.Displace;
            destination.Displacement = source.Displacement;
            destination.ClampWater = source.ClampWater;
        }

        static void CopyWaterGradient(SgtPlanetWaterGradient source, SgtPlanetWaterGradient destination)
        {
            // --- CopyWaterGradient ---
            destination.Shallow = source.Shallow;
            destination.Deep = source.Deep;
            destination.Ease = source.Ease;
            destination.Sharpness = source.Sharpness;
            destination.Scale = source.Scale;
        }

        static void CopyWaterTexture(SgtPlanetWaterTexture source, SgtPlanetWaterTexture destination)
        {
            // --- CopyWaterTexture ---
            destination.BaseTexture = source.BaseTexture;
            destination.Strength = source.Strength;
            destination.Speed = source.Speed;
        }

        void ReparentSpinningChildren()
        {
            // --- ReparentSpinningChildren ---
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child == _spinPivot)
                    continue;
                if (NonSpinningChildNames.Contains(child.name))
                    continue;
                if (child.GetComponent<Canvas>() != null)
                    continue;

                child.SetParent(_spinPivot, true);
            }
        }

        /// <summary>
        /// Reparents <paramref name="child"/> onto the unit-scale planet proxy root
        /// (parent of <see cref="PlanetVisualBody"/> when this component lives on the body).
        /// </summary>
        public void KeepOnPlanetRoot(Transform child)
        {
            // --- KeepOnPlanetRoot ---
            if (child == null)
                return;

            // Spin lives on PlanetVisualBody — labels/moon belong on the unit pose root above it.
            Transform planetRoot = transform;
            if (transform.parent != null && transform.name == PlanetVisualBody.BodyName)
                planetRoot = transform.parent;

            if (child.parent == planetRoot)
                return;

            child.SetParent(planetRoot, true);
        }

        void LateUpdate()
        {
            // --- Per-frame refresh ---
            if (_spinPivot == null)
                return;

            _spinPivot.Rotate(
                new Vector3(_spinAxisLocal.x, _spinAxisLocal.y, _spinAxisLocal.z),
                SpinDegreesPerSecond * Time.deltaTime,
                Space.Self);
        }
    }
}
