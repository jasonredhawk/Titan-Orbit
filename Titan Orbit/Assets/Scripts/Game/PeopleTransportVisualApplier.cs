using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Instantiates people-transport flight proxies for <see cref="PeopleTransportVfxDriver"/>.
    /// Clones <c>Resources/PeopleTransport</c> (yellow sphere + nested GenericSpaceship6), strips
    /// physics/network components, and applies per-team GenericSpaceships1-8 materials to ship
    /// child renderers only (root yellow sphere stays yellow).
    /// <para>
    /// Earlier Windows builds used primitive-only proxies after a misdiagnosed Main Menu kick;
    /// Player.log proved that kick was RpcSystem hash mismatch, not this Instantiates path.
    /// </para>
    /// </summary>
    public static class PeopleTransportVisualApplier
    {
        /// <summary>Authored PeopleTransport.prefab root scale (legacy projectile).</summary>
        const float DefaultPrefabBaseUniform = 0.25f;

        /// <summary>Resources key for the flight prefab (Assets/Resources/PeopleTransport.prefab).</summary>
        const string ResourcesPrefabPath = "PeopleTransport";

        /// <summary>Editor / project path to the GenericSpaceships1-8 colour set.</summary>
        const string PackMaterialFolder =
            "Assets/UltimateSpaceshipsCreator/Materials/GenericSpaceships/GenericSpaceship1-8/";

        /// <summary>Cached default prefab from Resources (or Editor AssetDatabase).</summary>
        static GameObject s_DefaultPrefab;

        /// <summary>Inactive stripped runtime template cloned per spawn.</summary>
        static GameObject s_RuntimeTemplate;

        /// <summary>Cached Resources catalog of GenericSpaceships1-8 team materials.</summary>
        static PeopleTransportTeamMaterials s_TeamMaterials;

        /// <summary>Direct material cache indexed by <see cref="TeamId"/> byte.</summary>
        static readonly Material[] s_DirectTeamMaterials = new Material[6];

        /// <summary>Shared unlit yellow for the outer sphere (never team-tinted).</summary>
        static Material s_YellowSphereMaterial;

        /// <summary>
        /// Loads the designer prefab from Resources (player + Editor). Editor can also resolve
        /// via AssetDatabase if Resources is empty during iteration.
        /// </summary>
        public static GameObject LoadDefaultPrefab()
        {
            if (s_DefaultPrefab != null)
                return s_DefaultPrefab;

            // [UNITY] Resources.Load — included in player builds when under Assets/Resources/.
            s_DefaultPrefab = Resources.Load<GameObject>(ResourcesPrefabPath);

#if UNITY_EDITOR
            if (s_DefaultPrefab == null)
            {
                s_DefaultPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/Prefabs/PeopleTransport.prefab");
            }
#endif
            return s_DefaultPrefab;
        }

        /// <summary>
        /// Instantiates a flight proxy from the PeopleTransport prefab (or lightweight fallback).
        /// </summary>
        public static GameObject CreateVisual(GameObject prefab, float peopleAmount, TeamId team)
        {
            EnsureRuntimeTemplate(prefab);

            GameObject instance;
            Vector3 baseVisualScale;

            if (s_RuntimeTemplate != null)
            {
                instance = Object.Instantiate(s_RuntimeTemplate);
                baseVisualScale = s_RuntimeTemplate.transform.localScale;
                if (baseVisualScale.sqrMagnitude < 0.0001f)
                    baseVisualScale = Vector3.one * DefaultPrefabBaseUniform;
            }
            else
            {
                instance = BuildLightweightRoot();
                baseVisualScale = Vector3.one * DefaultPrefabBaseUniform;
            }

            instance.name = "PeopleTransportProxy";
            instance.SetActive(true);

            float multiplier = PeopleTransportMath.GetVisualScaleMultiplier(math.max(0.001f, peopleAmount));
            instance.transform.localScale = baseVisualScale * multiplier;

            ApplyTeamMaterialToShipChild(instance, team);
            return instance;
        }

        /// <summary>Uniform world scale estimate for visualizer helpers.</summary>
        public static float ComputeWorldScale(float peopleAmount)
        {
            return DefaultPrefabBaseUniform *
                   PeopleTransportMath.GetVisualScaleMultiplier(math.max(0.001f, peopleAmount));
        }

        /// <summary>
        /// Builds the inactive template once from Resources/PeopleTransport (strip physics).
        /// Falls back to primitives only if the prefab is missing from the build.
        /// </summary>
        static void EnsureRuntimeTemplate(GameObject prefab)
        {
            if (s_RuntimeTemplate != null)
                return;

            if (prefab == null)
                prefab = LoadDefaultPrefab();

            if (prefab != null)
            {
                s_RuntimeTemplate = Object.Instantiate(prefab);
                s_RuntimeTemplate.name = "PeopleTransportProxyTemplate";
                s_RuntimeTemplate.SetActive(false);
                StripPhysicsForProxyImmediate(s_RuntimeTemplate);
                Object.DontDestroyOnLoad(s_RuntimeTemplate);
                return;
            }

            // --- Fallback only — prefab missing from Resources ---
            s_RuntimeTemplate = BuildLightweightRoot();
            s_RuntimeTemplate.name = "PeopleTransportProxyTemplate_Lightweight";
            s_RuntimeTemplate.SetActive(false);
            Object.DontDestroyOnLoad(s_RuntimeTemplate);
        }

        /// <summary>
        /// Yellow outer sphere + smaller child sphere (stands in for GenericSpaceship6).
        /// Used only when Resources/PeopleTransport is missing.
        /// </summary>
        static GameObject BuildLightweightRoot()
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            root.name = "PeopleTransportLightweight";
            Object.DestroyImmediate(root.GetComponent<Collider>());

            var rootRenderer = root.GetComponent<MeshRenderer>();
            if (rootRenderer != null)
            {
                rootRenderer.sharedMaterial = GetYellowSphereMaterial();
                rootRenderer.shadowCastingMode = ShadowCastingMode.Off;
                rootRenderer.receiveShadows = false;
            }

            root.transform.localScale = Vector3.one * DefaultPrefabBaseUniform;

            var ship = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ship.name = "PeopleTransportShip";
            Object.DestroyImmediate(ship.GetComponent<Collider>());
            ship.transform.SetParent(root.transform, false);
            ship.transform.localPosition = Vector3.zero;
            ship.transform.localScale = Vector3.one * 0.55f;

            var shipRenderer = ship.GetComponent<MeshRenderer>();
            if (shipRenderer != null)
            {
                shipRenderer.shadowCastingMode = ShadowCastingMode.Off;
                shipRenderer.receiveShadows = false;
            }

            return root;
        }

        /// <summary>Immediate strip — deferred Destroy left Rigidbodies on clones for a frame.</summary>
        static void StripPhysicsForProxyImmediate(GameObject root)
        {
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(col);
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
                Object.DestroyImmediate(rb);

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                string typeName = component.GetType().Name;
                if (typeName.Contains("Network") || typeName.Contains("Netcode") ||
                    typeName.Contains("PeopleTransportProjectile"))
                    Object.DestroyImmediate(component);
            }
        }

        /// <summary>Team material on child renderers only; root yellow sphere stays yellow.</summary>
        static void ApplyTeamMaterialToShipChild(GameObject root, TeamId team)
        {
            Material material = GetTeamShipMaterial(team);
            if (material == null)
                return;

            var rootRenderer = root.GetComponent<Renderer>();
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer == rootRenderer)
                    continue;
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                    continue;

                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        static Material GetYellowSphereMaterial()
        {
            if (s_YellowSphereMaterial != null)
                return s_YellowSphereMaterial;

            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Standard");
            s_YellowSphereMaterial = new Material(shader);
            var yellow = new Color(1f, 0.85f, 0.15f, 1f);
            if (s_YellowSphereMaterial.HasProperty("_BaseColor"))
                s_YellowSphereMaterial.SetColor("_BaseColor", yellow);
            if (s_YellowSphereMaterial.HasProperty("_Color"))
                s_YellowSphereMaterial.SetColor("_Color", yellow);
            s_YellowSphereMaterial.color = yellow;
            return s_YellowSphereMaterial;
        }

        static Material GetTeamShipMaterial(TeamId team)
        {
            EnsureTeamMaterialsCatalog();

            Material fromCatalog = s_TeamMaterials != null
                ? s_TeamMaterials.GetMaterialForTeam(team)
                : null;
            if (fromCatalog != null)
                return fromCatalog;

            return LoadPackMaterialDirect(team) ?? GetFallbackTeamUnlit(team);
        }

        static void EnsureTeamMaterialsCatalog()
        {
            if (s_TeamMaterials != null)
                return;

            s_TeamMaterials = Resources.Load<PeopleTransportTeamMaterials>(
                PeopleTransportTeamMaterials.ResourcesPath);

#if UNITY_EDITOR
            if (s_TeamMaterials == null)
            {
                s_TeamMaterials = UnityEditor.AssetDatabase.LoadAssetAtPath<PeopleTransportTeamMaterials>(
                    "Assets/Resources/PeopleTransportTeamMaterials.asset");
            }
#endif
        }

        static Material LoadPackMaterialDirect(TeamId team)
        {
            int index = (int)team;
            if (index < 0 || index >= s_DirectTeamMaterials.Length)
                index = 0;

            if (s_DirectTeamMaterials[index] != null)
                return s_DirectTeamMaterials[index];

            string fileName = ResolvePackMaterialFileName(team);
            Material mat = null;

#if UNITY_EDITOR
            mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(PackMaterialFolder + fileName);
#endif
            if (mat == null)
            {
                string resourceName = "PeopleTransportShip/" + fileName.Replace(".mat", string.Empty);
                mat = Resources.Load<Material>(resourceName);
            }

            s_DirectTeamMaterials[index] = mat;
            return mat;
        }

        /// <summary>Unlit team colour when pack mats are missing from the player build.</summary>
        static Material GetFallbackTeamUnlit(TeamId team)
        {
            int index = (int)team;
            if (index < 0 || index >= s_DirectTeamMaterials.Length)
                index = 0;
            if (s_DirectTeamMaterials[index] != null)
                return s_DirectTeamMaterials[index];

            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Standard");
            var mat = new Material(shader);
            Color color = team.ToColor();
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            mat.color = color;
            s_DirectTeamMaterials[index] = mat;
            return mat;
        }

        static string ResolvePackMaterialFileName(TeamId team)
        {
            switch (team)
            {
                case TeamId.TeamA: return "GenericSpaceships1-8_Red.mat";
                case TeamId.TeamB: return "GenericSpaceships1-8_Blue.mat";
                case TeamId.TeamC: return "GenericSpaceships1-8_Green.mat";
                case TeamId.TeamD: return "GenericSpaceships1-8_GreenYellow.mat";
                case TeamId.TeamE: return "GenericSpaceships1-8_Violet.mat";
                default: return "GenericSpaceships1-8_Grey.mat";
            }
        }
    }
}
