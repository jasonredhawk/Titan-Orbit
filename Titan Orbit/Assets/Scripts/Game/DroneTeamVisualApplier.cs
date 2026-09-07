using TitanOrbit.Core;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Paints the nested GenericSpaceship inside a drone prefab with that mesh's own
    /// team-color pack. Fighter/mining use GenericSpaceships1-8; shield uses GenericSpaceship11.
    /// Inner core spheres keep their authored material.
    /// </summary>
    public static class DroneTeamVisualApplier
    {
        const string Ship11FolderA =
            "Assets/UltimateSpaceshipsCreator/Materials/GenericSpaceships/GenericSpaceship11/";
        const string Ship11FolderB =
            "Assets/UltimateSpaceshipsCreator/Textures/GenericSpaceships/GenericSpaceship11/Materials/";

        static PeopleTransportTeamMaterials _pack18;

        public static void Apply(GameObject root, TeamId team)
        {
            if (root == null || team == TeamId.None)
                return;

            Material material = ResolveNestedShipTeamMaterial(root, team);
            if (material == null)
                return;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                    continue;
                if (IsInnerCoreRenderer(renderer))
                    continue;

                Material[] current = renderer.sharedMaterials;
                if (current == null || current.Length == 0)
                {
                    renderer.sharedMaterial = material;
                    continue;
                }

                var replaced = new Material[current.Length];
                for (int s = 0; s < current.Length; s++)
                    replaced[s] = material;
                renderer.sharedMaterials = replaced;
            }
        }

        static Material ResolveNestedShipTeamMaterial(GameObject root, TeamId team)
        {
            if (NestedShipLooksLikeShip11(root))
            {
                Material ship11 = LoadShip11Material(team);
                if (ship11 != null)
                    return ship11;
            }

            return LoadPack18Material(team);
        }

        static bool NestedShipLooksLikeShip11(GameObject root)
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (NameContains(transforms[i].name, "GenericSpaceship11"))
                    return true;
            }

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || IsInnerCoreRenderer(renderer))
                    continue;
                Material mat = renderer.sharedMaterial;
                if (mat != null && NameContains(mat.name, "GenericSpaceship11"))
                    return true;
            }

            return false;
        }

        static Material LoadPack18Material(TeamId team)
        {
            if (_pack18 == null)
            {
                _pack18 = Resources.Load<PeopleTransportTeamMaterials>(
                    PeopleTransportTeamMaterials.ResourcesPath);
#if UNITY_EDITOR
                if (_pack18 == null)
                {
                    _pack18 = UnityEditor.AssetDatabase.LoadAssetAtPath<PeopleTransportTeamMaterials>(
                        "Assets/Resources/PeopleTransportTeamMaterials.asset");
                }
#endif
            }

            return _pack18 != null ? _pack18.GetMaterialForTeam(team) : null;
        }

        static Material LoadShip11Material(TeamId team)
        {
            string[] fileNames = Ship11FileNames(team);
            for (int i = 0; i < fileNames.Length; i++)
            {
                Material mat = LoadMaterialFromFolders(fileNames[i], Ship11FolderA, Ship11FolderB);
                if (mat != null)
                    return mat;
            }

            return null;
        }

        static string[] Ship11FileNames(TeamId team)
        {
            switch (team)
            {
                case TeamId.TeamA: return new[] { "GenericSpaceship11_Red.mat" };
                case TeamId.TeamB: return new[] { "GenericSpaceship11_Blue.mat" };
                case TeamId.TeamC: return new[] { "GenericSpaceship11_Green.mat" };
                case TeamId.TeamD:
                    return new[]
                    {
                        "GenericSpaceship11_GreenYellow.mat",
                        "GenericSpaceship11_Yellow.mat",
                        "GenericSpaceship11_Red.mat",
                    };
                case TeamId.TeamE:
                    return new[]
                    {
                        "GenericSpaceship11_Violet.mat",
                        "GenericSpaceship11_Purple.mat",
                        "GenericSpaceship11_Blue.mat",
                    };
                default: return new[] { "GenericSpaceship11_Blue.mat" };
            }
        }

        static Material LoadMaterialFromFolders(string fileName, params string[] folders)
        {
#if UNITY_EDITOR
            for (int i = 0; i < folders.Length; i++)
            {
                Material mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(folders[i] + fileName);
                if (mat != null)
                    return mat;
            }
#endif
            return Resources.Load<Material>(fileName.Replace(".mat", string.Empty));
        }

        static bool IsInnerCoreRenderer(Renderer renderer)
        {
            if (renderer == null || renderer is ParticleSystemRenderer)
                return false;

            string name = renderer.gameObject.name;
            bool droneRoot = NameContains(name, "Drone")
                || NameContains(name, "StoreItemPreviewCapture");
            if (!droneRoot)
                return false;

            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null && filter.sharedMesh.name == "Sphere";
        }

        static bool NameContains(string name, string token)
        {
            return !string.IsNullOrEmpty(name)
                && name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
