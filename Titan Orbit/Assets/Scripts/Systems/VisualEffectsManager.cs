using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TMPro;

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

        [Header("Floating Count Popups")]
        [Tooltip("Optional: overrides for People load/unload icon and color. Per-channel on/off is in Floating Count Visibility below.")]
        [SerializeField] private FloatingCountFeedbackSettings floatingCountFeedbackSettings;
        [Header("Floating count — show per channel")]
        [Tooltip("World-space +N popups for each gameplay source.")]
        [SerializeField] private FloatingCountChannelVisibility floatingCountVisibility = new FloatingCountChannelVisibility();
        [Tooltip("World-space font used to render the floating (+N) popup text.")]
        [SerializeField] private TMP_FontAsset floatingCountFont;
        [SerializeField] private Sprite floatingCountGemIcon;
        [SerializeField] private Sprite floatingCountDamageIcon;
        [SerializeField] private Sprite floatingCountHealthIcon;
        [Tooltip("Used for people load/unload when the feedback settings asset has no people icon assigned.")]
        [SerializeField] private Sprite floatingCountPeopleIcon;
        [SerializeField] private Color floatingCountPeopleColor = new Color(1f, 0.9f, 0.25f, 1f);

        [SerializeField] private float floatingCountDuration = 1.7f;
        [SerializeField] private float floatingCountRiseSpeed = 2.5f;
        [SerializeField] private float floatingCountFontSize = 10f;
        [SerializeField] private float floatingCountIconScale = 0.1f;
        [SerializeField] private Vector3 floatingCountIconLocalOffset = new Vector3(-0.35f, 0.0f, 0f);
        [SerializeField] private float floatingCountVerticalOffset = 3.5f;
        [Header("Floating Count Spread")]
        [Tooltip("Extra random XZ offset around the anchor so simultaneous popups do not share one spot.")]
        [SerializeField] private float floatingCountSpawnJitterRadius = 1.05f;
        [Tooltip("Scales the golden-spiral ring radius (√n * step), capped by Max Spread Radius.")]
        [SerializeField] private float floatingCountSpawnRingStep = 0.32f;
        [Tooltip("Upper bound for spiral radius from the anchor (world units on XZ).")]
        [SerializeField] private float floatingCountMaxSpreadRadius = 3.75f;
        [Tooltip("How many spiral steps before radius wraps (angle still advances, so overlap stays low).")]
        [SerializeField] private int floatingCountSpiralPeriod = 40;
        [Tooltip("Perpendicular drift speed on the play plane so popups fan out instead of stacking in one line.")]
        [SerializeField] private float floatingCountLateralDriftMax = 0.55f;

        [SerializeField] private Color floatingCountDamageFallbackColor = new Color(1f, 0.3f, 0.3f, 1f);
        [SerializeField] private Color floatingCountHealthPositiveColor = new Color(0.2f, 0.9f, 0.3f, 1f);
        [SerializeField] private Color floatingCountHealthNegativeColor = new Color(0.95f, 0.25f, 0.2f, 1f);
        private int floatingPopupSequence;

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
            TeamManager.Team team = (TeamManager.Team)teamInt;
            if (floatingCountVisibility != null && !floatingCountVisibility.IsEnabled(FloatingCountChannel.GemPickup))
                return;

            // Prefer the new icon+TMP popup; falls back to legacy GemPickupText only if
            // there is no usable TMP font (assigned or default).
            if (floatingCountFont != null || TMP_Settings.defaultFontAsset != null)
            {
                SpawnFloatingCountPopupLocal(position, FloatingCountChannel.GemPickup, amount, team);
                return;
            }

            if (gemPickupTextPrefab == null) return;
            GameObject go = Instantiate(gemPickupTextPrefab, position, Quaternion.identity);
            var text = go.GetComponent<GemPickupText>() ?? go.AddComponent<GemPickupText>();
            text?.Initialize(Mathf.RoundToInt(amount), team, gemPickupTextDuration);
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnFloatingCountServerRpc(Vector3 position, int channelId, float signedAmount, int teamInt)
        {
            SpawnFloatingCountClientRpc(position, channelId, signedAmount, teamInt);
        }

        [ClientRpc]
        private void SpawnFloatingCountClientRpc(Vector3 position, int channelId, float signedAmount, int teamInt)
        {
            var channel = (FloatingCountChannel)Mathf.Clamp(channelId, 0, FloatingCountFeedbackSettings.MaxChannelIndex);
            TeamManager.Team team = (TeamManager.Team)teamInt;
            SpawnFloatingCountPopupLocal(position, channel, signedAmount, team);
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnAsteroidStatsFloatingTextServerRpc(Vector3 position, float remainingHealth, float remainingGems, int teamInt)
        {
            SpawnAsteroidStatsFloatingTextClientRpc(position, remainingHealth, remainingGems, teamInt);
        }

        [ClientRpc]
        private void SpawnAsteroidStatsFloatingTextClientRpc(Vector3 position, float remainingHealth, float remainingGems, int teamInt)
        {
            TeamManager.Team team = (TeamManager.Team)teamInt;
            if (floatingCountVisibility != null && !floatingCountVisibility.IsEnabled(FloatingCountChannel.DamageAsteroid))
                return;

            Color hpColor = floatingCountHealthPositiveColor;
            Color gemsColor = team != TeamManager.Team.None ? TeamManager.GetTeamColor(team) : new Color(0.85f, 0.95f, 1f, 1f);
            string hpMessage = $"HP Left: {Mathf.Max(0, Mathf.RoundToInt(remainingHealth))}";
            string gemsMessage = $"Gems: {Mathf.Max(0, Mathf.RoundToInt(remainingGems))}";

            SpawnCustomFloatingCountPopupLocal(position, hpMessage, floatingCountHealthIcon, hpColor);
            SpawnCustomFloatingCountPopupLocal(position, gemsMessage, floatingCountGemIcon, gemsColor);
        }

        private void SpawnFloatingCountPopupLocal(Vector3 position, FloatingCountChannel channel, float signedAmount, TeamManager.Team team)
        {
            if (floatingCountVisibility != null && !floatingCountVisibility.IsEnabled(channel))
                return;

            TMP_FontAsset fontToUse = floatingCountFont != null ? floatingCountFont : TMP_Settings.defaultFontAsset;
            if (fontToUse == null)
            {
                Debug.LogWarning("VisualEffectsManager: FloatingCountPopup font missing (assign VisualEffectsManager.floatingCountFont).");
                return;
            }

            float abs = Mathf.Abs(signedAmount);
            int amountInt = Mathf.RoundToInt(abs);
            if (amountInt <= 0) return;

            char sign = signedAmount >= 0f ? '+' : '-';
            string label = channel switch
            {
                FloatingCountChannel.GemPickup => "Gems",
                FloatingCountChannel.GemDeposit => "Gems",
                FloatingCountChannel.DamageAsteroid => "Damage",
                FloatingCountChannel.DamageShipOrDrone => "Damage",
                FloatingCountChannel.DamageMoon => "Damage",
                FloatingCountChannel.HealthChange => "Health",
                FloatingCountChannel.PeopleLoad => "People",
                FloatingCountChannel.PeopleUnload => "People",
                FloatingCountChannel.Healing => "Health",
                FloatingCountChannel.HealthRegen => "Health",
                FloatingCountChannel.Energy => "Energy",
                FloatingCountChannel.Upgrades => "Upgrade",
                _ => "Value"
            };

            string message = $"{sign}{amountInt} {label}";

            Sprite icon = null;
            Color color = Color.white;

            switch (channel)
            {
                case FloatingCountChannel.GemPickup:
                case FloatingCountChannel.GemDeposit:
                    icon = floatingCountGemIcon;
                    color = team != TeamManager.Team.None ? TeamManager.GetTeamColor(team) : new Color(0.85f, 0.95f, 1f, 1f);
                    break;

                case FloatingCountChannel.DamageAsteroid:
                case FloatingCountChannel.DamageShipOrDrone:
                case FloatingCountChannel.DamageMoon:
                    icon = floatingCountDamageIcon;
                    color = team != TeamManager.Team.None ? TeamManager.GetTeamColor(team) : floatingCountDamageFallbackColor;
                    break;

                case FloatingCountChannel.HealthChange:
                case FloatingCountChannel.Healing:
                case FloatingCountChannel.HealthRegen:
                    icon = floatingCountHealthIcon;
                    color = channel == FloatingCountChannel.HealthChange
                        ? (signedAmount >= 0f ? floatingCountHealthPositiveColor : floatingCountHealthNegativeColor)
                        : floatingCountHealthPositiveColor;
                    break;

                case FloatingCountChannel.PeopleLoad:
                case FloatingCountChannel.PeopleUnload:
                    icon = floatingCountFeedbackSettings != null && floatingCountFeedbackSettings.peopleIcon != null
                        ? floatingCountFeedbackSettings.peopleIcon
                        : floatingCountPeopleIcon;
                    color = floatingCountFeedbackSettings != null
                        ? floatingCountFeedbackSettings.peopleColor
                        : floatingCountPeopleColor;
                    break;

                case FloatingCountChannel.Energy:
                    icon = null;
                    color = new Color(0.35f, 0.75f, 1f, 1f);
                    break;

                case FloatingCountChannel.Upgrades:
                    icon = null;
                    color = new Color(0.95f, 0.85f, 0.35f, 1f);
                    break;
            }

            SpawnPopup(message, icon, color, $"FloatingCountPopup_{channel}", position, fontToUse);
        }

        private void SpawnCustomFloatingCountPopupLocal(Vector3 position, string message, Sprite icon, Color color)
        {
            TMP_FontAsset fontToUse = floatingCountFont != null ? floatingCountFont : TMP_Settings.defaultFontAsset;
            if (fontToUse == null)
                return;

            SpawnPopup(message, icon, color, "FloatingCountPopup_AsteroidStats", position, fontToUse);
        }

        /// <summary>
        /// Picks a spawn point using a golden-angle spiral plus anchor-based phase and jitter.
        /// Sequential popups at the same world spot spread on XZ instead of stacking in one column.
        /// </summary>
        private Vector3 GetSpreadSpawnPosition(Vector3 position)
        {
            int n = floatingPopupSequence++;
            // Golden angle (~137.5°): well-distributed angles; irrational step avoids periodic clumping.
            const float goldenAngle = 2.39996323f;
            // Phase from anchor so different locations do not share identical spiral alignment.
            float phase = (position.x * 12.9898f + position.z * 78.233f) * 0.215f;
            float angle = phase + goldenAngle * n;
            int period = Mathf.Max(8, floatingCountSpiralPeriod);
            float ringR = Mathf.Sqrt((n % period) + 1) * Mathf.Max(0.01f, floatingCountSpawnRingStep);
            ringR = Mathf.Min(ringR, Mathf.Max(0.5f, floatingCountMaxSpreadRadius));

            Vector3 ring = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * ringR;
            Vector2 jitter = Random.insideUnitCircle * Mathf.Max(0f, floatingCountSpawnJitterRadius);
            Vector3 spawnPos = position + ring + new Vector3(jitter.x, floatingCountVerticalOffset, jitter.y);

            // Hard floor so stale serialized inspector values cannot pin popups to ground.
            if (spawnPos.y < 4f)
                spawnPos.y = 4f;
            return spawnPos;
        }

        private void SpawnPopup(string message, Sprite icon, Color color, string popupName, Vector3 position, TMP_FontAsset fontToUse)
        {
            if (string.IsNullOrEmpty(message))
                return;

            Vector3 spawnPos = GetSpreadSpawnPosition(position);
            GameObject go = new GameObject(popupName);
            go.transform.position = spawnPos;

            var popup = go.AddComponent<FloatingCountPopup>();
            popup.Initialize(
                message,
                icon,
                color,
                fontToUse,
                floatingCountFontSize,
                floatingCountDuration,
                floatingCountRiseSpeed,
                floatingCountIconScale,
                floatingCountIconLocalOffset,
                Mathf.Max(0f, floatingCountLateralDriftMax)
            );
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
