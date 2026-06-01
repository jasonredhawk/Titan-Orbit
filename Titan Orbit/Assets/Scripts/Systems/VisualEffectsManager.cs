using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Audio;
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
        [Header("Ship collision — weapon-style impact")]
        [Tooltip("Base scale for hull/asteroid collision bursts (matches Bullet impactEffectScale; final scale is this × severity from Starship).")]
        [SerializeField] private float weaponCollisionImpactBaseScale = 0.5f;
        [SerializeField] private float weaponCollisionImpactDuration = 3f;
        [Header("Collision VFX Tuning")]
        [Tooltip("Ship-ship: minimum relative speed required before weapon-style collision VFX can spawn.")]
        [SerializeField, Min(0f)] private float collisionVfxShipMinRelativeSpeed = 2f;
        [Tooltip("Ship-ship: relative speed that maps to maximum collision VFX severity.")]
        [SerializeField, Min(0f)] private float collisionVfxShipMaxRelativeSpeed = 35f;
        [Tooltip("Ship-asteroid: minimum impact force (N) required before weapon-style collision VFX can spawn.")]
        [SerializeField, Min(0f)] private float collisionVfxAsteroidMinImpactForce = 25f;
        [Tooltip("Ship-asteroid: impact force (N) that maps to maximum collision VFX severity.")]
        [SerializeField, Min(0f)] private float collisionVfxAsteroidMaxImpactForce = 1200f;
        [Tooltip("Severity 0 maps to this scale multiplier before base impact scale is applied.")]
        [SerializeField, Min(0.01f)] private float collisionVfxMinScaleMultiplier = 0.35f;
        [Tooltip("Severity 1 maps to this scale multiplier before base impact scale is applied.")]
        [SerializeField, Min(0.01f)] private float collisionVfxMaxScaleMultiplier = 1.85f;
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
        [Header("Floating Count Stack")]
        [Tooltip("Line spacing multiplier for multi-line stacked popups (asteroid hit groups).")]
        [SerializeField] private float floatingCountStackLineSpacing = 0.82f;

        [SerializeField] private Color floatingCountDamageFallbackColor = new Color(1f, 0.3f, 0.3f, 1f);
        [SerializeField] private Color floatingCountHealthPositiveColor = new Color(0.2f, 0.9f, 0.3f, 1f);
        [SerializeField] private Color floatingCountHealthNegativeColor = new Color(0.95f, 0.25f, 0.2f, 1f);
        [SerializeField] private Color floatingCountImpactForceColor = new Color(1f, 0.75f, 0.2f, 1f);
        private int floatingPopupSequence;

        public FloatingCountChannelVisibility FloatingCountVisibility => floatingCountVisibility;

        public float CollisionVfxShipMinRelativeSpeed => Mathf.Max(0f, collisionVfxShipMinRelativeSpeed);
        public float CollisionVfxShipMaxRelativeSpeed => Mathf.Max(CollisionVfxShipMinRelativeSpeed + 0.01f, collisionVfxShipMaxRelativeSpeed);
        public float CollisionVfxAsteroidMinImpactForce => Mathf.Max(0f, collisionVfxAsteroidMinImpactForce);
        public float CollisionVfxAsteroidMaxImpactForce => Mathf.Max(CollisionVfxAsteroidMinImpactForce + 0.01f, collisionVfxAsteroidMaxImpactForce);
        public float CollisionVfxMinScaleMultiplier => Mathf.Max(0.01f, collisionVfxMinScaleMultiplier);
        public float CollisionVfxMaxScaleMultiplier => Mathf.Max(CollisionVfxMinScaleMultiplier + 0.01f, collisionVfxMaxScaleMultiplier);

        private void Awake()
        {
            if (floatingCountVisibility == null)
                floatingCountVisibility = new FloatingCountChannelVisibility();

            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private bool IsFloatingCountChannelVisible(FloatingCountChannel channel)
        {
            return floatingCountVisibility == null || floatingCountVisibility.IsEnabled(channel);
        }

        /// <summary>Client-local floating count (no RPC). Used for bullet impact popups synced to explosion VFX.</summary>
        public void SpawnFloatingCountLocal(Vector3 position, FloatingCountChannel channel, float signedAmount, TeamManager.Team team)
        {
            SpawnFloatingCountPopupLocal(position, channel, signedAmount, team);
        }

        /// <summary>Client-local stacked asteroid feedback (damage, HP, gems, impact force).</summary>
        public void SpawnAsteroidFeedbackLocal(Vector3 position, AsteroidFloatingFeedback feedback)
        {
            SpawnAsteroidFeedbackPopupLocal(position, feedback);
        }

        /// <summary>
        /// Spawns stacked asteroid feedback on every client. Use from server code directly, or from the
        /// owning client after ram/grind (physics runs on owner; dedicated server is not IsServer on ship).
        /// </summary>
        public void SpawnAsteroidFeedback(Vector3 position, AsteroidFloatingFeedback feedback)
        {
            if (IsServer)
                SpawnAsteroidFeedbackFromServerAuthority(position, feedback);
            else
                SpawnAsteroidFeedbackServerRpc(
                    position,
                    feedback.Damage ?? -1f,
                    feedback.RemainingHealth ?? -1f,
                    feedback.RemainingGems ?? -1f,
                    feedback.ImpactForceNewtons ?? -1f,
                    (int)feedback.Team);
        }

        public void SpawnAsteroidFeedbackFromServerAuthority(Vector3 position, AsteroidFloatingFeedback feedback)
        {
            if (!IsServer) return;
            if (IsClient)
                SpawnAsteroidFeedbackPopupLocal(position, feedback);

            SpawnAsteroidFeedbackClientRpc(
                position,
                feedback.Damage ?? -1f,
                feedback.RemainingHealth ?? -1f,
                feedback.RemainingGems ?? -1f,
                feedback.ImpactForceNewtons ?? -1f,
                (int)feedback.Team);
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnAsteroidFeedbackServerRpc(
            Vector3 position,
            float damage,
            float remainingHealth,
            float remainingGems,
            float impactForceNewtons,
            int teamInt)
        {
            SpawnAsteroidFeedbackFromServerAuthority(
                position,
                DecodeAsteroidFeedback(damage, remainingHealth, remainingGems, impactForceNewtons, teamInt));
        }

        [ClientRpc]
        private void SpawnAsteroidFeedbackClientRpc(
            Vector3 position,
            float damage,
            float remainingHealth,
            float remainingGems,
            float impactForceNewtons,
            int teamInt)
        {
            if (IsServer)
                return;

            SpawnAsteroidFeedbackPopupLocal(position, DecodeAsteroidFeedback(damage, remainingHealth, remainingGems, impactForceNewtons, teamInt));
        }

        private static AsteroidFloatingFeedback DecodeAsteroidFeedback(
            float damage,
            float remainingHealth,
            float remainingGems,
            float impactForceNewtons,
            int teamInt)
        {
            return new AsteroidFloatingFeedback
            {
                Team = (TeamManager.Team)teamInt,
                Damage = damage >= 0f ? damage : null,
                RemainingHealth = remainingHealth >= 0f ? remainingHealth : null,
                RemainingGems = remainingGems >= 0f ? remainingGems : null,
                ImpactForceNewtons = impactForceNewtons >= 0f ? impactForceNewtons : null,
            };
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
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayExplosionSound();
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
            SpawnFloatingCountFromServerAuthority(position, (int)FloatingCountChannel.GemPickup, amount, (int)team);
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnFloatingCountServerRpc(Vector3 position, int channelId, float signedAmount, int teamInt)
        {
            SpawnFloatingCountFromServerAuthority(position, channelId, signedAmount, teamInt);
        }

        /// <summary>Server path without queuing a ServerRpc (collision/grind on host).</summary>
        public void SpawnFloatingCountFromServerAuthority(Vector3 position, int channelId, float signedAmount, int teamInt)
        {
            if (!IsServer) return;

            var channel = (FloatingCountChannel)Mathf.Clamp(channelId, 0, FloatingCountFeedbackSettings.MaxChannelIndex);
            var team = (TeamManager.Team)teamInt;

            // Host is server+client: ClientRpc alone can miss local delivery on in-scene managers in some NGO setups.
            if (IsClient)
                SpawnFloatingCountPopupLocal(position, channel, signedAmount, team);

            SpawnFloatingCountClientRpc(position, channelId, signedAmount, teamInt);
        }

        /// <summary>Server-only convenience wrapper for gameplay code already running on the server.</summary>
        public void SpawnFloatingCountFromServerAuthority(Vector3 position, FloatingCountChannel channel, float signedAmount, TeamManager.Team team)
        {
            SpawnFloatingCountFromServerAuthority(position, (int)channel, signedAmount, (int)team);
        }

        [ClientRpc]
        private void SpawnFloatingCountClientRpc(Vector3 position, int channelId, float signedAmount, int teamInt)
        {
            // Host already spawned in ServerRpc; remote clients only here.
            if (IsServer)
                return;

            var channel = (FloatingCountChannel)Mathf.Clamp(channelId, 0, FloatingCountFeedbackSettings.MaxChannelIndex);
            TeamManager.Team team = (TeamManager.Team)teamInt;
            SpawnFloatingCountPopupLocal(position, channel, signedAmount, team);
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnAsteroidStatsFloatingTextServerRpc(Vector3 position, float remainingHealth, float remainingGems, int teamInt)
        {
            SpawnAsteroidFeedbackFromServerAuthority(position, new AsteroidFloatingFeedback
            {
                Team = (TeamManager.Team)teamInt,
                RemainingHealth = remainingHealth,
                RemainingGems = remainingGems,
            });
        }

        public void SpawnAsteroidStatsFloatingTextFromServerAuthority(Vector3 position, float remainingHealth, float remainingGems, int teamInt)
        {
            SpawnAsteroidFeedbackFromServerAuthority(position, new AsteroidFloatingFeedback
            {
                Team = (TeamManager.Team)teamInt,
                RemainingHealth = remainingHealth,
                RemainingGems = remainingGems,
            });
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnImpactForceFloatingTextServerRpc(Vector3 position, float impactForceNewtons)
        {
            SpawnAsteroidFeedbackFromServerAuthority(position, new AsteroidFloatingFeedback
            {
                ImpactForceNewtons = impactForceNewtons,
            });
        }

        public void SpawnImpactForceFloatingTextFromServerAuthority(Vector3 position, float impactForceNewtons)
        {
            SpawnAsteroidFeedbackFromServerAuthority(position, new AsteroidFloatingFeedback
            {
                ImpactForceNewtons = impactForceNewtons,
            });
        }

        private void SpawnFloatingCountPopupLocal(Vector3 position, FloatingCountChannel channel, float signedAmount, TeamManager.Team team)
        {
            if (!IsFloatingCountChannelVisible(channel))
                return;

            TMP_FontAsset fontToUse = floatingCountFont != null ? floatingCountFont : TMP_Settings.defaultFontAsset;
            if (fontToUse == null)
            {
                Debug.LogWarning("VisualEffectsManager: FloatingCountPopup font missing (assign VisualEffectsManager.floatingCountFont).");
                return;
            }

            float abs = Mathf.Abs(signedAmount);
            if (abs < 0.01f)
                return;
            int amountInt = Mathf.RoundToInt(abs);
            if (amountInt <= 0)
                amountInt = 1;

            char sign = signedAmount >= 0f ? '+' : '-';
            bool compactGemLabel = channel == FloatingCountChannel.GemPickup || channel == FloatingCountChannel.GemDeposit;
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

            string message = compactGemLabel ? $"{sign}{amountInt}" : $"{sign}{amountInt} {label}";

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

        private void SpawnAsteroidFeedbackPopupLocal(Vector3 position, AsteroidFloatingFeedback feedback)
        {
            FloatingCountStackLine[] lines = BuildAsteroidFeedbackLines(feedback);
            if (lines == null || lines.Length == 0)
                return;

            TMP_FontAsset fontToUse = floatingCountFont != null ? floatingCountFont : TMP_Settings.defaultFontAsset;
            if (fontToUse == null)
                return;

            Vector3 spawnPos = GetStackSpawnPosition(position);
            GameObject go = new GameObject("FloatingCountStack_AsteroidHit");
            go.transform.position = spawnPos;

            var popup = go.AddComponent<FloatingCountStackPopup>();
            popup.Initialize(
                lines,
                fontToUse,
                floatingCountFontSize,
                floatingCountStackLineSpacing,
                floatingCountDuration,
                floatingCountRiseSpeed,
                Mathf.Max(0f, floatingCountLateralDriftMax)
            );
        }

        private FloatingCountStackLine[] BuildAsteroidFeedbackLines(AsteroidFloatingFeedback feedback)
        {
            if (floatingCountVisibility == null)
                return System.Array.Empty<FloatingCountStackLine>();

            var lines = new System.Collections.Generic.List<FloatingCountStackLine>(4);
            TeamManager.Team team = feedback.Team;

            if (floatingCountVisibility.IsAsteroidDamageEnabled()
                && feedback.Damage.HasValue
                && feedback.Damage.Value > 0.0001f)
            {
                int damageInt = Mathf.Max(1, Mathf.RoundToInt(feedback.Damage.Value));
                Color damageColor = team != TeamManager.Team.None
                    ? TeamManager.GetTeamColor(team)
                    : floatingCountDamageFallbackColor;
                lines.Add(new FloatingCountStackLine($"+{damageInt} Damage", damageColor));
            }

            if (floatingCountVisibility.IsAsteroidHealthRemainingEnabled()
                && feedback.RemainingHealth.HasValue)
            {
                int hp = Mathf.Max(0, Mathf.RoundToInt(feedback.RemainingHealth.Value));
                lines.Add(new FloatingCountStackLine($"HP Left: {hp}", floatingCountHealthPositiveColor));
            }

            if (floatingCountVisibility.IsAsteroidGemsRemainingEnabled()
                && feedback.RemainingGems.HasValue)
            {
                int gems = Mathf.Max(0, Mathf.RoundToInt(feedback.RemainingGems.Value));
                Color gemsColor = team != TeamManager.Team.None
                    ? TeamManager.GetTeamColor(team)
                    : new Color(0.85f, 0.95f, 1f, 1f);
                lines.Add(new FloatingCountStackLine($"Gems: {gems}", gemsColor));
            }

            if (floatingCountVisibility.IsAsteroidImpactForceEnabled()
                && feedback.ImpactForceNewtons.HasValue
                && feedback.ImpactForceNewtons.Value >= 1f)
            {
                int force = Mathf.RoundToInt(feedback.ImpactForceNewtons.Value);
                lines.Add(new FloatingCountStackLine($"{force:N0} Force", floatingCountImpactForceColor));
            }

            return lines.Count == 0 ? System.Array.Empty<FloatingCountStackLine>() : lines.ToArray();
        }

        /// <summary>Single anchor for stacked groups — no spiral spread so lines stay together.</summary>
        private Vector3 GetStackSpawnPosition(Vector3 position)
        {
            Vector3 spawnPos = position;
            spawnPos.y = floatingCountVerticalOffset;
            if (spawnPos.y < 4f)
                spawnPos.y = 4f;
            return spawnPos;
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

        /// <summary>
        /// Sparks/impact burst using the same prefab as bullet hits (<see cref="CombatSystem.GetImpactPrefabFromBank"/>),
        /// scaled by collision severity. Falls back to <see cref="asteroidCollisionEffect"/> when the bank has no impact VFX.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void SpawnWeaponCollisionImpactServerRpc(Vector3 position, Vector3 normal, float scaleMultiplier, float audioPitch, int impactPrefabBankIndex, int teamInt)
        {
            SpawnWeaponCollisionImpactClientRpc(position, normal, scaleMultiplier, audioPitch, impactPrefabBankIndex, teamInt);
        }

        [ClientRpc]
        private void SpawnWeaponCollisionImpactClientRpc(Vector3 position, Vector3 normal, float scaleMultiplier, float audioPitch, int impactPrefabBankIndex, int teamInt)
        {
            TeamManager.Team team = (TeamManager.Team)teamInt;
            GameObject prefab = null;
            if (impactPrefabBankIndex >= 0 && CombatSystem.Instance != null)
                prefab = CombatSystem.Instance.GetImpactPrefabFromBank(impactPrefabBankIndex, team);
            if (prefab == null)
                prefab = asteroidCollisionEffect;
            if (prefab == null) return;

            Vector3 n = normal;
            n.y = 0f;
            if (n.sqrMagnitude < 0.0001f)
                n = Vector3.forward;
            else
                n.Normalize();

            Quaternion rot = Quaternion.LookRotation(n, Vector3.up);
            GameObject go = Instantiate(prefab, position, rot);
            float mul = Mathf.Max(0.12f, scaleMultiplier);
            float finalScale = weaponCollisionImpactBaseScale * mul;
            ApplyCollisionImpactVisualScale(go, finalScale);
            FixAllIn1VfxForUrp(go);
            SetAudioPitchInHierarchy(go, audioPitch);
            Destroy(go, weaponCollisionImpactDuration);
        }

        private static void SetAudioPitchInHierarchy(GameObject root, float pitch)
        {
            if (root == null) return;
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            if (sources == null || sources.Length == 0) return;
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null)
                    sources[i].pitch = pitch;
            }
        }

        /// <summary>
        /// Uniformly scales impact VFX so collisions visibly differ at low/high severity.
        /// Some particle prefabs use world space or constant modules and ignore transform scale alone.
        /// </summary>
        private static void ApplyCollisionImpactVisualScale(GameObject root, float scale)
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
