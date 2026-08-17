using TitanOrbit;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>Procedural bullet mesh shape when bank prefab is unavailable.</summary>
    public enum BulletShape
    {
        Sphere,
        Square,
    }

    /// <summary>
    /// Builds bullet visuals (bank prefab particle, optional procedural core + trail) and spawns
    /// muzzle/impact VFX for ECS client tracers. Consumes <see cref="BulletVfxBank"/> team-colored
    /// prefabs and <see cref="Simulation.BulletVisualScale"/> for per-shot sizing.
    /// Muzzle/impact Instantiates go through <see cref="BulletOneShotVfxPool"/> so kill/fire
    /// frames reuse shells. Presentation only — hit detection stays on server
    /// <see cref="ECS.Systems.BulletSimulationSystem"/>.
    /// </summary>
    public static class BulletVisualFactory
    {
        public const float DefaultBulletVisualScale = 1.2f;
        /// <summary>Legacy CombatSystem global bullet VFX scale when no bank is assigned.</summary>
        public const float LegacyGlobalVisualScale = 0.5f;
        public const float DefaultCoreSize = 0.5f;
        public const float DefaultTailLength = 0.8f;
        public const float DefaultTailWidth = 0.12f;
        public const float DefaultTailFade = 0.7f;
        public const float DefaultImpactDuration = 3f;

        static Material trailMat;
        static Material defaultBulletMat;

        /// <summary>
        /// Final VFX size = factory baseline × per-shot fire-power scale ×
        /// (bank Global × category Global). Category defaults to 1 (100%).
        /// </summary>
        /// <param name="bankIndex">
        /// Category index for the per-family global knob; negative skips category (bank only).
        /// </param>
        public static float GetBulletVisualScale(BulletVfxBank bank, float scaleMultiplier, int bankIndex = -1)
        {
            // --- Bank-wide + optional per-category global ---
            float globalScale;
            if (bank == null)
                globalScale = LegacyGlobalVisualScale;
            else if (bankIndex >= 0)
                globalScale = bank.GetCombinedGlobalVisualScaleMultiplier(bankIndex);
            else
                globalScale = bank.GlobalVisualScaleMultiplier;

            return DefaultBulletVisualScale * Mathf.Max(0.1f, scaleMultiplier) * globalScale;
        }

        /// <summary>Impact burst uses the same size stack as in-flight tracers.</summary>
        public static float GetImpactScale(BulletVfxBank bank, float bulletScaleMultiplier, int bankIndex = -1) =>
            GetBulletVisualScale(bank, bulletScaleMultiplier, bankIndex);

        public static Color GetTeamBulletColor(TeamId team) => team.ToColor();

        public static GameObject BuildVisual(
            Transform parent,
            BulletVfxBank bank,
            int bankIndex,
            TeamId team,
            BulletShape shape,
            float scaleMultiplier,
            float bulletSpeed,
            bool noTrail)
        {
            // [TITAN-ORBIT] bankIndex also selects per-category Global Visual Scale (default 1).
            float scale = GetBulletVisualScale(bank, scaleMultiplier, bankIndex);
            Color color = GetTeamBulletColor(team);

            GameObject visualPrefab = null;
            if (!Application.isMobilePlatform && bank != null)
                visualPrefab = bank.GetProjectileVisualPrefab(bankIndex, team);

            GameObject visual;
            if (visualPrefab != null)
            {
                visual = Object.Instantiate(visualPrefab, parent);
                VfxUrpCompat.FixAllIn1MaterialsForUrp(visual);
                ApplyColorToVisual(visual, color);
                VfxUrpCompat.ApplyImpactVisualScale(visual, scale);
                VfxUrpCompat.PlayParticleSystemsInHierarchy(visual);
                if (noTrail)
                {
                    var trails = visual.GetComponentsInChildren<TrailRenderer>(true);
                    for (int i = 0; i < trails.Length; i++)
                    {
                        if (trails[i] != null)
                            trails[i].enabled = false;
                    }
                }
            }
            else
            {
                visual = CreateCustomizableVfxStyle(shape, scale, bulletSpeed, noTrail, color);
                visual.transform.SetParent(parent, false);
            }

            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            if (visualPrefab == null)
                visual.transform.localScale = Vector3.one * scale;
            SetAudioPitchInHierarchy(visual, GetProjectileSoundPitchBySpeed(bulletSpeed));
            return visual;
        }

        public static void PlayMuzzleVfx(
            Vector3 position,
            Vector3 direction,
            BulletVfxBank bank,
            int bankIndex,
            TeamId team,
            float scaleMultiplier,
            float bulletSpeed)
        {
            // Keep authored / mount world Y so the flash sits on the weapon, not the ground plane.
            Vector3 dir = direction;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f)
                dir = Vector3.forward;
            dir.Normalize();

            float visualScale = GetBulletVisualScale(bank, scaleMultiplier, bankIndex);
            float pitch = GetProjectileSoundPitchBySpeed(bulletSpeed);
            Color flashColor = GetTeamBulletColor(team);

            if (!Application.isMobilePlatform && bank != null)
            {
                GameObject muzzlePrefab = bank.GetMuzzlePrefab(bankIndex, team);
                if (muzzlePrefab != null)
                {
                    // [TITAN-ORBIT] Pool muzzle flashes — Instantiates-per-shot was ~18–20ms with impacts.
                    if (!BulletOneShotVfxPool.TryRent(muzzlePrefab, out GameObject muzzle) || muzzle == null)
                        return;

                    muzzle.transform.SetPositionAndRotation(position, Quaternion.LookRotation(-dir));
                    VfxUrpCompat.ApplyImpactVisualScale(muzzle, visualScale);
                    VfxUrpCompat.PrepareVfxInstance(muzzle);
                    SetAudioPitchInHierarchy(muzzle, pitch);
                    BulletOneShotVfxPool.ScheduleReturn(muzzle, 1.5f);
                    return;
                }
            }

            VfxUrpCompat.SpawnMobileMuzzleFlash(position, dir, flashColor, visualScale);
        }

        public static void SpawnBulletImpactVfx(
            Vector3 position,
            BulletVfxBank bank,
            int bankIndex,
            TeamId team,
            float damage,
            float scaleMultiplier,
            Transform attachParent = null)
        {
            // [TITAN-ORBIT] Isolation F1 — skip impact Instantiates/Rent to bisect destroy stutter.
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
                return;

            // Keep the caller Y (drawn surface). Flattening to 0 put flashes under large rocks / moons.
            float impactScale = GetImpactScale(bank, scaleMultiplier, bankIndex);
            float pitch = GetImpactSoundPitch(damage);

            if (Application.isMobilePlatform)
            {
                VfxUrpCompat.SpawnMobileImpactBurst(position, GetTeamBulletColor(team), impactScale);
                AudioManager.Instance?.PlayImpactSound(pitch);
                return;
            }

            GameObject prefab = bank != null ? bank.GetImpactPrefab(bankIndex, team) : null;
            if (prefab == null)
            {
                VfxUrpCompat.SpawnMobileImpactBurst(position, GetTeamBulletColor(team), impactScale);
                AudioManager.Instance?.PlayImpactSound(pitch);
                return;
            }

            SpawnImpactAt(position, prefab, pitch, impactScale, DefaultImpactDuration, attachParent);
            AudioManager.Instance?.PlayImpactSound(pitch);
        }

        public static void ApplyColorToVisual(GameObject root, Color color)
        {
            // --- Apply changes ---
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
            // --- SetAudioPitchInHierarchy ---
            if (root == null) return;
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null)
                    sources[i].pitch = pitch;
            }
        }

        public static float GetProjectileSoundPitchBySpeed(float projectileSpeed)
        {
            // --- Compute value ---
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

        public static float GetImpactSoundPitch(float damage)
        {
            // --- Compute value ---
            float d = Mathf.Max(0.01f, damage);
            const float minDamage = 1f;
            const float maxDamage = 40f;
            const float lowPitch = 0.45f;
            const float highPitch = 1.8f;
            float t = Mathf.InverseLerp(minDamage, maxDamage, d);
            return Mathf.Lerp(lowPitch, highPitch, t);
        }

        /// <summary>
        /// Places a one-shot impact flash. Rents from <see cref="BulletOneShotVfxPool"/> so
        /// asteroid kills do not Instantiates a fresh prefab every HitRpc.
        /// </summary>
        public static void SpawnImpactAt(
            Vector3 position,
            GameObject prefab,
            float pitch,
            float scale,
            float duration,
            Transform attachParent = null)
        {
            // --- SpawnImpactAt (pooled) ---
            if (prefab == null)
                return;

            if (!BulletOneShotVfxPool.TryRent(prefab, out GameObject go) || go == null)
                return;

            go.transform.SetPositionAndRotation(position, Quaternion.identity);
            VfxUrpCompat.ApplyImpactVisualScale(go, scale);
            SetAudioPitchInHierarchy(go, pitch);
            // [UNITY] PrepareVfxInstance restarts ParticleSystems — required after pool Return cleared them.
            // Cold Instantiates also pays FixAllIn1 / light strip here once (marker after).
            VfxUrpCompat.PrepareVfxInstance(go);
            // worldPositionStays — flash stays on the surface and rides a moving hull / moon / rock.
            if (attachParent != null)
                go.transform.SetParent(attachParent, true);
            BulletOneShotVfxPool.ScheduleReturn(go, duration);
        }

        static GameObject CreateCustomizableVfxStyle(BulletShape shape, float scale, float bulletSpeed, bool noTrailVisual, Color color)
        {
            // --- Create instance ---
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
                        new GradientAlphaKey(0.5f, 1f - Mathf.Clamp01(DefaultTailFade * 0.5f)),
                        new GradientAlphaKey(0f, 1f)
                    });
                trail.colorGradient = grad;
                trail.material = GetTrailMaterial();
                trail.sortingOrder = 0;
            }

            return root;
        }

        static Material GetTrailMaterial()
        {
            // --- Compute value ---
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

        static Material CreateDefaultBulletMaterial()
        {
            // --- Create instance ---
            if (defaultBulletMat != null) return defaultBulletMat;
            defaultBulletMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            defaultBulletMat.color = new Color(0.75f, 0.88f, 1f);
            defaultBulletMat.enableInstancing = true;
            return defaultBulletMat;
        }
    }
}
