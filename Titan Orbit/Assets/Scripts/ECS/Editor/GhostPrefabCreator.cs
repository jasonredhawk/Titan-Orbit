#if UNITY_EDITOR
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;
using Unity.NetCode;
using Unity.Entities;
using Unity.Entities.Hybrid.Baking;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// Editor menu utility that creates ECS ghost prefabs (ship, planet, asteroid, gem, transport)
    /// and the GamePrefabsRegistry asset under Assets/Prefabs/ECS/. Run once per project setup.
    /// <para>
    /// Map bodies must be interpolated-only with <c>HasOwner = false</c> and Static optimize.
    /// Short-lived movers (people transports) use Dynamic + higher Importance instead —
    /// Static + Importance 1 starves mid-flight pose updates under MaxSendChunks.
    /// </para>
    /// </summary>
    public static class GhostPrefabCreator
    {
        /// <summary>Sole project path for the shared map-generation ScriptableObject (Resources).</summary>
        const string MapGenerationSettingsPath = "Assets/Resources/MapGenerationSettings.asset";

        /// <summary>Menu entry — creates all ECS ghost prefabs and the registry asset.</summary>
        [MenuItem("Titan Orbit/Create Ghost Prefabs")]
        public static void CreateGhostPrefabs()
        {
            // --- Folders + shared data ---
            EnsureDirectory("Assets/Prefabs/ECS");
            EnsureDirectory("Assets/Data");
            EnsureMapGenerationSettingsAsset();

            // --- Prefabs ---
            CreateShipPrefab();
            CreatePlanetPrefab();
            CreateAsteroidPrefab();
            CreateGemPrefab();
            CreatePeopleTransportPrefab();
            CreateRegistryPrefab();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[GhostPrefabCreator] Created ECS ghost prefabs under Assets/Prefabs/ECS/");
        }

        /// <summary>Creates nested AssetDatabase folders if missing.</summary>
        /// <param name="path">Unity asset path using forward slashes.</param>
        static void EnsureDirectory(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                var parts = path.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }
        }

        /// <summary>Adds LinkedEntityGroup + GhostAuthoring for an owned player ship ghost.</summary>
        /// <param name="go">Root GameObject that will become the prefab.</param>
        static void AddOwnedGhostRootComponents(GameObject go)
        {
            if (go.GetComponent<LinkedEntityGroupAuthoring>() == null)
                go.AddComponent<LinkedEntityGroupAuthoring>();

            // [NETCODE] Ships need ownership for command targeting. DefaultGhostMode is
            // Predicted (not OwnerPredicted) with SupportedGhostModes All so
            // ShipClientPredictionSwitchSystem can interpolate remotes on each client.
            // OwnerPredicted cannot be switched on demand.
            var ghost = go.AddComponent<GhostAuthoringComponent>();
            ghost.HasOwner = true;
            ghost.SupportAutoCommandTarget = true;
            ghost.DefaultGhostMode = GhostMode.Predicted;
            ghost.SupportedGhostModes = GhostModeMask.All;
        }

        /// <summary>
        /// Adds LinkedEntityGroup + GhostAuthoring for map / world objects (no owner, interpolated).
        /// </summary>
        /// <param name="go">Root GameObject that will become the prefab.</param>
        static void AddMapGhostRootComponents(GameObject go)
        {
            if (go.GetComponent<LinkedEntityGroupAuthoring>() == null)
                go.AddComponent<LinkedEntityGroupAuthoring>();

            // [NETCODE] Map ghosts are world objects — never player-owned.
            // [TITAN-ORBIT] Interpolated + StaticOptimize reduces join-time GhostSpawn cost.
            var ghost = go.AddComponent<GhostAuthoringComponent>();
            ghost.HasOwner = false;
            ghost.SupportAutoCommandTarget = false;
            ghost.DefaultGhostMode = GhostMode.Interpolated;
            ghost.SupportedGhostModes = GhostModeMask.Interpolated;
            ghost.OptimizationMode = GhostOptimizationMode.Static;
            ghost.RollbackPredictionOnStructuralChanges = false;
            // [NETCODE] MaxSendRate (Hz) — 0 = every NetworkTick. Cap map resends so join bandwidth
            // prefers ships (Importance 100). First send of a new ghost is not deferred by this.
            ghost.Importance = 1;
            ghost.MaxSendRate = 2;
        }

        /// <summary>
        /// Adds LinkedEntityGroup + GhostAuthoring for short-lived moving projectiles (people transports).
        /// Dynamic + high importance so mid-flight LocalTransform keeps updating under MaxSendChunks caps.
        /// Do NOT use <see cref="AddMapGhostRootComponents"/> — Static + Importance 1 starves pose updates.
        /// </summary>
        /// <param name="go">Root GameObject that will become the prefab.</param>
        static void AddProjectileGhostRootComponents(GameObject go)
        {
            if (go.GetComponent<LinkedEntityGroupAuthoring>() == null)
                go.AddComponent<LinkedEntityGroupAuthoring>();

            // [NETCODE] Interpolated projectile — no owner, changes every tick (Dynamic).
            var ghost = go.AddComponent<GhostAuthoringComponent>();
            ghost.HasOwner = false;
            ghost.SupportAutoCommandTarget = false;
            ghost.DefaultGhostMode = GhostMode.Interpolated;
            ghost.SupportedGhostModes = GhostModeMask.Interpolated;
            ghost.OptimizationMode = GhostOptimizationMode.Dynamic;
            ghost.RollbackPredictionOnStructuralChanges = false;
            // Below ships (100), far above asteroids/planets (1) so flight poses win snapshot budget.
            ghost.Importance = 60;
            ghost.MaxSendRate = 0;
        }

        /// <summary>Creates PeopleTransportGhost (interpolated Dynamic projectile, no owner).</summary>
        static void CreatePeopleTransportPrefab()
        {
            var go = new GameObject("PeopleTransportGhost");
            AddProjectileGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.PeopleTransportGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/PeopleTransportGhost.prefab");
            Object.DestroyImmediate(go);
        }

        /// <summary>Creates StarshipGhost (owned / predicted player ship).</summary>
        static void CreateShipPrefab()
        {
            var go = new GameObject("StarshipGhost");
            AddOwnedGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.StarshipGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/StarshipGhost.prefab");
            Object.DestroyImmediate(go);
        }

        /// <summary>Creates PlanetGhost (interpolated map ghost — higher send rate than asteroids).</summary>
        static void CreatePlanetPrefab()
        {
            var go = new GameObject("PlanetGhost");
            AddPlanetGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.PlanetGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/PlanetGhost.prefab");
            Object.DestroyImmediate(go);
        }

        /// <summary>
        /// LinkedEntityGroup + GhostAuthoring for planets: Ownership / population must replicate
        /// faster than asteroids so connection lines + minimap do not lag captures. Still below
        /// ships (100). Paired with <see cref="PlanetOwnershipChangedRpc"/> for immediate UI.
        /// </summary>
        static void AddPlanetGhostRootComponents(GameObject go)
        {
            if (go.GetComponent<LinkedEntityGroupAuthoring>() == null)
                go.AddComponent<LinkedEntityGroupAuthoring>();

            var ghost = go.AddComponent<GhostAuthoringComponent>();
            ghost.HasOwner = false;
            ghost.SupportAutoCommandTarget = false;
            ghost.DefaultGhostMode = GhostMode.Interpolated;
            ghost.SupportedGhostModes = GhostModeMask.Interpolated;
            ghost.OptimizationMode = GhostOptimizationMode.Static;
            ghost.RollbackPredictionOnStructuralChanges = false;
            ghost.Importance = 40;
            ghost.MaxSendRate = 4;
        }

        /// <summary>Creates AsteroidGhost (interpolated map ghost, no owner).</summary>
        static void CreateAsteroidPrefab()
        {
            var go = new GameObject("AsteroidGhost");
            AddMapGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.AsteroidGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/AsteroidGhost.prefab");
            Object.DestroyImmediate(go);
        }

        /// <summary>
        /// Creates GemGhost (interpolated Dynamic pickup — not Static map optimize).
        /// Gems move every tick during burst/tractor; Static + MaxSendRate 2 starved pose updates.
        /// </summary>
        static void CreateGemPrefab()
        {
            var go = new GameObject("GemGhost");
            AddGemGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.GemGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/GemGhost.prefab");
            Object.DestroyImmediate(go);
        }

        /// <summary>
        /// LinkedEntityGroup + GhostAuthoring for gem pickups: Interpolated, Dynamic, Importance 50,
        /// MaxSendRate 30 — matches live Assets/Prefabs/ECS/GemGhost.prefab.
        /// </summary>
        static void AddGemGhostRootComponents(GameObject go)
        {
            if (go.GetComponent<LinkedEntityGroupAuthoring>() == null)
                go.AddComponent<LinkedEntityGroupAuthoring>();

            var ghost = go.AddComponent<GhostAuthoringComponent>();
            ghost.HasOwner = false;
            ghost.SupportAutoCommandTarget = false;
            ghost.DefaultGhostMode = GhostMode.Interpolated;
            ghost.SupportedGhostModes = GhostModeMask.Interpolated;
            ghost.OptimizationMode = GhostOptimizationMode.Dynamic;
            ghost.RollbackPredictionOnStructuralChanges = false;
            ghost.Importance = 50;
            ghost.MaxSendRate = 30;
        }

        /// <summary>Creates GamePrefabsRegistry that points at all ghost prefabs + map settings.</summary>
        static void CreateRegistryPrefab()
        {
            var ship = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ECS/StarshipGhost.prefab");
            var planet = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ECS/PlanetGhost.prefab");
            var asteroid = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ECS/AsteroidGhost.prefab");
            var gem = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ECS/GemGhost.prefab");
            var peopleTransport = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ECS/PeopleTransportGhost.prefab");

            var go = new GameObject("GamePrefabsRegistry");
            var reg = go.AddComponent<TitanOrbit.ECS.Authoring.GamePrefabsRegistryAuthoring>();
            reg.ShipPrefab = ship;
            reg.PlanetPrefab = planet;
            reg.AsteroidPrefab = asteroid;
            reg.GemPrefab = gem;
            reg.PeopleTransportPrefab = peopleTransport;
            reg.MapGenerationSettings = EnsureMapGenerationSettingsAsset();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/GamePrefabsRegistry.prefab");
            Object.DestroyImmediate(go);
        }

        /// <summary>Selects the MapGenerationSettings asset in the Project window.</summary>
        [MenuItem("Titan Orbit/Select Map Generation Settings Asset")]
        public static void SelectMapGenerationSettingsAsset()
        {
            var asset = EnsureMapGenerationSettingsAsset();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        /// <summary>Creates MapGenerationSettings if missing, then selects it.</summary>
        [MenuItem("Titan Orbit/Create Map Generation Settings Asset")]
        public static void CreateMapGenerationSettingsMenuItem()
        {
            EnsureDirectory("Assets/Resources");
            var asset = EnsureMapGenerationSettingsAsset();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            Debug.Log($"[GhostPrefabCreator] Map generation settings at {MapGenerationSettingsPath}");
        }

        /// <summary>
        /// Loads or creates <see cref="MapGenerationSettings"/> at the fixed project path.
        /// </summary>
        /// <returns>Existing or newly created settings asset.</returns>
        public static MapGenerationSettings EnsureMapGenerationSettingsAsset()
        {
            var existing = AssetDatabase.LoadAssetAtPath<MapGenerationSettings>(MapGenerationSettingsPath);
            if (existing != null)
                return existing;

            var asset = ScriptableObject.CreateInstance<MapGenerationSettings>();
            AssetDatabase.CreateAsset(asset, MapGenerationSettingsPath);
            AssetDatabase.SaveAssets();
            return asset;
        }
    }
}
#endif
