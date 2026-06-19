using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Builds bullet visuals (bank prefab particle, optional procedural core + trail) without
    /// requiring a Bullet NetworkBehaviour. Used by <see cref="ClientBulletTracer"/> so the
    /// lightweight server-authoritative bullet path matches the legacy networked Bullet look.
    /// Defaults mirror the values on <c>Assets/Prefabs/Bullet.prefab</c>.
    /// </summary>
    public static class BulletVisualFactory
    {
        public const float DefaultBulletVisualScale = 1.2f;
        public const float DefaultCoreSize = 0.5f;
        public const float DefaultTailLength = 0.8f;
        public const float DefaultTailWidth = 0.12f;
        public const float DefaultTailFade = 0.7f;
        public const float DefaultImpactScale = 0.5f;
        /// <summary>Legacy asteroid-only shrink; impacts now follow <see cref="GetBulletVisualScale"/> like bullets.</summary>
        public const float AsteroidImpactScaleFactor = 0.25f;
        public const float DefaultImpactDuration = 3f;

        /// <summary>Same transform scale applied to client bullet visuals (<see cref="BuildVisual"/>).</summary>
        public static float GetBulletVisualScale(float scaleMultiplier)
        {
            float globalScale = CombatSystem.Instance != null ? CombatSystem.Instance.BulletVisualScaleMultiplier : 1f;
            return DefaultBulletVisualScale * Mathf.Max(0.1f, scaleMultiplier) * globalScale;
        }

        /// <summary>Impact burst scale — matches bullet/muzzle visual size for this shot.</summary>
        public static float GetImpactScale(float bulletScaleMultiplier, bool isAsteroidHit = false) =>
            GetBulletVisualScale(bulletScaleMultiplier);

        /// <summary>
        /// Server / host use logical physics coordinates for VFX. Pure clients remap logical hit
        /// points into the toroidal display tile the local camera sees (matches <see cref="ToroidalRenderer"/>).
        /// </summary>
        public static Vector3 ResolveClientImpactWorldPosition(Vector3 worldOrLogicalPosition)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient || nm.IsServer)
            {
                Vector3 logical = worldOrLogicalPosition;
                logical.y = 0f;
                return logical;
            }

            Vector3 reference = ResolveToroidalVfxReference();
            Vector3 logicalPos = worldOrLogicalPosition;
            logicalPos.y = 0f;
            return ToroidalMap.GetDisplayPosition(logicalPos, reference);
        }

        private static Vector3 ResolveToroidalVfxReference()
        {
            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam == null || !cam.isActiveAndEnabled)
            {
                var cc = Object.FindFirstObjectByType<TitanOrbit.Camera.CameraController>();
                if (cc != null)
                    cam = cc.GetComponent<UnityEngine.Camera>();
            }

            Vector3 reference = cam != null ? cam.transform.position : Vector3.zero;
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsClient && nm.SpawnManager != null)
            {
                NetworkObject localPlayer = nm.SpawnManager.GetLocalPlayerObject();
                if (localPlayer != null)
                {
                    var ship = localPlayer.GetComponent<Starship>();
                    if (ship != null)
                    {
                        Vector3 shipRef = ship.GetCameraFollowWorldPosition();
                        reference.x = shipRef.x;
                        reference.z = shipRef.z;
                    }
                }
            }

            return reference;
        }

        /// <summary>Spawns bank impact VFX (or procedural fallback) at a hit point on this client.</summary>
        public static void SpawnBulletImpactVfx(
            Vector3 worldOrLogicalPosition,
            GameObject prefab,
            TeamManager.Team team,
            float pitch,
            float scale,
            float duration,
            Transform attachParent = null,
            Vector3 localOffset = default,
            float loopingDuration = 0f)
        {
            Vector3 position = ResolveClientImpactWorldPosition(worldOrLogicalPosition);

            if (Application.isMobilePlatform)
            {
                SpawnMobileImpact(position, team, scale);
                return;
            }

            if (prefab == null)
            {
                SpawnMobileImpact(position, team, scale);
                return;
            }

            if (loopingDuration > 0.05f)
            {
                SpawnLoopingImpactAt(
                    position,
                    prefab,
                    pitch,
                    scale,
                    loopingDuration,
                    attachParent,
                    localOffset);
            }
            else
            {
                SpawnImpactAt(position, prefab, pitch, scale, duration);
            }
        }

        private static Material trailMat;
        private static Material defaultBulletMat;

        public static Color GetTeamBulletColor(TeamManager.Team team)
        {
            if (team == TeamManager.Team.None)
                return new Color(0.75f, 0.88f, 1f);
            if (TeamManager.Instance != null)
                return TeamManager.GetTeamColor(team);
            switch (team)
            {
                case TeamManager.Team.TeamA: return new Color(1f, 0.3f, 0.3f);
                case TeamManager.Team.TeamB: return new Color(0.3f, 0.5f, 1f);
                case TeamManager.Team.TeamC: return new Color(0.2f, 0.7f, 0.28f);
                case TeamManager.Team.TeamD: return new Color(0.95f, 0.55f, 0.12f);
                case TeamManager.Team.TeamE: return new Color(0.65f, 0.25f, 0.85f);
                default: return new Color(0.75f, 0.88f, 1f);
            }
        }

        /// <summary>
        /// Creates the bullet visual under <paramref name="parent"/>, using the bank prefab when
        /// available (and we are not on mobile, where Sci-Fi Arsenal materials misbehave) and
        /// falling back to a procedural core + TrailRenderer otherwise.
        /// </summary>
        public static GameObject BuildVisual(
            Transform parent,
            int visualPrefabBankIndex,
            TeamManager.Team team,
            BulletShape shape,
            float scaleMultiplier,
            float bulletSpeed,
            bool noTrail)
        {
            float scale = GetBulletVisualScale(scaleMultiplier);
            Color color = GetTeamBulletColor(team);

            GameObject visualPrefab = null;
            if (!Application.isMobilePlatform && visualPrefabBankIndex >= 0 && CombatSystem.Instance != null)
                visualPrefab = CombatSystem.Instance.GetVisualPrefabFromBank(visualPrefabBankIndex, team);

            GameObject visual;
            if (visualPrefab != null)
            {
                visual = Object.Instantiate(visualPrefab, parent);
                VfxUrpCompat.FixAllIn1MaterialsForUrp(visual);
                ApplyColorToVisual(visual, color);
                VfxUrpCompat.PlayParticleSystemsInHierarchy(visual);
                if (noTrail)
                {
                    var trails = visual.GetComponentsInChildren<TrailRenderer>(true);
                    for (int i = 0; i < trails.Length; i++)
                        if (trails[i] != null) trails[i].enabled = false;
                }
            }
            else
            {
                visual = CreateCustomizableVfxStyle(shape, scale, bulletSpeed, noTrail, color);
                visual.transform.SetParent(parent, false);
            }

            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one * scale;
            SetAudioPitchInHierarchy(visual, GetProjectileSoundPitchBySpeed(bulletSpeed));
            return visual;
        }

        public static void ApplyColorToVisual(GameObject root, Color color)
        {
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterials == null) continue;
                for (int i = 0; i < r.sharedMaterials.Length; i++)
                {
                    Material mat = r.materials[i];
                    if (mat == null) continue;
                    mat.color = color;
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                    if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
                    if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", color);
                    if (mat.HasProperty("_MainColor")) mat.SetColor("_MainColor", color);
                }
            }
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.startColor = color;
            }
        }

        public static void SetAudioPitchInHierarchy(GameObject root, float pitch)
        {
            if (root == null) return;
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource src = sources[i];
                if (src == null) continue;
                src.pitch = pitch;
            }
        }

        public static float GetProjectileSoundPitchBySpeed(float projectileSpeed)
        {
            float s = Mathf.Max(0.01f, projectileSpeed);
            const float minSpeed = 1f;
            const float maxSpeed = 30f;
            const float lowPitch = 0.35f;
            const float highPitch = 2.2f;

            float clamped = Mathf.Clamp(s, minSpeed, maxSpeed);
            float minLog = Mathf.Log10(minSpeed);
            float maxLog = Mathf.Log10(maxSpeed);
            float sLog = Mathf.Log10(clamped);
            float normalized = Mathf.InverseLerp(minLog, maxLog, sLog);
            float emphasized = Mathf.Pow(normalized, 0.85f);
            return Mathf.Lerp(lowPitch, highPitch, emphasized);
        }

        /// <summary>Spawns the desktop/non-mobile impact effect at <paramref name="position"/>.</summary>
        public static void SpawnImpactAt(Vector3 position, GameObject prefab, float pitch, float scale, float duration)
        {
            if (prefab == null) return;
            GameObject go = Object.Instantiate(prefab, position, Quaternion.identity);
            VfxUrpCompat.ApplyImpactVisualScale(go, scale);
            SetAudioPitchInHierarchy(go, pitch);
            VfxUrpCompat.PrepareVfxInstance(go);
            Object.Destroy(go, duration);
        }

        /// <summary>
        /// Spawns the bank impact prefab (e.g. fire) and loops it for <paramref name="duration"/> seconds.
        /// When <paramref name="attachParent"/> is set, uses <see cref="ShipBurnVfxAnchor"/> so fire sticks to a moving hull.
        /// </summary>
        public static GameObject SpawnLoopingImpactAt(
            Vector3 position,
            GameObject prefab,
            float pitch,
            float scale,
            float duration,
            Transform attachParent = null,
            Vector3 localOffset = default)
        {
            if (prefab == null || duration <= 0f) return null;

            if (attachParent != null)
            {
                return ShipBurnVfxAnchor.SpawnAttached(
                    attachParent, localOffset, prefab, pitch, scale, duration);
            }

            GameObject go = Object.Instantiate(prefab, position, Quaternion.identity);
            VfxUrpCompat.ApplyImpactVisualScale(go, scale);
            SetAudioPitchInHierarchy(go, pitch);
            ConfigureLoopingImpactParticles(go, duration, simulateInLocalSpace: false);
            VfxUrpCompat.PrepareVfxInstance(go);
            Object.Destroy(go, duration + 0.25f);
            return go;
        }

        public static void ConfigureLoopingImpactParticles(
            GameObject root,
            float duration,
            bool simulateInLocalSpace = false)
        {
            if (root == null) return;
            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps == null) continue;
                var main = ps.main;
                main.loop = true;
                if (main.duration < duration)
                    main.duration = duration;
                main.stopAction = ParticleSystemStopAction.None;
                if (simulateInLocalSpace)
                    main.simulationSpace = ParticleSystemSimulationSpace.Local;

                var emission = ps.emission;
                if (emission.rateOverTime.constant <= 0.01f && emission.burstCount > 0)
                {
                    emission.rateOverTime = Mathf.Max(4f, emission.burstCount * 2f);
                }
            }
        }

        public static void SpawnMobileImpact(Vector3 position, TeamManager.Team team, float scale)
        {
            VfxUrpCompat.SpawnMobileImpactBurst(position, GetTeamBulletColor(team), scale);
        }

        private static GameObject CreateCustomizableVfxStyle(BulletShape shape, float scale, float bulletSpeed, bool noTrailVisual, Color color)
        {
            Material baseMat = CreateDefaultBulletMaterial();
            Material instanced = new Material(baseMat);
            instanced.color = color;
            if (instanced.HasProperty("_BaseColor"))
                instanced.SetColor("_BaseColor", color);

            GameObject root = new GameObject("BulletVisual");

            GameObject core = shape == BulletShape.Square
                ? GameObject.CreatePrimitive(PrimitiveType.Cube)
                : GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(core.GetComponent<Collider>());
            core.transform.SetParent(root.transform, false);
            core.transform.localPosition = Vector3.zero;
            core.transform.localScale = Vector3.one * DefaultCoreSize;
            var coreMr = core.GetComponent<Renderer>();
            if (coreMr != null) coreMr.sharedMaterial = instanced;

            if (!noTrailVisual && DefaultTailLength > 0.01f)
            {
                TrailRenderer trail = root.AddComponent<TrailRenderer>();
                trail.time = DefaultTailLength / Mathf.Max(5f, bulletSpeed);
                trail.minVertexDistance = 0.03f;
                trail.widthMultiplier = DefaultTailWidth * scale;
                trail.autodestruct = false;
                trail.emitting = true;
                trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                trail.receiveShadows = false;
                trail.numCornerVertices = 8;
                trail.numCapVertices = 4;
                trail.widthCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0.02f);
                Gradient grad = new Gradient();
                grad.SetKeys(
                    new GradientColorKey[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                    new GradientAlphaKey[]
                    {
                        new GradientAlphaKey(0.95f, 0f),
                        new GradientAlphaKey(0.5f, Mathf.Clamp01(1f - DefaultTailFade * 0.5f)),
                        new GradientAlphaKey(0f, 1f)
                    });
                trail.colorGradient = grad;
                trail.material = GetTrailMaterial();
                trail.sortingOrder = 0;
            }

            return root;
        }

        private static Material GetTrailMaterial()
        {
            if (trailMat != null) return trailMat;
            Shader s = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                ?? Shader.Find("Particles/Standard Unlit")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Unlit/Color");
            trailMat = new Material(s);
            trailMat.renderQueue = 3000;
            if (trailMat.HasProperty("_BaseColor")) trailMat.SetColor("_BaseColor", Color.white);
            return trailMat;
        }

        private static Material CreateDefaultBulletMaterial()
        {
            if (defaultBulletMat != null) return defaultBulletMat;
            defaultBulletMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            defaultBulletMat.color = new Color(0.75f, 0.88f, 1f);
            defaultBulletMat.enableInstancing = true;
            return defaultBulletMat;
        }
    }
}
