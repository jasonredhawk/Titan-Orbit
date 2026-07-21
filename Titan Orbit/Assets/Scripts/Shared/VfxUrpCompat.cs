using System;
using System.IO;
using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Sci-Fi Arsenal / AllIn1 VFX often ship with GrabPass materials that do not render correctly on URP,
    /// especially on mobile (GLES/Vulkan). Do not add AllIn1 shaders to Project Settings → Graphics →
    /// Always Included Shaders — that can crash or destabilize Android/iOS builds; rely on fallbacks here instead.
    /// <para>
    /// [TITAN-ORBIT] debug-604d3d: asteroid destroy still blinked after camera/tile/gem Instantiates were
    /// proven clean. Sci-Fi impact prefabs carry Point Lights — a short intensity spike reads as a
    /// whole-scene eye-blink. Lights are stripped on prepare; particles stay.
    /// </para>
    /// </summary>
    public static class VfxUrpCompat
    {
        private static Shader s_allIn1SrpBatch;
        private static Shader s_urpParticlesUnlit;
        private static Shader s_legacyParticlesUnlit;

        /// <summary>
        /// Swap GrabPass AllIn1 materials for SRP batch or particle unlit fallbacks; disable screen
        /// distortion and soft particles. Uses <c>sharedMaterials</c> only — never
        /// <c>Renderer.materials</c> (that clones every material per impact and GC-hitchs destroy frames;
        /// debug-604d3d showed 40–56ms !!HITCH with camera stable after Sci-Fi Instantiates).
        /// </summary>
        public static void FixAllIn1MaterialsForUrp(GameObject root)
        {
            // --- Guard null root ---
            if (root == null) return;

            // --- Lazy-resolve fallback shaders ---
            if (s_urpParticlesUnlit == null) s_urpParticlesUnlit = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (s_legacyParticlesUnlit == null) s_legacyParticlesUnlit = Shader.Find("Particles/Standard Unlit");
            // Desktop only: AllIn1 SRP batch can upset mobile builds if referenced; mobile uses particle unlit only.
            if (!Application.isMobilePlatform && s_allIn1SrpBatch == null)
                s_allIn1SrpBatch = Shader.Find("AllIn1Vfx/AllIn1VfxSRPBatch");

            // --- Walk renderers and swap GrabPass materials (sharedMaterials — no per-hit clones) ---
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] shared = r.sharedMaterials;
                if (shared == null || shared.Length == 0)
                    continue;

                bool changed = false;
                for (int i = 0; i < shared.Length; i++)
                {
                    Material mat = shared[i];
                    if (mat == null || mat.shader == null)
                        continue;

                    string sn = mat.shader.name;
                    bool isGrab =
                        sn == "AllIn1Vfx/AllIn1VfxGrabPass" ||
                        (sn.IndexOf("AllIn1", StringComparison.Ordinal) >= 0 &&
                         sn.IndexOf("GrabPass", StringComparison.Ordinal) >= 0);

                    if (!isGrab &&
                        !mat.IsKeywordEnabled("SCREENDISTORTION_ON") &&
                        !mat.IsKeywordEnabled("_SOFTPARTICLES_ON"))
                        continue;

                    // Clone once per unique shared material slot we must mutate — not Renderer.materials.
                    Material edit = new Material(mat);
                    if (isGrab)
                    {
                        Shader replacement;
                        if (Application.isMobilePlatform)
                            replacement = s_urpParticlesUnlit != null ? s_urpParticlesUnlit : s_legacyParticlesUnlit;
                        else
                            replacement = s_allIn1SrpBatch != null ? s_allIn1SrpBatch : s_urpParticlesUnlit;
                        if (replacement == null)
                            replacement = s_legacyParticlesUnlit;
                        if (replacement != null)
                            edit.shader = replacement;
                    }

                    if (edit.IsKeywordEnabled("SCREENDISTORTION_ON"))
                        edit.DisableKeyword("SCREENDISTORTION_ON");
                    if (edit.IsKeywordEnabled("_SOFTPARTICLES_ON"))
                        edit.DisableKeyword("_SOFTPARTICLES_ON");
                    if (edit.HasProperty("_SoftParticlesNearFadeDistance"))
                        edit.SetFloat("_SoftParticlesNearFadeDistance", 0f);
                    if (edit.HasProperty("_SoftParticlesFarFadeDistance"))
                        edit.SetFloat("_SoftParticlesFarFadeDistance", 0f);

                    shared[i] = edit;
                    changed = true;
                }

                if (changed)
                    r.sharedMaterials = shared;
            }

            // --- Cap billboard size so one particle cannot cover the whole view ---
            foreach (var psr in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (psr == null)
                    continue;
                psr.maxParticleSize = Mathf.Min(psr.maxParticleSize, 0.25f);
            }
        }

        /// <summary>Clears and plays all particle systems (helps mobile when Play On Awake is unreliable).</summary>
        public static void PlayParticleSystemsInHierarchy(GameObject root)
        {
            if (root == null) return;
            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps == null) continue;
                ps.Clear(true);
                ps.Play(true);
            }
        }

        /// <summary>Fix materials then ensure particles are simulating — use for one-shot muzzle/impact instances.</summary>
        public static void PrepareVfxInstance(GameObject root)
        {
            FixAllIn1MaterialsForUrp(root);
            StripSceneFlashLights(root, "PrepareVfxInstance");
            PlayParticleSystemsInHierarchy(root);
        }

        /// <summary>
        /// Disables Point/Spot lights on Sci-Fi VFX instances. One bright light on asteroid kill
        /// flashes the whole URP scene even when the camera and toroidal tiles are stable.
        /// Particles / meshes stay — only the light components are turned off.
        /// </summary>
        /// <param name="root">Impact or muzzle instance.</param>
        /// <param name="caller">Diagnostic tag for debug-604d3d.log.</param>
        public static void StripSceneFlashLights(GameObject root, string caller)
        {
            if (root == null)
                return;

            Light[] lights = root.GetComponentsInChildren<Light>(true);
            int count = 0;
            float peakIntensity = 0f;
            float peakRange = 0f;
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light == null)
                    continue;
                count++;
                peakIntensity = Mathf.Max(peakIntensity, light.intensity);
                peakRange = Mathf.Max(peakRange, light.range);
                light.enabled = false;
                light.intensity = 0f;
                // Destroy so nothing can re-enable the light next frame.
                UnityEngine.Object.Destroy(light);
            }

            // #region agent log
            // Hypothesis H: impact Point Lights cause whole-scene blink on asteroid kill.
            if (count > 0)
            {
                try
                {
                    string path = Path.GetFullPath(
                        Path.Combine(Application.dataPath, "..", "..", "debug-604d3d.log"));
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string line =
                        "{\"sessionId\":\"604d3d\",\"hypothesisId\":\"H\",\"location\":\"VfxUrpCompat.StripSceneFlashLights\"," +
                        "\"message\":\"IMPACT_LIGHTS_STRIPPED\",\"data\":{" +
                        "\"caller\":\"" + (caller ?? "") + "\",\"count\":" + count +
                        ",\"peakIntensity\":" + peakIntensity.ToString("F2") +
                        ",\"peakRange\":" + peakRange.ToString("F2") +
                        ",\"frame\":" + Time.frameCount +
                        "},\"timestamp\":" + ts + ",\"runId\":\"post-fix\"}\n";
                    File.AppendAllText(path, line);
                }
                catch
                {
                    // Diagnostic only.
                }

                Debug.Log(
                    $"[AsteroidBlink] IMPACT_LIGHTS_STRIPPED caller={caller} count={count} " +
                    $"peakIntensity={peakIntensity:F2} peakRange={peakRange:F2}");
            }
            // #endregion
        }

        /// <summary>
        /// [DIAGNOSTIC] Hypothesis I — which impact path ran (Sci-Fi vs mobile) and Instantiates cost.
        /// </summary>
        public static void LogImpactPath(string pathName, float scale, double ms = 0)
        {
            try
            {
                string path = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", "..", "debug-604d3d.log"));
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line =
                    "{\"sessionId\":\"604d3d\",\"hypothesisId\":\"I\",\"location\":\"BulletVisualFactory.SpawnBulletImpactVfx\"," +
                    "\"message\":\"IMPACT_PATH\",\"data\":{" +
                    "\"path\":\"" + pathName + "\",\"scale\":" + scale.ToString("F2") +
                    ",\"ms\":" + ms.ToString("F2") +
                    ",\"frame\":" + Time.frameCount +
                    ",\"frameDtMs\":" + (Time.deltaTime * 1000f).ToString("F1") +
                    "},\"timestamp\":" + ts + ",\"runId\":\"post-fix\"}\n";
                File.AppendAllText(path, line);
            }
            catch
            {
                // Diagnostic only.
            }
        }

        /// <summary>
        /// Uniformly scales impact VFX. Hierarchy-mode particles follow <see cref="Transform.localScale"/>.
        /// World/local-space Sci-Fi Arsenal / AllIn1 prefabs ignore transform scale — scale their modules instead.
        /// </summary>
        public static void ApplyImpactVisualScale(GameObject root, float scale)
        {
            // --- Root transform scale (hierarchy-mode particles) ---
            if (root == null) return;
            float s = Mathf.Max(0.05f, scale);
            root.transform.localScale = Vector3.one * s;

            // --- Per-system module scaling (world/local space prefabs) ---
            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null) continue;

                var main = ps.main;
                if (main.scalingMode == ParticleSystemScalingMode.Hierarchy)
                    continue;

                // Local / shape / world scaling: prefab size ignores root transform alone.
                main.startSizeMultiplier *= s;
                main.startSpeedMultiplier *= Mathf.Lerp(0.85f, 1.25f, Mathf.InverseLerp(0.2f, 2.2f, s));
                main.startLifetimeMultiplier *= Mathf.Lerp(0.9f, 1.25f, Mathf.InverseLerp(0.2f, 2.2f, s));

                var shape = ps.shape;
                if (shape.enabled)
                {
                    shape.radius *= s;
                    shape.scale *= s;
                }

                var sizeOverLifetime = ps.sizeOverLifetime;
                if (sizeOverLifetime.enabled)
                    sizeOverLifetime.sizeMultiplier *= s;
            }

            // --- Impact Point Lights: stripped in PrepareVfxInstance (not scaled up) ---
        }

        /// <summary>Weapon fire: compact cone burst using URP Particles Unlit only (mobile; no AllIn1 prefabs).</summary>
        public static void SpawnMobileMuzzleFlash(Vector3 position, Vector3 forwardHorizontal, Color color, float scale = 1f)
        {
            forwardHorizontal.y = 0f;
            if (forwardHorizontal.sqrMagnitude < 0.0001f)
                forwardHorizontal = Vector3.forward;
            forwardHorizontal.Normalize();
            Quaternion rot = Quaternion.LookRotation(forwardHorizontal);
            scale = Mathf.Max(0.15f, scale);

            GameObject go = new GameObject("MobileMuzzleFlash");
            go.transform.SetPositionAndRotation(position, rot);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 0.14f;
            main.startLifetime = 0.07f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f * scale, 6f * scale);
            main.startSize = new ParticleSystem.MinMaxCurve(0.035f * scale, 0.11f * scale);
            main.startColor = new ParticleSystem.MinMaxGradient(color, color * 0.65f);
            main.maxParticles = 40;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 20) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 26f;
            shape.radius = 0.02f * scale;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = g;

            var pr = go.GetComponent<ParticleSystemRenderer>();
            Material mat = NewMobileParticleMaterial(color);
            if (mat != null)
                pr.material = mat;
            pr.renderMode = ParticleSystemRenderMode.Billboard;

            ps.Play();
            UnityEngine.Object.Destroy(go, 0.5f);
        }

        /// <summary>Hit feedback: spherical burst (URP Particles Unlit only).</summary>
        public static void SpawnMobileImpactBurst(Vector3 position, Color color, float scale)
        {
            position.y = 0f;
            scale = Mathf.Max(0.15f, scale);

            GameObject go = new GameObject("MobileImpactBurst");
            go.transform.position = position;
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 0.28f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.1f, 0.22f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 4.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.07f * scale, 0.2f * scale);
            main.startColor = new ParticleSystem.MinMaxGradient(color, Color.Lerp(color, Color.white, 0.35f));
            main.maxParticles = 56;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 32) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.06f * scale;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(color, 0f), new GradientColorKey(color * 0.45f, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = g;

            var pr = go.GetComponent<ParticleSystemRenderer>();
            Material mat = NewMobileParticleMaterial(Color.white);
            if (mat != null)
                pr.material = mat;
            pr.renderMode = ParticleSystemRenderMode.Billboard;

            ps.Play();
            UnityEngine.Object.Destroy(go, 0.65f);
        }

        private static Material NewMobileParticleMaterial(Color color)
        {
            if (s_urpParticlesUnlit == null) s_urpParticlesUnlit = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (s_legacyParticlesUnlit == null) s_legacyParticlesUnlit = Shader.Find("Particles/Standard Unlit");
            Shader s = s_urpParticlesUnlit != null ? s_urpParticlesUnlit : s_legacyParticlesUnlit;
            if (s == null) return null;
            var m = new Material(s);
            m.color = color;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            return m;
        }
    }
}
