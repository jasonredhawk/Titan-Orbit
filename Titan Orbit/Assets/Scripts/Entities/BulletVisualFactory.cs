using System.Collections.Generic;
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
        /// <summary>Unused. Drawn size is the fire-power ratio times the bank global scale.</summary>
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
        /// WebGL cannot <c>Play</c> an <see cref="AudioSource"/> on the frame its shell
        /// was enabled — that logs "Can not play a disabled audio source". These wait
        /// until the next presentation frame.
        /// </summary>
        struct DeferredAudioReplay
        {
            public GameObject Root;
            public int Frame;
        }

        static readonly List<DeferredAudioReplay> s_deferredAudioReplay = new List<DeferredAudioReplay>(8);

        /// <summary>
        /// Drawn tracer size = fire-power scale × the one bank-wide global.
        /// <paramref name="scaleMultiplier"/> is fire power ÷ the Bullet VFX Bank reference (default 4).
        /// Bullet type does not change size.
        /// </summary>
        /// <param name="bankIndex">Unused. Kept so existing call sites still compile.</param>
        public static float GetBulletVisualScale(BulletVfxBank bank, float scaleMultiplier, int bankIndex = -1)
        {
            _ = bankIndex;
            float globalScale = bank == null
                ? LegacyGlobalVisualScale
                : bank.GlobalVisualScaleMultiplier;

            return scaleMultiplier * globalScale;
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
            float firePower,
            bool noTrail)
        {
            // [TITAN-ORBIT] bankIndex also selects per-category Global Visual Scale (default 1).
            float scale = GetBulletVisualScale(bank, scaleMultiplier, bankIndex);

            GameObject visualPrefab = null;
            if (!Application.isMobilePlatform && bank != null)
                visualPrefab = bank.GetProjectileVisualPrefab(bankIndex, team);

            GameObject visual;
            if (visualPrefab != null)
            {
                // Prefab is already the team's colour for this bullet type. Do not restamp it.
                visual = Object.Instantiate(visualPrefab, parent);
                VfxUrpCompat.FixAllIn1MaterialsForUrp(visual);
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
                visual = CreateCustomizableVfxStyle(
                    shape, scale, bulletSpeed, noTrail, GetTeamBulletColor(team));
                visual.transform.SetParent(parent, false);
            }

            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            if (visualPrefab == null)
                visual.transform.localScale = Vector3.one * scale;
            SetAudioPitchInHierarchy(visual, GetFirePowerSoundPitch(firePower));
            return visual;
        }

        public static void PlayMuzzleVfx(
            Vector3 position,
            Vector3 direction,
            BulletVfxBank bank,
            int bankIndex,
            TeamId team,
            float scaleMultiplier,
            float firePower,
            float baseFirePower = 0f,
            float firePowerPerExtra = 0f)
        {
            // Keep authored / mount world Y so the flash sits on the weapon, not the ground plane.
            Vector3 dir = direction;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f)
                dir = Vector3.forward;
            dir.Normalize();

            float visualScale = GetBulletVisualScale(bank, scaleMultiplier, bankIndex);
            float pitch = GetFirePowerSoundPitch(firePower, baseFirePower, firePowerPerExtra);
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
                    SetAudioPitchInHierarchy(muzzle, pitch, position);
                    // Shell is still inactive. Prepare enables it once so this barrel's
                    // flash plays here, including when several guns fire on the same frame.
                    VfxUrpCompat.PrepareVfxInstance(muzzle);
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
            Transform attachParent = null,
            float duration = 0f,
            Vector3 surfaceNormal = default,
            float baseFirePower = 0f,
            float firePowerPerExtra = 0f)
        {
            // [TITAN-ORBIT] Isolation F1 — skip impact Instantiates/Rent to bisect destroy stutter.
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
                return;

            // Keep the caller Y (drawn surface). Flattening to 0 put flashes under large rocks / moons.
            float impactScale = GetImpactScale(bank, scaleMultiplier, bankIndex);
            float pitch = GetFirePowerSoundPitch(damage, baseFirePower, firePowerPerExtra);
            float life = duration > 0.05f ? duration : DefaultImpactDuration;

            float hear = GameplaySfxProximity.VolumeAt(position);
            if (Application.isMobilePlatform)
            {
                VfxUrpCompat.SpawnMobileImpactBurst(position, GetTeamBulletColor(team), impactScale);
                if (hear > 0.001f)
                    AudioManager.Instance?.PlayBulletImpactSound(pitch, hear);
                return;
            }

            GameObject prefab = bank != null ? bank.GetImpactPrefab(bankIndex, team) : null;
            if (prefab == null)
            {
                VfxUrpCompat.SpawnMobileImpactBurst(position, GetTeamBulletColor(team), impactScale);
                if (hear > 0.001f)
                    AudioManager.Instance?.PlayBulletImpactSound(pitch, hear);
                return;
            }

            SpawnImpactAt(position, prefab, pitch, impactScale, life, attachParent, surfaceNormal);
            if (hear > 0.001f)
                AudioManager.Instance?.PlayBulletImpactSound(pitch, hear);
        }

        /// <summary>
        /// Flat colour for the procedural sphere used when no bank prefab exists.
        /// Bank projectiles, muzzles, and impacts keep their authored team colours.
        /// </summary>
        public static void ApplyColorToVisual(GameObject root, Color color)
        {
            // --- Apply changes ---
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] sharedBefore = r.sharedMaterials;
                if (sharedBefore == null) continue;
                for (int i = 0; i < sharedBefore.Length; i++)
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
            SetAudioPitchInHierarchy(root, pitch, default, applyHear: false);
        }

        /// <summary>
        /// Sets pitch and ducks prefab <see cref="AudioSource"/>s by toroidal distance
        /// from the local ship. Distant PlayOnAwake clips are stopped.
        /// </summary>
        public static void SetAudioPitchInHierarchy(GameObject root, float pitch, Vector3 worldPosition)
        {
            SetAudioPitchInHierarchy(root, pitch, worldPosition, applyHear: true);
        }

        static void SetAudioPitchInHierarchy(GameObject root, float pitch, Vector3 worldPosition, bool applyHear)
        {
            // --- SetAudioPitchInHierarchy ---
            if (root == null) return;
            float hear = applyHear ? GameplaySfxProximity.VolumeAt(worldPosition) : 1f;
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] == null)
                    continue;
                sources[i].pitch = pitch;
                if (applyHear)
                    GameplaySfxProximity.Apply(sources[i], hear);
            }
        }

        /// <summary>
        /// Stops and plays every live <see cref="AudioSource"/> so a size retune is
        /// heard from the first sample. Skips inactive sources — WebGL logs
        /// "Can not play a disabled audio source" and drops the clip.
        /// </summary>
        public static void ReplayAudioInHierarchy(GameObject root)
        {
            // playOnAwake was held off so enable would not start the prefab pitch.
            // Turn it back on so the next pool rent still auto-plays.
            PlayRetunedAudio(root, restorePlayOnAwake: true);
        }

        /// <summary>
        /// Plays blasts queued by <see cref="QueueDeferredAudioReplay"/>. Call once per
        /// presentation frame. A replay stays queued through the frame the shell was
        /// enabled, because WebGL still treats that source as disabled.
        /// </summary>
        public static void TickDeferredAudioReplay()
        {
            int count = s_deferredAudioReplay.Count;
            if (count == 0)
                return;

            int frame = Time.frameCount;
            for (int i = count - 1; i >= 0; i--)
            {
                DeferredAudioReplay entry = s_deferredAudioReplay[i];
                if (entry.Frame >= frame)
                    continue;

                s_deferredAudioReplay.RemoveAt(i);
                PlayRetunedAudio(entry.Root, restorePlayOnAwake: true);
            }
        }

        /// <summary>
        /// Keeps <c>playOnAwake</c> from starting the clip when the pooled shell enables.
        /// The deferred replay turns it back on so the next rent still auto-plays.
        /// </summary>
        static void HoldPlayOnAwake(GameObject root)
        {
            if (root == null)
                return;

            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null)
                    sources[i].playOnAwake = false;
            }
        }

        static void QueueDeferredAudioReplay(GameObject root)
        {
            int frame = Time.frameCount;
            for (int i = 0; i < s_deferredAudioReplay.Count; i++)
            {
                if (s_deferredAudioReplay[i].Root != root)
                    continue;

                DeferredAudioReplay updated = s_deferredAudioReplay[i];
                updated.Frame = frame;
                s_deferredAudioReplay[i] = updated;
                return;
            }

            s_deferredAudioReplay.Add(new DeferredAudioReplay
            {
                Root = root,
                Frame = frame,
            });
        }

        static void PlayRetunedAudio(GameObject root, bool restorePlayOnAwake)
        {
            if (root == null)
                return;

            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            bool live = root.activeInHierarchy;
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource source = sources[i];
                if (source == null)
                    continue;

                if (restorePlayOnAwake)
                    source.playOnAwake = true;

                // Muted sources were ducked by hear range — replaying them would
                // spend a voice on a blast the local ship should not hear.
                // Inactive sources are the WebGL "disabled audio source" warning.
                if (!live || !source.isActiveAndEnabled || source.mute)
                    continue;

                source.Stop();
                source.Play();
            }
        }

        /// <summary>
        /// Chromatic piano pitch from Extra Level fire power.
        /// Each gun / cannon / rocket / sniper uses its authored base as the top C
        /// and walks down toward base + perExtra × max Extra Levels.
        /// Used for muzzle one-shots, tracer prefab AudioSources, and bullet impacts.
        /// </summary>
        /// <param name="firePower">Live Extra Level fire power (pre-bank). 0 falls back to top C.</param>
        /// <param name="baseFirePower">Catalog / unique-component base (top C). 0 = use live as base.</param>
        /// <param name="firePowerPerExtra">Catalog Per Extra Level. MEGA unique weapons are 0.</param>
        /// <returns>Unity <see cref="AudioSource.pitch"/> multiplier.</returns>
        public static float GetFirePowerSoundPitch(
            float firePower,
            float baseFirePower = 0f,
            float firePowerPerExtra = 0f)
        {
            // --- Per-weapon Extra Level piano ---
            // [TITAN-ORBIT] Do not pass bank-scaled plan.Damage as live — B-key
            // Fireballs 1.15× would shove a MEGA cannon off its own top C.
            float pianoAmount = GemMusicalPitch.FirePowerToPianoAmount(
                firePower, baseFirePower, firePowerPerExtra);
            AudioManager audio = AudioManager.Instance;
            if (audio != null)
                return audio.ResolveFirePowerPitch(pianoAmount);
            return GemMusicalPitch.ResolvePitch(pianoAmount, 1f, 0.01f);
        }

        /// <summary>
        /// PlayOneShot volume scale from raw fire power. Everyday guns stay at 1;
        /// Titan ~50-FP cannons use the heavy multiplier. Pitch uses Extra Level.
        /// </summary>
        /// <param name="firePower">Per-shot damage (mount fire power, not hull-sum DPS).</param>
        /// <returns>Multiplier on top of <c>AudioManager</c> shoot mix.</returns>
        public static float GetFirePowerShootVolume(float firePower)
        {
            // --- Fire-power loudness ---
            // [TITAN-ORBIT] One shared shoot clip. Volume follows raw damage so a
            // slow Titan cannon is not lost next to machine-gun chatter.
            AudioManager audio = AudioManager.Instance;
            if (audio != null)
                return audio.ResolveFirePowerShootVolumeScale(firePower);
            return GemMusicalPitch.FirePowerToShootVolumeScale(firePower);
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
            Transform attachParent = null,
            Vector3 surfaceNormal = default,
            bool replayAudio = false)
        {
            // --- SpawnImpactAt (pooled) ---
            if (prefab == null)
                return;

            if (!BulletOneShotVfxPool.TryRent(prefab, out GameObject go) || go == null)
                return;

            // Sci-Fi impact prefabs are authored with +Y = surface normal
            // (SciFiProjectileScript uses FromToRotation(up, hit.normal)).
            // LookRotation / identity on a ship hull made Fire/Glow cones read as muzzle.
            Quaternion rot = surfaceNormal.sqrMagnitude > 0.0001f
                ? Quaternion.FromToRotation(Vector3.up, surfaceNormal.normalized)
                : Quaternion.identity;
            go.transform.SetPositionAndRotation(position, rot);
            VfxUrpCompat.ApplyImpactVisualScale(go, scale);
            SetAudioPitchInHierarchy(go, pitch, position);
            // The rented shell is still inactive. Play() here is a no-op, and WebGL
            // logs "Can not play a disabled audio source". Hold playOnAwake so enable
            // does not start the prefab pitch, then play once the source is live.
            // WebGL also rejects Play on the enable frame, so that path waits one frame.
            bool webGlReplay = replayAudio && Application.platform == RuntimePlatform.WebGLPlayer;
            if (replayAudio)
                HoldPlayOnAwake(go);
            // worldPositionStays — flash stays on the surface and rides a moving hull / moon / rock.
            // Skip a parent that is already hidden (kill parked the proxy this frame). Parenting
            // there deactivates the burst before it draws. Restart particles AFTER the reparent
            // so the time-0 burst emits at the final pose, not at the pool origin.
            if (attachParent != null && attachParent.gameObject.activeInHierarchy)
                go.transform.SetParent(attachParent, true);
            // [UNITY] PrepareVfxInstance restarts ParticleSystems — required after pool Return cleared them.
            // Cold Instantiates also pays FixAllIn1 / light strip here once (marker after).
            VfxUrpCompat.PrepareVfxInstance(go);
            if (webGlReplay)
                QueueDeferredAudioReplay(go);
            else if (replayAudio)
                ReplayAudioInHierarchy(go);
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
