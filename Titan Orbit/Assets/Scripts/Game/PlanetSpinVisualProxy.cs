using System.Collections.Generic;
using SpaceGraphicsToolkit;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Cosmetic spin for planet body and level rings around the level-band tilt axis from
    /// <see cref="PlanetOrbitMath.GetLevelBandsSpinAxisLocal"/>. Gem moon and UI labels stay parented
    /// outside the spin pivot so they do not rotate with the mesh. Render only — no sim impact.
    /// </summary>
    public class PlanetSpinVisualProxy : MonoBehaviour
    {
        /// <summary>Slow decorative rotation rate (degrees per second).</summary>
        const float SpinDegreesPerSecond = 2f;
        const string SpinPivotName = "PlanetSpinPivot";
        const string PlanetBodyName = "PlanetBody";

        /// <summary>Children that must not be reparented under the spin pivot (moon, labels).</summary>
        static readonly HashSet<string> NonSpinningChildNames = new HashSet<string>
        {
            "GemMoonVisual",
            "PopulationText",
            "PlanetStatsLabel",
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

        /// <summary>Moves existing PlanetBody child under the spin pivot if not already there.</summary>
        void MigratePlanetBodyToPivot()
        {
            // --- MigratePlanetBodyToPivot ---
            if (_spinPivot.Find(PlanetBodyName) != null)
                return;

            var sgt = GetComponent<SgtPlanet>();
            if (sgt == null)
                return;

            var bodyGo = new GameObject(PlanetBodyName);
            bodyGo.transform.SetParent(_spinPivot, false);
            var newSgt = bodyGo.AddComponent<SgtPlanet>();
            CopySgtPlanet(sgt, newSgt);

            var waterGradient = GetComponent<SgtPlanetWaterGradient>();
            if (waterGradient != null)
            {
                var newGradient = bodyGo.AddComponent<SgtPlanetWaterGradient>();
                CopyWaterGradient(waterGradient, newGradient);
                Destroy(waterGradient);
            }

            var waterTexture = GetComponent<SgtPlanetWaterTexture>();
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

        public void KeepOnPlanetRoot(Transform child)
        {
            // --- KeepOnPlanetRoot ---
            if (child == null || child.parent == transform)
                return;

            child.SetParent(transform, true);
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
