using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;

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
        [Tooltip("Base scale for level-up VFX (e.g. 4.0). Final scale is this value multiplied by a planet-size factor.")]
        [SerializeField] private float levelUpEffectScale = 4f;
        [Header("Asteroid Collision")]
        [Tooltip("Particles spawned where ships collide with asteroids (e.g. sparks).")]
        [SerializeField] private GameObject asteroidCollisionEffect;
        [SerializeField] private float asteroidCollisionEffectDuration = 2f;
        [Header("Gem Pickup Text")]
        [SerializeField] private GameObject gemPickupTextPrefab;
        [SerializeField] private float gemPickupTextDuration = 1f;

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

        [ServerRpc(RequireOwnership = false)]
        public void SpawnGemPickupTextServerRpc(Vector3 position, float amount, TeamManager.Team team)
        {
            SpawnGemPickupTextClientRpc(position, amount, (int)team);
        }

        [ClientRpc]
        private void SpawnGemPickupTextClientRpc(Vector3 position, float amount, int teamInt)
        {
            if (gemPickupTextPrefab == null) return;

            GameObject go = Instantiate(gemPickupTextPrefab, position, Quaternion.identity);
            var text = go.GetComponent<GemPickupText>() ?? go.AddComponent<GemPickupText>();
            if (text != null)
            {
                text.Initialize(Mathf.RoundToInt(amount), (TeamManager.Team)teamInt, gemPickupTextDuration);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnAsteroidCollisionEffectServerRpc(Vector3 position, Vector3 normal)
        {
            SpawnAsteroidCollisionEffectClientRpc(position, normal);
        }

        [ClientRpc]
        private void SpawnAsteroidCollisionEffectClientRpc(Vector3 position, Vector3 normal)
        {
            if (asteroidCollisionEffect == null) return;

            Quaternion rotation = Quaternion.identity;
            if (normal.sqrMagnitude > 0.0001f)
            {
                normal.Normalize();
                rotation = Quaternion.LookRotation(normal, Vector3.up);
            }

            GameObject effect = Instantiate(asteroidCollisionEffect, position, rotation);
            FixAllIn1VfxForUrp(effect);
            Destroy(effect, asteroidCollisionEffectDuration);
        }

        /// <summary>Play level-up burst. Uses prefab VFX only (same URP fix as bullet impact), scaled by planet size.</summary>
        public void PlayLevelUpEffect(Vector3 position, float planetSize = 1f)
        {
            float sizeFactor = Mathf.Clamp(planetSize / 4f, 1f, 6f); // Size 4 -> 1x, 12 -> 3x, 20 -> 5x
            float finalScale = levelUpEffectScale * sizeFactor;

            if (levelUpEffect != null)
            {
                GameObject go = Instantiate(levelUpEffect, position, Quaternion.identity);
                go.transform.localScale = Vector3.one * finalScale;
                FixAllIn1VfxForUrp(go); // Same as bullet DisableGrabPassMaterials – required for URP
                Destroy(go, 4f);
                return;
            }

            // Fallback to existing prefab effects (no procedural billboard particles)
            GameObject fallbackPrefab = captureEffect != null ? captureEffect : explosionEffect;
            if (fallbackPrefab != null)
            {
                GameObject go = Instantiate(fallbackPrefab, position, Quaternion.identity);
                go.transform.localScale = Vector3.one * finalScale;
                FixAllIn1VfxForUrp(go);
                Destroy(go, 4f);
                return;
            }

            Debug.LogWarning("Level-up VFX missing: assign Level Up Effect prefab on VisualEffectsManager.");
        }

        /// <summary>Play level-up effect at position. Call from anywhere (e.g. Planet level-up).</summary>
        public static void PlayLevelUpEffectStatic(Vector3 position, float planetSize = 1f)
        {
            VisualEffectsManager vfx = Object.FindFirstObjectByType<VisualEffectsManager>();
            if (vfx != null)
                vfx.PlayLevelUpEffect(position, planetSize);
            else
                Debug.LogWarning("VisualEffectsManager not found; cannot play level-up VFX.");
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

    }
}
