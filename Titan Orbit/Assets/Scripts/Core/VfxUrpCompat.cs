using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Sci-Fi Arsenal / AllIn1 VFX often ship with GrabPass materials that do not render correctly on URP,
    /// especially on mobile (GLES/Vulkan). Do not add AllIn1 shaders to Project Settings → Graphics →
    /// Always Included Shaders — that can crash or destabilize Android/iOS builds; rely on fallbacks here instead.
    /// </summary>
    public static class VfxUrpCompat
    {
        private static Shader s_allIn1SrpBatch;
        private static Shader s_urpParticlesUnlit;
        private static Shader s_legacyParticlesUnlit;

        /// <summary>Swap GrabPass AllIn1 materials for SRP batch or particle unlit fallbacks; disable screen distortion.</summary>
        public static void FixAllIn1MaterialsForUrp(GameObject root)
        {
            if (root == null) return;

            if (s_urpParticlesUnlit == null) s_urpParticlesUnlit = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (s_legacyParticlesUnlit == null) s_legacyParticlesUnlit = Shader.Find("Particles/Standard Unlit");
            // Desktop only: AllIn1 SRP batch can upset mobile builds if referenced; mobile uses particle unlit only.
            if (!Application.isMobilePlatform && s_allIn1SrpBatch == null)
                s_allIn1SrpBatch = Shader.Find("AllIn1Vfx/AllIn1VfxSRPBatch");

            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterials == null) continue;
                foreach (Material mat in r.materials)
                {
                    if (mat == null || mat.shader == null) continue;
                    string sn = mat.shader.name;
                    if (sn == "AllIn1Vfx/AllIn1VfxGrabPass" || (sn.IndexOf("AllIn1", System.StringComparison.Ordinal) >= 0 && sn.IndexOf("GrabPass", System.StringComparison.Ordinal) >= 0))
                    {
                        Shader replacement;
                        if (Application.isMobilePlatform)
                            replacement = s_urpParticlesUnlit != null ? s_urpParticlesUnlit : s_legacyParticlesUnlit;
                        else
                            replacement = s_allIn1SrpBatch != null ? s_allIn1SrpBatch : s_urpParticlesUnlit;
                        if (replacement == null) replacement = s_legacyParticlesUnlit;
                        if (replacement != null)
                            mat.shader = replacement;
                    }
                    if (mat.IsKeywordEnabled("SCREENDISTORTION_ON"))
                        mat.DisableKeyword("SCREENDISTORTION_ON");
                }
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
            PlayParticleSystemsInHierarchy(root);
        }

        /// <summary>
        /// Uniformly scales impact VFX. Sci-Fi Arsenal / AllIn1 prefabs often use world-space particles
        /// and ignore <see cref="Transform.localScale"/> alone — adjust particle modules directly.
        /// </summary>
        public static void ApplyImpactVisualScale(GameObject root, float scale)
        {
            if (root == null) return;
            float s = Mathf.Max(0.05f, scale);
            root.transform.localScale = Vector3.one * s;

            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null) continue;

                var main = ps.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
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

            Light[] lights = root.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] == null) continue;
                lights[i].range *= s;
                lights[i].intensity *= Mathf.Lerp(0.7f, 1.35f, Mathf.InverseLerp(0.2f, 2.2f, s));
            }
        }

        /// <summary>Weapon fire: compact cone burst using URP Particles Unlit only (mobile; no AllIn1 prefabs).</summary>
        public static void SpawnMobileMuzzleFlash(Vector3 position, Vector3 forwardHorizontal, Color color)
        {
            forwardHorizontal.y = 0f;
            if (forwardHorizontal.sqrMagnitude < 0.0001f)
                forwardHorizontal = Vector3.forward;
            forwardHorizontal.Normalize();
            Quaternion rot = Quaternion.LookRotation(forwardHorizontal);

            GameObject go = new GameObject("MobileMuzzleFlash");
            go.transform.SetPositionAndRotation(position, rot);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 0.14f;
            main.startLifetime = 0.07f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.11f);
            main.startColor = new ParticleSystem.MinMaxGradient(color, color * 0.65f);
            main.maxParticles = 40;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 20) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 26f;
            shape.radius = 0.02f;

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
            Object.Destroy(go, 0.5f);
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
            Object.Destroy(go, 0.65f);
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
