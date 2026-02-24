using UnityEngine;
using Unity.Netcode;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Manages visual effects like particle systems, explosions, etc.
    /// </summary>
    public class VisualEffectsManager : NetworkBehaviour
    {
        public static VisualEffectsManager Instance { get; private set; }

        [Header("Particle Effects")]
        [SerializeField] private GameObject explosionEffect;
        [SerializeField] private GameObject miningEffect;
        [SerializeField] private GameObject captureEffect;
        [SerializeField] private GameObject bulletTrailEffect;
        [SerializeField] private GameObject levelUpEffect;
        [Tooltip("Optional. Assign an AllIn1 VFX material for fallback burst when no prefab is set. Ignored if Level Up Effect prefab is set.")]
        [SerializeField] private Material levelUpParticleMaterial;
        [Tooltip("Scale applied to level-up VFX prefab when used (e.g. 1.5). Same fix as bullet impact (GrabPass -> SRP) is applied.")]
        [SerializeField] private float levelUpEffectScale = 1.5f;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnExplosionServerRpc(Vector3 position)
        {
            SpawnExplosionClientRpc(position);
        }

        [ClientRpc]
        private void SpawnExplosionClientRpc(Vector3 position)
        {
            if (explosionEffect != null)
            {
                GameObject effect = Instantiate(explosionEffect, position, Quaternion.identity);
                Destroy(effect, 5f);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnMiningEffectServerRpc(Vector3 position)
        {
            SpawnMiningEffectClientRpc(position);
        }

        [ClientRpc]
        private void SpawnMiningEffectClientRpc(Vector3 position)
        {
            if (miningEffect != null)
            {
                GameObject effect = Instantiate(miningEffect, position, Quaternion.identity);
                Destroy(effect, 2f);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnCaptureEffectServerRpc(Vector3 position)
        {
            SpawnCaptureEffectClientRpc(position);
        }

        [ClientRpc]
        private void SpawnCaptureEffectClientRpc(Vector3 position)
        {
            if (captureEffect != null)
            {
                GameObject effect = Instantiate(captureEffect, position, Quaternion.identity);
                Destroy(effect, 3f);
            }
        }

        /// <summary>Play level-up burst. Uses levelUpEffect prefab if set (same URP fix as bullet impact); otherwise procedural fallback.</summary>
        public void PlayLevelUpEffect(Vector3 position)
        {
            if (levelUpEffect != null)
            {
                GameObject go = Instantiate(levelUpEffect, position, Quaternion.identity);
                go.transform.localScale = Vector3.one * levelUpEffectScale;
                FixAllIn1VfxForUrp(go); // Same as bullet DisableGrabPassMaterials – required for URP
                Destroy(go, 4f);
                return;
            }
            CreateFallbackLevelUpBurst(position, null);
        }

        /// <summary>Play level-up effect at position. Call from anywhere (e.g. Planet level-up).</summary>
        public static void PlayLevelUpEffectStatic(Vector3 position)
        {
            VisualEffectsManager vfx = Object.FindFirstObjectByType<VisualEffectsManager>();
            if (vfx != null)
                vfx.PlayLevelUpEffect(position);
            else
                CreateFallbackLevelUpBurst(position, null);
        }

        /// <summary>Swap AllIn1 GrabPass shader to SRP batch so effect works in URP without job-thread error.</summary>
        private static void FixAllIn1VfxForUrp(GameObject root)
        {
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterials == null) continue;
                foreach (Material mat in r.materials)
                {
                    if (mat == null) continue;
                    FixAllIn1MaterialForUrp(mat);
                }
            }
        }

        private static void FixAllIn1MaterialForUrp(Material mat)
        {
            if (mat == null) return;
            if (mat.shader.name == "AllIn1Vfx/AllIn1VfxGrabPass")
            {
                Shader srpShader = Shader.Find("AllIn1Vfx/AllIn1VfxSRPBatch");
                if (srpShader != null) mat.shader = srpShader;
            }
            if (mat.IsKeywordEnabled("SCREENDISTORTION_ON"))
                mat.DisableKeyword("SCREENDISTORTION_ON");
        }

        private static void CreateFallbackLevelUpBurst(Vector3 position, Material optionalAllIn1StyleMaterial)
        {
            Material particleMat = optionalAllIn1StyleMaterial != null
                ? new Material(optionalAllIn1StyleMaterial)
                : GetLevelUpParticleMaterial();
            if (particleMat == null) return;
            if (optionalAllIn1StyleMaterial != null)
                FixAllIn1MaterialForUrp(particleMat);
            float duration = 2.5f;
            float scale = 5f; // Visible from top-down camera; planets are size 4–20

            GameObject root = new GameObject("LevelUpBurst");
            root.transform.position = position;
            root.transform.localScale = Vector3.one * scale;

            // Layer 1: Fast outward spark burst (white → gold)
            GameObject burstGo = new GameObject("Burst");
            burstGo.transform.SetParent(root.transform, false);
            burstGo.transform.localPosition = Vector3.zero;
            var burst = burstGo.AddComponent<ParticleSystem>();
            var burstMain = burst.main;
            burstMain.playOnAwake = false;
            burst.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            burstMain.duration = 0.4f;
            burstMain.loop = false;
            burstMain.startLifetime = 0.6f;
            burstMain.startSpeed = 12f;
            burstMain.startSize = 1.2f;
            burstMain.maxParticles = 60;
            burstMain.simulationSpace = ParticleSystemSimulationSpace.World;
            burstMain.gravityModifier = -0.2f;
            var burstEmission = burst.emission;
            burstEmission.rateOverTime = 0;
            burstEmission.SetBursts(new[] { new ParticleSystem.Burst(0f, 50) });
            var burstShape = burst.shape;
            burstShape.shapeType = ParticleSystemShapeType.Sphere;
            burstShape.radius = 1.5f;
            var burstColor = burst.colorOverLifetime;
            burstColor.enabled = true;
            var burstGrad = new Gradient();
            burstGrad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(1f, 0.85f, 0.4f), 0.5f), new GradientColorKey(new Color(1f, 0.7f, 0.2f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.4f), new GradientAlphaKey(0f, 1f) });
            burstColor.color = burstGrad;
            var burstSize = burst.sizeOverLifetime;
            burstSize.enabled = true;
            burstSize.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(0.3f, 0.8f), new Keyframe(1f, 0f)));
            ApplyParticleMaterial(burstGo, particleMat);

            // Layer 2: Soft expanding glow (core)
            GameObject glowGo = new GameObject("Glow");
            glowGo.transform.SetParent(root.transform, false);
            glowGo.transform.localPosition = Vector3.zero;
            var glow = glowGo.AddComponent<ParticleSystem>();
            var glowMain = glow.main;
            glowMain.playOnAwake = false;
            glow.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            glowMain.duration = 0.3f;
            glowMain.loop = false;
            glowMain.startLifetime = 1.2f;
            glowMain.startSpeed = 4f;
            glowMain.startSize = 4f;
            glowMain.maxParticles = 25;
            glowMain.simulationSpace = ParticleSystemSimulationSpace.World;
            var glowEmission = glow.emission;
            glowEmission.rateOverTime = 0;
            glowEmission.SetBursts(new[] { new ParticleSystem.Burst(0f, 20) });
            var glowShape = glow.shape;
            glowShape.shapeType = ParticleSystemShapeType.Sphere;
            glowShape.radius = 0.5f;
            var glowColor = glow.colorOverLifetime;
            glowColor.enabled = true;
            var glowGrad = new Gradient();
            glowGrad.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.95f, 0.7f), 0f), new GradientColorKey(new Color(1f, 0.8f, 0.3f), 1f) },
                new[] { new GradientAlphaKey(0.7f, 0f), new GradientAlphaKey(0.3f, 0.5f), new GradientAlphaKey(0f, 1f) });
            glowColor.color = glowGrad;
            var glowSize = glow.sizeOverLifetime;
            glowSize.enabled = true;
            glowSize.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.3f), new Keyframe(0.2f, 1.2f), new Keyframe(1f, 2.5f)));
            ApplyParticleMaterial(glowGo, particleMat);

            burst.Play();
            glow.Play();
            if (optionalAllIn1StyleMaterial != null && particleMat != optionalAllIn1StyleMaterial)
            {
                var destroyMat = root.AddComponent<DestroyMaterialOnDestroy>();
                destroyMat.SetMaterialToDestroy(particleMat);
            }
            Object.Destroy(root, duration);
        }

        private static Texture2D softParticleTexture;
        private static Material cachedBuiltInParticleMaterial;

        /// <summary>Creates or returns a cached soft round particle texture (white with alpha falloff) so particles look like soft circles, not squares.</summary>
        private static Texture2D GetSoftParticleTexture()
        {
            if (softParticleTexture != null) return softParticleTexture;
            const int size = 64;
            softParticleTexture = new Texture2D(size, size);
            softParticleTexture.name = "LevelUpSoftParticle";
            Color[] pixels = new Color[size * size];
            float center = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center) / center;
                    float dy = (y - center) / center;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = d >= 1f ? 0f : Mathf.Clamp01(1f - d * d); // Soft falloff
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            softParticleTexture.SetPixels(pixels);
            softParticleTexture.Apply(true);
            softParticleTexture.filterMode = FilterMode.Bilinear;
            return softParticleTexture;
        }

        private static Material GetLevelUpParticleMaterial()
        {
            if (cachedBuiltInParticleMaterial != null) return cachedBuiltInParticleMaterial;
            Shader s = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                ?? Shader.Find("Particles/Standard Unlit")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Unlit/Color");
            if (s == null) return null;
            cachedBuiltInParticleMaterial = new Material(s);
            cachedBuiltInParticleMaterial.renderQueue = 3000; // Transparent
            Texture2D softTex = GetSoftParticleTexture();
            if (cachedBuiltInParticleMaterial.HasProperty("_BaseMap")) cachedBuiltInParticleMaterial.SetTexture("_BaseMap", softTex);
            if (cachedBuiltInParticleMaterial.HasProperty("_MainTex")) cachedBuiltInParticleMaterial.SetTexture("_MainTex", softTex);
            if (cachedBuiltInParticleMaterial.HasProperty("_BaseColor")) cachedBuiltInParticleMaterial.SetColor("_BaseColor", Color.white);
            else if (cachedBuiltInParticleMaterial.HasProperty("_Color")) cachedBuiltInParticleMaterial.SetColor("_Color", Color.white);
            return cachedBuiltInParticleMaterial;
        }

        private static void ApplyParticleMaterial(GameObject particleGo, Material mat)
        {
            if (mat == null) return;
            var r = particleGo.GetComponent<ParticleSystemRenderer>();
            if (r == null) return;
            r.material = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
        }
    }

    /// <summary>Destroys a material when this GameObject is destroyed (e.g. level-up effect material instance).</summary>
    public class DestroyMaterialOnDestroy : MonoBehaviour
    {
        private Material matToDestroy;

        public void SetMaterialToDestroy(Material mat) => matToDestroy = mat;

        private void OnDestroy()
        {
            if (matToDestroy != null)
                Destroy(matToDestroy);
        }
    }
}
