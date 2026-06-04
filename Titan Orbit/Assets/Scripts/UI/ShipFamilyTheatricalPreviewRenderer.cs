using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Systems;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Runtime theatrical (3/4 hero) ship thumbnail renderer for UI. Mirrors editor
    /// <see cref="Editor.ShipFamilyMenuPreviewGenerator"/> framing from <see cref="ShipFamilyDefinition"/>.
    /// Always uses a fixed canonical pose so repeated captures do not drift with live ship rotation.
    /// </summary>
    public static class ShipFamilyTheatricalPreviewRenderer
    {
        private const int PreviewLayer = 31;
        private const int RenderSize = 512;
        /// <summary>Same hero yaw as editor menu previews — applied once per capture, never accumulated.</summary>
        private const float TheatricalSubjectYawDeg = -28f;
        private static readonly Vector3 OffscreenRoot = new Vector3(8000f, 8000f, 8000f);

        private struct TheatricalPreviewFraming
        {
            public Vector3 lookTarget;
            public Vector3 cameraPosition;
            public Quaternion keyLightRotation;
        }

        public static bool TryRenderCurrentShipPreview(Starship ship, ShipFamilyDefinition family, out Sprite sprite)
        {
            sprite = null;
            if (ship == null || family == null)
                return false;

            var root = new GameObject("RuntimeShipMenuPreviewRoot");
            root.transform.position = OffscreenRoot;
            root.hideFlags = HideFlags.HideAndDontSave;

            var subject = new GameObject("PreviewSubject");
            subject.transform.SetParent(root.transform, false);
            subject.transform.localPosition = Vector3.zero;
            subject.transform.localRotation = Quaternion.Euler(0f, TheatricalSubjectYawDeg, 0f);
            subject.transform.localScale = Vector3.one;

            GameObject hullSubject = CloneLiveShipVisuals(ship, subject.transform);
            if (hullSubject == null)
            {
                Object.Destroy(root);
                return false;
            }

            PlaceEquippedDroneVisuals(ship, subject.transform);

            ApplyLayerRecursive(root, PreviewLayer);

            Bounds shipBounds = CalculateEnabledRendererBounds(hullSubject);
            Bounds fullBounds = CalculateEnabledRendererBounds(subject);
            if (shipBounds.size.sqrMagnitude < 1e-6f && fullBounds.size.sqrMagnitude < 1e-6f)
            {
                Object.Destroy(root);
                return false;
            }

            if (shipBounds.size.sqrMagnitude < 1e-6f)
                shipBounds = fullBounds;

            var camGo = new GameObject("RuntimeShipMenuPreviewCam");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<UnityEngine.Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = family.menuPreviewBackgroundColor;
            cam.cullingMask = 1 << PreviewLayer;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;
            cam.orthographic = false;
            cam.fieldOfView = Mathf.Clamp(family.menuPreviewTheatricalFieldOfView, 20f, 55f);

            TheatricalPreviewFraming framing = BuildTheatricalFraming(shipBounds, fullBounds, family);
            cam.transform.position = framing.cameraPosition;
            cam.transform.LookAt(framing.lookTarget, Vector3.up);

            var lightGo = new GameObject("RuntimeShipMenuPreviewLight");
            lightGo.hideFlags = HideFlags.HideAndDontSave;
            lightGo.transform.SetParent(root.transform, false);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.cullingMask = ~0;
            light.transform.rotation = framing.keyLightRotation;

            Color oldAmb = RenderSettings.ambientLight;
            RenderSettings.ambientLight = new Color(0.32f, 0.35f, 0.42f);

            var rt = RenderTexture.GetTemporary(RenderSize, RenderSize, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture prevTarget = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prevTarget;

            RenderTexture.active = rt;
            var tex = new Texture2D(RenderSize, RenderSize, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, RenderSize, RenderSize), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);

#if UNITY_EDITOR
            SaveEditorDebugPreviewPng(tex);
#endif

            RenderSettings.ambientLight = oldAmb;
            Object.Destroy(root);

            sprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, RenderSize, RenderSize),
                new Vector2(0.5f, 0.5f),
                100f);
            return sprite != null;
        }

        /// <summary>
        /// Copies the live hull/equipped meshes but resets root pose so banking/dock spin is not baked in.
        /// </summary>
        private static GameObject CloneLiveShipVisuals(Starship ship, Transform subject)
        {
            Transform liveRoot = ship.GetCardVisualRoot();
            if (liveRoot == null)
                return null;

            ship.EnsureMenuPreviewVisualSourceUpToDate();

            GameObject copy = Object.Instantiate(liveRoot.gameObject, subject, false);
            copy.name = "PreviewHull";
            copy.transform.localRotation = Quaternion.identity;
            ship.SyncMenuPreviewComponentScales(copy.transform);
            copy.transform.localScale = ship.GetMenuPreviewHullLocalScale();
            ApplyShipLocalPosition(ship, copy.transform, liveRoot.position);

            StripToRenderersOnly(copy.transform);
            DisableParticleRenderers(copy.transform);
            return copy;
        }

        /// <summary>
        /// Places drones using live hub offsets in ship-local meters, or exact runtime formation math.
        /// </summary>
        private static void PlaceEquippedDroneVisuals(Starship ship, Transform subject)
        {
            IReadOnlyList<EquippedEquipmentEntry> equipment = ship.EquippedEquipment;
            if (equipment == null || equipment.Count == 0)
                return;

            var store = HomePlanetStoreSystem.Instance;
            if (store == null)
                return;

            float orbitHullRadius = ship.GetShipMoonDockRadiusXZ();
            PlaceComputedDroneVisuals(ship, subject, store, equipment, orbitHullRadius);
        }

        private static void PlaceComputedDroneVisuals(
            Starship ship,
            Transform subject,
            HomePlanetStoreSystem store,
            IReadOnlyList<EquippedEquipmentEntry> equipment,
            float orbitHullRadius)
        {
            DroneSwarmController swarm = ship.DroneSwarm;
            if (swarm == null)
                return;

            for (int slot = 0; slot < equipment.Count; slot++)
            {
                EquippedEquipmentEntry entry = equipment[slot];
                if (!StoreItemData.IsDrone(entry.ItemType) || entry.remainingCharges <= 0)
                    continue;

                GameObject prefab = GetDronePrefab(store, entry.ItemType);
                if (prefab == null)
                    continue;

                DroneSwarmPositioning.OrbitSlotTarget target =
                    swarm.GetMenuPreviewOrbitSlot(slot, entry.ItemType, orbitHullRadius);
                Vector3 localPos = DroneSwarmController.OrbitSlotToCanonicalLocalOffset(target);
                SpawnPreviewDrone(
                    prefab,
                    subject,
                    localPos,
                    Quaternion.LookRotation(Vector3.forward, Vector3.up),
                    entry.ItemType + "_Slot" + slot);
            }
        }

        private static void SpawnPreviewDrone(
            GameObject prefab,
            Transform subject,
            Vector3 localPosition,
            Quaternion localRotation,
            string nameHint)
        {
            GameObject drone = Object.Instantiate(prefab, subject, false);
            drone.name = "PreviewDrone_" + nameHint;
            drone.transform.localPosition = localPosition;
            drone.transform.localRotation = localRotation;
            StripToRenderersOnly(drone.transform);
            DisableParticleRenderers(drone.transform);
        }

        private static void ApplyShipLocalPosition(Starship ship, Transform target, Vector3 worldPosition)
        {
            DroneSwarmPositioning.GetShipBasis(ship, out _, out Vector3 forward, out Vector3 right);
            Vector3 offset = worldPosition - ship.transform.position;
            target.localPosition = WorldOffsetToShipLocal(offset, forward, right);
        }

        private static Vector3 WorldOffsetToShipLocal(Vector3 worldOffset, Vector3 forward, Vector3 right)
        {
            return new Vector3(
                Vector3.Dot(worldOffset, right),
                worldOffset.y,
                Vector3.Dot(worldOffset, forward));
        }

#if UNITY_EDITOR
        private static void SaveEditorDebugPreviewPng(Texture2D tex)
        {
            if (tex == null)
                return;

            const string relativePath = "Assets/_Diagnostics/menu-ship-preview.png";
            string fullPath = System.IO.Path.Combine(Application.dataPath, "_Diagnostics/menu-ship-preview.png");
            string directory = System.IO.Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                System.IO.Directory.CreateDirectory(directory);

            System.IO.File.WriteAllBytes(fullPath, tex.EncodeToPNG());
            UnityEditor.AssetDatabase.ImportAsset(relativePath, UnityEditor.ImportAssetOptions.ForceUpdate);
        }
#endif

        private static GameObject GetDronePrefab(HomePlanetStoreSystem store, StoreItemType itemType)
        {
            switch (itemType)
            {
                case StoreItemType.FighterDrone: return store.FighterDronePrefab;
                case StoreItemType.ShieldDrone: return store.ShieldDronePrefab;
                case StoreItemType.MiningDrone: return store.MiningDronePrefab;
                default: return null;
            }
        }

        private static void StripToRenderersOnly(Transform root)
        {
            Component[] components = root.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                if (c == null)
                    continue;
                if (c is Transform || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer)
                    continue;
                Object.DestroyImmediate(c);
            }

            for (int i = 0; i < root.childCount; i++)
                StripToRenderersOnly(root.GetChild(i));
        }

        private static void DisableParticleRenderers(Transform root)
        {
            ParticleSystemRenderer[] particleRenderers = root.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < particleRenderers.Length; i++)
            {
                if (particleRenderers[i] != null)
                    particleRenderers[i].enabled = false;
            }
        }

        private static TheatricalPreviewFraming BuildTheatricalFraming(
            Bounds shipBounds,
            Bounds fullBounds,
            ShipFamilyDefinition def)
        {
            float padding = Mathf.Max(1f, def.menuPreviewBoundsPadding);
            float shipExt = MaxBoundsExtent(shipBounds);
            float fullExt = MaxBoundsExtent(fullBounds);
            bool includesDrones = fullExt > shipExt * 1.06f;

            float maxExt = includesDrones ? fullExt : shipExt;
            float standoffMultiplier = includesDrones ? 4.45f : 3.6f;
            float dronePaddingBoost = includesDrones ? 1.15f : 1f;
            float standoff = maxExt * padding * standoffMultiplier * dronePaddingBoost;

            const float elevationDeg = 26f;
            const float azimuthDeg = 34f;
            float elevRad = elevationDeg * Mathf.Deg2Rad;
            float azRad = azimuthDeg * Mathf.Deg2Rad;
            float horiz = standoff * Mathf.Cos(elevRad);
            float height = standoff * Mathf.Sin(elevRad);
            Vector3 cameraOffset = new Vector3(
                horiz * Mathf.Sin(azRad),
                height,
                horiz * Mathf.Cos(azRad));

            Vector3 lookTarget = includesDrones
                ? fullBounds.center + Vector3.up * (fullBounds.extents.y * 0.1f)
                : shipBounds.center + Vector3.up * (shipBounds.extents.y * 0.12f);
            Vector3 cameraPosition = lookTarget + cameraOffset;

            Vector3 lightOffset = new Vector3(
                horiz * 0.62f,
                height * 1.75f,
                horiz * 0.82f);
            Vector3 lightPosition = lookTarget + lightOffset;
            Quaternion keyLightRotation = Quaternion.LookRotation((lookTarget - lightPosition).normalized, Vector3.up);

            return new TheatricalPreviewFraming
            {
                lookTarget = lookTarget,
                cameraPosition = cameraPosition,
                keyLightRotation = keyLightRotation
            };
        }

        private static float MaxBoundsExtent(Bounds bounds) =>
            Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);

        private static Bounds CalculateEnabledRendererBounds(GameObject root)
        {
            var rends = root.GetComponentsInChildren<Renderer>();
            Bounds? bounds = null;
            for (int i = 0; i < rends.Length; i++)
            {
                Renderer r = rends[i];
                if (r == null || !r.enabled || r is ParticleSystemRenderer)
                    continue;
                if (!bounds.HasValue)
                    bounds = r.bounds;
                else
                {
                    Bounds b = bounds.Value;
                    b.Encapsulate(r.bounds);
                    bounds = b;
                }
            }

            return bounds ?? new Bounds(root.transform.position, Vector3.zero);
        }

        private static void ApplyLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                ApplyLayerRecursive(t.GetChild(i).gameObject, layer);
        }
    }
}
