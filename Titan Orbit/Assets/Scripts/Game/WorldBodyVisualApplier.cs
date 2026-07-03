using System.Collections.Generic;
using SpaceGraphicsToolkit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Instantiates legacy planet/asteroid prefabs as ECS presentation proxies.</summary>
    public static class WorldBodyVisualApplier
    {
        const string DefaultPlanetPoolPath = "Assets/Data/PlanetMaterialPool.asset";

        static readonly HashSet<string> StripComponentNames = new HashSet<string>
        {
            "NetworkObject",
            "NetworkBehaviour",
            "ToroidalRenderer",
            "Planet",
            "HomePlanet",
            "PlanetOrbitZone",
            "PlanetGemMoon",
            "PlanetRingsDrawer",
            "HomePlanetRingsDrawer",
            "PlanetOrbitZone",
            "OrbitZoneVisual",
            "Asteroid",
        };

        public static bool TryCreatePlanetVisual(
            GameObject homePrefab,
            GameObject neutralPrefab,
            PlanetMaterialPool materialPool,
            bool isHome,
            TeamId team,
            int planetLevel,
            int planetId,
            float worldScale,
            out GameObject instance)
        {
            instance = null;
            GameObject prefab = isHome ? homePrefab : neutralPrefab;
            if (prefab == null)
                prefab = neutralPrefab != null ? neutralPrefab : homePrefab;
            if (prefab == null)
                return false;

            instance = Object.Instantiate(prefab);
            instance.name = isHome ? "HomePlanetProxy" : "PlanetTagProxy";
            StripForProxy(instance);
            RemoveUiChildren(instance);
            ApplyPlanetMaterial(instance, materialPool, isHome, team, planetId);
            instance.transform.localScale = Vector3.one * Mathf.Max(0.25f, worldScale);

            var stats = instance.GetComponent<PlanetWorldStatsLabel>();
            if (stats == null)
                stats = instance.AddComponent<PlanetWorldStatsLabel>();
            stats.Configure(planetId);

            var moon = instance.GetComponent<PlanetGemMoonVisualProxy>();
            if (moon == null)
                moon = instance.AddComponent<PlanetGemMoonVisualProxy>();
            Material moonMaterial = CreateGemMoonMaterial(instance, materialPool, isHome, team, planetId);
            moon.Configure(worldScale, planetLevel, isHome, planetId, moonMaterial, team);
            EnsurePlanetSpin(instance);
            EnsureOrbitRingVisual(instance, worldScale, planetLevel, team, isHome, planetId);
            return true;
        }

        static void EnsurePlanetSpin(GameObject planetRoot)
        {
            if (planetRoot == null)
                return;

            if (planetRoot.GetComponent<PlanetSpinVisualProxy>() == null)
                planetRoot.AddComponent<PlanetSpinVisualProxy>();
        }

        static void EnsureOrbitRingVisual(
            GameObject planetRoot,
            float planetSize,
            int planetLevel,
            TeamId team,
            bool isHome,
            int planetId)
        {
            if (planetRoot == null)
                return;

            Transform ringsRoot = planetRoot.transform.Find("PlanetRings");
            if (ringsRoot == null)
            {
                var ringsGo = new GameObject("PlanetRings");
                ringsGo.transform.SetParent(planetRoot.transform, false);
                ringsRoot = ringsGo.transform;
            }

            var ringVisual = ringsRoot.GetComponent<PlanetOrbitRingVisual>();
            if (ringVisual == null)
                ringVisual = ringsRoot.gameObject.AddComponent<PlanetOrbitRingVisual>();
            ringVisual.Configure(planetRoot.transform, planetSize, planetLevel, team, isHome, planetId);
        }

        public static Material CreateGemMoonMaterial(
            GameObject planetRoot,
            PlanetMaterialPool pool,
            bool isHome,
            TeamId team,
            int planetId)
        {
            Material baseMat = TryGetPlanetSurfaceMaterial(planetRoot);
            if (baseMat == null)
                baseMat = ResolvePlanetMaterial(pool, isHome, team, planetId);
            if (baseMat == null)
                return null;

            var moonMat = new Material(baseMat);

            if (!isHome && team != TeamId.None && pool != null)
            {
                Material neutralMat = ResolvePlanetMaterial(pool, isHome: false, TeamId.None, planetId);
                if (neutralMat != null)
                {
                    Color neutralBase = GetMaterialColor(neutralMat);
                    Color teamColor = team.ToColor();
                    Color tinted = Color.Lerp(neutralBase, teamColor, 0.2f);
                    if (moonMat.HasProperty("_Color"))
                        moonMat.SetColor("_Color", tinted);
                    if (moonMat.HasProperty("_BaseColor"))
                        moonMat.SetColor("_BaseColor", tinted);
                }
            }

            StripWaterFromGemMoonMaterial(moonMat);
            return moonMat;
        }

        static Material TryGetPlanetSurfaceMaterial(GameObject planetRoot)
        {
            if (planetRoot == null)
                return null;

            var sgtPlanet = planetRoot.GetComponentInChildren<SgtPlanet>(true);
            if (sgtPlanet != null && sgtPlanet.Material != null)
                return sgtPlanet.Material;

            foreach (var renderer in planetRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;
                if (renderer.gameObject.name.Contains("GemMoonVisual"))
                    continue;
                if (renderer.sharedMaterial != null)
                    return renderer.sharedMaterial;
            }

            return null;
        }

        public static void StripWaterFromGemMoonMaterial(Material material)
        {
            if (material == null)
                return;
            if (material.HasProperty("_HasWater"))
                material.SetFloat("_HasWater", 0f);
            if (material.HasProperty("_WaterLevel"))
                material.SetFloat("_WaterLevel", -2f);
        }

        static Color GetMaterialColor(Material material)
        {
            if (material == null)
                return Color.white;
            if (material.HasProperty("_BaseColor"))
                return material.GetColor("_BaseColor");
            if (material.HasProperty("_Color"))
                return material.GetColor("_Color");
            return Color.white;
        }

        const float MinAsteroidRadius = 0.35f;
        const float BaseTextureTiling = 8f;
        const float TextureScaleRandomMin = 0.12f;
        const float TextureScaleRandomMax = 7f;
        const float BumpScaleMin = 0.15f;
        const float BumpScaleMax = 5f;
        const float DisplacementMin = 0.025f;
        const float DisplacementMax = 0.32f;
        const float DetailTilingMin = 12f;
        const float DetailTilingMax = 140f;

        static readonly int ShaderIdTiling = Shader.PropertyToID("_Tiling");
        static readonly int ShaderIdBumpScale = Shader.PropertyToID("_BumpScale");
        static readonly int ShaderIdDetailTiling = Shader.PropertyToID("_DetailTiling");

        public static bool TryCreateAsteroidVisual(
            GameObject asteroidPrefab,
            Vector3 worldPosition,
            float worldScale,
            out GameObject instance)
        {
            instance = null;
            if (asteroidPrefab == null)
                return false;

            instance = Object.Instantiate(asteroidPrefab);
            instance.name = "AsteroidTagProxy";
            StripForProxy(instance);

            float target = Mathf.Max(0.25f, worldScale);
            instance.transform.localScale = Vector3.one * target;
            instance.transform.position = worldPosition;
            ApplyAsteroidSurfaceVariation(instance, worldPosition, target);
            EnsureAsteroidSpin(instance, worldPosition);
            return true;
        }

        public static void EnsureAsteroidSpin(GameObject root, Vector3 worldPosition)
        {
            if (root == null)
                return;

            var spin = root.GetComponent<AsteroidSpinVisualProxy>();
            if (spin == null)
                spin = root.AddComponent<AsteroidSpinVisualProxy>();
            spin.Configure(worldPosition);
        }

        /// <summary>Same Barren asteroid texture for every rock; vary UV scale, normals, and displacement per instance.</summary>
        static void ApplyAsteroidSurfaceVariation(GameObject root, Vector3 worldPosition, float rawSize)
        {
            if (rawSize < 0.01f)
                return;

            var sgt = root.GetComponentInChildren<SgtPlanet>(true);
            if (sgt == null || sgt.Material == null || !sgt.Material.HasProperty(ShaderIdTiling))
                return;

            int seed = unchecked((int)((long)(worldPosition.x * 1000) * 73856093
                ^ (long)(worldPosition.z * 1000) * 19349663
                ^ (long)(worldPosition.y * 100) * 83492791));
            var rng = new System.Random(seed);

            float sizeTiling = BaseTextureTiling * (rawSize / MinAsteroidRadius);
            float scaleMul = Mathf.Lerp(TextureScaleRandomMin, TextureScaleRandomMax, (float)rng.NextDouble());
            sgt.Properties.SetFloat(ShaderIdTiling, sizeTiling * scaleMul);

            if (sgt.Material.HasProperty(ShaderIdBumpScale))
            {
                float bump = Mathf.Lerp(BumpScaleMin, BumpScaleMax, (float)rng.NextDouble());
                sgt.Properties.SetFloat(ShaderIdBumpScale, bump);
            }

            if (sgt.Material.HasProperty(ShaderIdDetailTiling))
            {
                float detailTiling = Mathf.Lerp(DetailTilingMin, DetailTilingMax, (float)rng.NextDouble());
                sgt.Properties.SetFloat(ShaderIdDetailTiling, detailTiling);
            }

            sgt.Displacement = Mathf.Lerp(DisplacementMin, DisplacementMax, (float)rng.NextDouble());
            sgt.DirtyMesh();
        }

        public static void ApplyPlanetMaterial(GameObject root, PlanetMaterialPool pool, bool isHome, TeamId team, int planetId)
        {
            Material mat = ResolvePlanetMaterial(pool, isHome, team, planetId);
            if (mat == null)
                return;
            ApplyMaterialToSgtPlanets(root, mat);
        }

        static Material ResolvePlanetMaterial(PlanetMaterialPool pool, bool isHome, TeamId team, int planetId)
        {
            if (pool == null)
                return null;

            if (isHome && team != TeamId.None)
                return pool.GetMaterial(TeamToTropicalIndex(team), useWaterList: true);

            int seed = planetId != 0 ? planetId : 17;
            int index = Mathf.Abs(seed) % Mathf.Max(1, pool.Materials?.Count ?? 1);
            return pool.GetMaterial(index, useWaterList: false);
        }

        static int TeamToTropicalIndex(TeamId team)
        {
            switch (team)
            {
                case TeamId.TeamA: return 0;
                case TeamId.TeamB: return 1;
                case TeamId.TeamC: return 2;
                default: return 0;
            }
        }

        static void ApplyMaterialToSgtPlanets(GameObject root, Material mat)
        {
            if (mat == null)
                return;

            var sgtPlanets = root.GetComponentsInChildren<SgtPlanet>(true);
            for (int i = 0; i < sgtPlanets.Length; i++)
            {
                if (sgtPlanets[i] != null)
                    sgtPlanets[i].Material = mat;
            }

            if (sgtPlanets.Length > 0)
                return;

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;
                if (renderer.gameObject.name.Contains("PopulationText"))
                    continue;
                renderer.sharedMaterial = mat;
            }
        }

        public static Material CreateLitMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            mat.color = color;
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            return mat;
        }

        public static void StripForProxy(GameObject root)
        {
            ShipVisualApplier.StripPhysicsAndNetworking(root);

            var components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                    continue;
                if (component is Transform || component is SgtPlanet)
                    continue;

                string typeName = component.GetType().Name;
                if (StripComponentNames.Contains(typeName))
                    Object.Destroy(component);
            }
        }

        static void RemoveUiChildren(GameObject root)
        {
            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = root.transform.GetChild(i);
                if (child.name.Contains("PopulationText"))
                    continue;
                if (child.GetComponent<Canvas>() != null)
                    Object.Destroy(child.gameObject);
            }
        }

        public static PlanetMaterialPool LoadDefaultMaterialPool()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<PlanetMaterialPool>(DefaultPlanetPoolPath);
#else
            return null;
#endif
        }
    }
}
