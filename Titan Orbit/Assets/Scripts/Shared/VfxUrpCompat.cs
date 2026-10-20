using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Sci-Fi Arsenal / AllIn1 VFX often ship with GrabPass materials that do not render correctly on URP,
    /// especially on mobile (GLES/Vulkan). Do not add AllIn1 shaders to Project Settings → Graphics →
    /// Always Included Shaders — that can crash or destabilize Android/iOS builds; rely on fallbacks here instead.
    /// <para>
    /// [TITAN-ORBIT] Sci-Fi impact prefabs also carry Point Lights — a short intensity spike reads as a
    /// whole-scene flash. Lights are stripped on prepare; particles stay.
    /// </para>
    /// </summary>
    public static class VfxUrpCompat
    {
        private static Shader s_allIn1SrpBatch;
        private static Shader s_urpParticlesUnlit;
        private static Shader s_legacyParticlesUnlit;
        static Material s_UrpParticleTemplate;

        /// <summary>
        /// Resources material that already uses the additive transparent URP Particles/Unlit
        /// variant. Shader.Find alone is stripped from the WebGL player when no asset
        /// references that variant, so the retarget used to no-op and the built-in
        /// soft-particle materials stayed invisible.
        /// </summary>
        const string UrpParticleTemplateResource = "WebGlParticleUnlit";

        /// <summary>
        /// URP Particles/Unlit reads Position, Normal, Color, UV. Sci-Fi mesh flames and
        /// matrix shells ship custom streams (Position, Color, UV only). That mismatch
        /// puts the UV in the wrong interpolator, so the additive sprite stays invisible.
        /// </summary>
        static readonly List<ParticleSystemVertexStream> s_UrpParticleStreams = new List<ParticleSystemVertexStream>(4)
        {
            ParticleSystemVertexStream.Position,
            ParticleSystemVertexStream.Normal,
            ParticleSystemVertexStream.Color,
            ParticleSystemVertexStream.UV,
        };

        /// <summary>
        /// One URP stand-in per source material. WebGL clones are session-lived;
        /// thrusters and moon shields share the same Sci-Fi materials.
        /// </summary>
        static readonly Dictionary<int, Material> s_WebGlParticleMaterials = new Dictionary<int, Material>(16);

        /// <summary>
        /// Marks the inactive <c>BulletOneShotVfxPool</c> root so a shell parked there is not
        /// treated as a flash buried under a hidden ship or rock.
        /// </summary>
        public sealed class VfxPoolRootMarker : MonoBehaviour
        {
        }

        /// <summary>
        /// Marks a pooled muzzle/impact shell that already paid FixAllIn1 + light strip +
        /// Hierarchy scale setup. Session 74383c kill frames hit <c>hitMs:21</c> while the pool
        /// was still Instantiates-growing and re-running GrabPass material walks every Rent.
        /// </summary>
        sealed class VfxPreparedMarker : MonoBehaviour
        {
            /// <summary>True after particle systems were switched to Hierarchy scaling.</summary>
            public bool HierarchyScaleReady;

            /// <summary>Authored <see cref="TrailRenderer.widthMultiplier"/> captured on first scale.</summary>
            public float[] TrailBaseWidths;

            /// <summary>
            /// A kill hides the rock or hull this flash is parented to. That deactivates
            /// children, so the burst vanishes with the body. Queue a same-frame detach.
            /// Pool Return deactivates us while the parent is still active, and the pool
            /// root carries <see cref="VfxPoolRootMarker"/> — those must stay parked.
            /// </summary>
            void OnDisable()
            {
                Transform parent = transform.parent;
                if (parent == null || parent.GetComponent<VfxPoolRootMarker>() != null)
                    return;
                if (parent.gameObject.activeInHierarchy)
                    return;
                QueueImpactHiddenWithParent(gameObject);
            }
        }

        /// <summary>Flashes deactivated because their follow-parent was hidden this frame.</summary>
        static readonly List<GameObject> s_hiddenWithParent = new List<GameObject>(8);

        /// <summary>
        /// WebGL ignores particle Play on the same frame a shell is enabled. These
        /// wait one presentation frame, then emit if the burst is still empty.
        /// </summary>
        static readonly List<GameObject> s_webGlDeferredPlay = new List<GameObject>(32);

        static void QueueImpactHiddenWithParent(GameObject go)
        {
            if (go == null)
                return;
            for (int i = 0; i < s_hiddenWithParent.Count; i++)
            {
                if (s_hiddenWithParent[i] == go)
                    return;
            }

            s_hiddenWithParent.Add(go);
        }

        /// <summary>
        /// Detaches impacts whose ship / rock / moon was deactivated, then turns them
        /// back on at the world pose they already had. Call once per presentation frame
        /// after spawns and after proxy hides.
        /// </summary>
        public static void RescueImpactsHiddenWithParent()
        {
            for (int i = s_hiddenWithParent.Count - 1; i >= 0; i--)
            {
                GameObject go = s_hiddenWithParent[i];
                if (go == null)
                    continue;

                Transform parent = go.transform.parent;
                if (parent == null || parent.GetComponent<VfxPoolRootMarker>() != null)
                    continue;
                if (parent.gameObject.activeInHierarchy)
                    continue;

                go.transform.SetParent(null, true);
                if (!go.activeSelf)
                    go.SetActive(true);
            }

            s_hiddenWithParent.Clear();
        }

        /// <summary>
        /// Prefab-local scales, captured once at identity before any shot scale.
        /// A reused tracer restores these so one barrel cannot keep a scaled copy
        /// of the bolt. Particle modules stay as the projectile prefab authored them.
        /// </summary>
        public sealed class TracerPrefabScaleSnapshot : MonoBehaviour
        {
            public Transform[] Nodes;
            public Vector3[] LocalScales;
        }

        /// <summary>
        /// Swap GrabPass AllIn1 materials for SRP batch or particle unlit fallbacks; disable screen
        /// distortion and soft particles. Uses <c>sharedMaterials</c> only — never
        /// <c>Renderer.materials</c> (that clones every material per impact and GC-hitchs destroy frames).
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

        /// <summary>
        /// Starts every muzzle, trail, and impact particle system under
        /// <paramref name="root"/>, including systems nested several children deep.
        /// Call after the shell is at its final pose, while the shell is still inactive.
        /// <para>
        /// Recycling used to enable the shell, then <c>Stop</c> / <c>Simulate</c> /
        /// <c>Play</c> it in the same frame. One shot usually emits. A volley runs
        /// that restart on many systems before Unity's particle update, and those
        /// calls do not emit — so simultaneous fire goes blank while slow fire looks
        /// fine. This turns the shell on once, at the shot pose, and lets
        /// <c>playOnAwake</c> start each child. No <c>Simulate</c>.
        /// </para>
        /// </summary>
        public static void PlayParticleSystemsInHierarchy(GameObject root)
        {
            if (root == null)
                return;

            // Already active this frame (fallback shell). Do not toggle off/on —
            // that skips OnEnable for every system in the volley.
            if (root.activeSelf)
            {
                RestartAlreadyActiveRecursive(root.transform);
                return;
            }

            ArmInactiveShotVfxRecursive(root.transform);
            root.SetActive(true);
            EmitIfBurstMissedRecursive(root.transform, forceBody: false);
            QueueWebGlDeferredPlay(root);
        }

        /// <summary>
        /// Depth-first so nested Glow / Fire / LaserBolt children are armed, not only the root.
        /// </summary>
        static void ArmInactiveShotVfxRecursive(Transform node)
        {
            int childCount = node.childCount;
            for (int i = 0; i < childCount; i++)
                ArmInactiveShotVfxRecursive(node.GetChild(i));

            ParticleSystem ps = node.GetComponent<ParticleSystem>();
            if (ps != null)
            {
                ParticleSystem.MainModule main = ps.main;
                main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                main.stopAction = ParticleSystemStopAction.None;
                main.playOnAwake = true;
                ParticleSystem.EmissionModule emission = ps.emission;
                emission.enabled = true;
            }

            TrailRenderer trail = node.GetComponent<TrailRenderer>();
            if (trail == null)
                return;

            trail.Clear();
            trail.emitting = true;
            trail.enabled = true;
        }

        /// <summary>
        /// <c>playOnAwake</c> already ran on enable. If a one-shot still has no particles,
        /// emit its burst. <paramref name="forceBody"/> also emits a short body for a
        /// looping bolt whose rate has not ticked yet (WebGL enable frame).
        /// </summary>
        static void EmitIfBurstMissedRecursive(Transform node, bool forceBody)
        {
            int childCount = node.childCount;
            for (int i = 0; i < childCount; i++)
                EmitIfBurstMissedRecursive(node.GetChild(i), forceBody);

            ParticleSystem ps = node.GetComponent<ParticleSystem>();
            if (ps == null || !ps.gameObject.activeInHierarchy || ps.particleCount > 0)
                return;

            if (!ps.isPlaying)
                ps.Play(false);

            int burst = SumBurstCount(ps);
            if (burst > 0)
                ps.Emit(burst);
            else if (forceBody)
                ps.Emit(8);
        }

        /// <summary>
        /// Shell was already enabled (procedural fallback). Rewind each system in place.
        /// </summary>
        static void RestartAlreadyActiveRecursive(Transform node)
        {
            int childCount = node.childCount;
            for (int i = 0; i < childCount; i++)
                RestartAlreadyActiveRecursive(node.GetChild(i));

            TrailRenderer trail = node.GetComponent<TrailRenderer>();
            if (trail != null)
            {
                trail.Clear();
                trail.emitting = true;
                trail.enabled = true;
            }

            ParticleSystem ps = node.GetComponent<ParticleSystem>();
            if (ps == null || !ps.gameObject.activeInHierarchy)
                return;

            ParticleSystem.MainModule main = ps.main;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            main.stopAction = ParticleSystemStopAction.None;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;

            ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Clear(false);
            if (!ps.isPlaying)
                ps.Play(false);
            else
                ps.time = 0f;

            int burst = SumBurstCount(ps);
            if (burst > 0 && ps.particleCount == 0)
                ps.Emit(burst);

            QueueWebGlDeferredPlay(ps.gameObject);
        }

        /// <summary>
        /// WebGL often drops the burst that <c>playOnAwake</c> scheduled during enable.
        /// The next presentation frame emits it if the system is still empty.
        /// </summary>
        static void QueueWebGlDeferredPlay(GameObject root)
        {
            if (root == null || Application.platform != RuntimePlatform.WebGLPlayer)
                return;

            for (int i = 0; i < s_webGlDeferredPlay.Count; i++)
            {
                if (s_webGlDeferredPlay[i] == root)
                    return;
            }

            s_webGlDeferredPlay.Add(root);
        }

        /// <summary>
        /// Call once per presentation frame before new shots spawn. Replays any muzzle,
        /// trail, or impact whose enable-frame burst WebGL swallowed.
        /// </summary>
        public static void TickWebGlDeferredShotPlay()
        {
            int count = s_webGlDeferredPlay.Count;
            if (count == 0)
                return;

            for (int i = 0; i < count; i++)
            {
                GameObject go = s_webGlDeferredPlay[i];
                if (go == null || !go.activeInHierarchy)
                    continue;
                EmitIfBurstMissedRecursive(go.transform, forceBody: true);
            }

            s_webGlDeferredPlay.Clear();
        }

        /// <summary>
        /// Stops every particle system, then sets <c>main.loop</c>. Must stop first — Unity
        /// warns if loop/duration change while playing. Used so burn / shock impact VFX
        /// persist for the status duration, then restore one-shot before pool Return.
        /// </summary>
        public static void SetParticleSystemsLooping(GameObject root, bool looping)
        {
            if (root == null)
                return;

            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null)
                    continue;

                if (ps.isPlaying)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                var main = ps.main;
                main.loop = looping;
                main.playOnAwake = false;
                if (looping)
                    ps.Play(true);
            }
        }

        /// <summary>
        /// Restarts a one-shot impact from time 0 so burn ticks show the explosion again.
        /// Sci-Fi impacts burst once, then sit out a multi-second duration. Forcing
        /// <c>main.loop</c> stays dark between ticks, and <c>Emit</c> alone does not rewind
        /// lifetime curves. Stop, clear, then play — one rented shell, no new Instantiate.
        /// </summary>
        public static void ReplayImpactAnimation(GameObject root)
        {
            if (root == null)
                return;

            if (!root.activeInHierarchy)
            {
                PrepareVfxInstance(root);
                return;
            }

            ReplayImpactAnimationRecursive(root.transform);
        }

        static void ReplayImpactAnimationRecursive(Transform node)
        {
            int childCount = node.childCount;
            for (int i = 0; i < childCount; i++)
                ReplayImpactAnimationRecursive(node.GetChild(i));

            TrailRenderer trail = node.GetComponent<TrailRenderer>();
            if (trail != null)
            {
                trail.Clear();
                trail.emitting = true;
                trail.enabled = true;
            }

            Animation legacy = node.GetComponent<Animation>();
            if (legacy != null)
            {
                legacy.Rewind();
                legacy.Play();
            }

            Animator animator = node.GetComponent<Animator>();
            if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
            {
                animator.Rebind();
                animator.Update(0f);
            }

            ParticleSystem ps = node.GetComponent<ParticleSystem>();
            if (ps == null || !ps.gameObject.activeInHierarchy)
                return;

            // Stop before loop changes — Unity warns if loop is edited while playing.
            ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Clear(false);

            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            main.stopAction = ParticleSystemStopAction.None;
            main.playOnAwake = true;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;

            ps.time = 0f;
            ps.Play(false);
            // A warm system can skip its time-0 burst on Play. Emit it if nothing spawned.
            if (ps.particleCount == 0)
            {
                int burst = SumBurstCount(ps);
                if (burst > 0)
                    ps.Emit(burst);
            }
        }

        /// <summary>
        /// Fires each system's authored time-0 burst again and keeps particles already in flight.
        /// Sci-Fi impacts emit once, then sit out a multi-second duration, so setting
        /// <c>main.loop</c> stays dark between burn ticks. <c>Emit</c> does not clear the system.
        /// Prefer <see cref="ReplayImpactAnimation"/> when the whole explosion should play again.
        /// </summary>
        public static void ReplayParticleBursts(GameObject root)
        {
            if (root == null)
                return;

            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null || !ps.gameObject.activeInHierarchy)
                    continue;

                if (!ps.isPlaying)
                    ps.Play(false);

                int count = SumBurstCount(ps);
                if (count > 0)
                    ps.Emit(count);
            }
        }

        /// <summary>Authored burst size (constant or the midpoint of a two-constant range).</summary>
        static int SumBurstCount(ParticleSystem ps)
        {
            var emission = ps.emission;
            if (!emission.enabled || emission.burstCount <= 0)
                return 0;

            int total = 0;
            int bursts = emission.burstCount;
            for (int i = 0; i < bursts; i++)
            {
                ParticleSystem.MinMaxCurve count = emission.GetBurst(i).count;
                float n = count.mode == ParticleSystemCurveMode.TwoConstants
                    ? (count.constantMin + count.constantMax) * 0.5f
                    : count.constant;
                if (n > 0.5f)
                    total += Mathf.RoundToInt(n);
            }

            return total;
        }

        /// <summary>
        /// One-time material/light fix on first use, then restart particles every Rent.
        /// Pooled shells keep the marker so destroy frames skip GrabPass walks.
        /// Pass <paramref name="playParticles"/> false for thruster jets — those are
        /// driven by the thrust button, and Play-on-prepare left foreign JetFlames
        /// visible while idle (looked inverted).
        /// </summary>
        public static void PrepareVfxInstance(GameObject root, bool playParticles = true)
        {
            if (root == null)
                return;

            // --- Cold prepare once per pooled shell ---
            var marker = root.GetComponent<VfxPreparedMarker>();
            if (marker == null)
            {
                FixAllIn1MaterialsForUrp(root);
                RetargetBuiltInParticlesForWebGl(root);
                StripSceneFlashLights(root);
                marker = root.AddComponent<VfxPreparedMarker>();
            }

            if (playParticles)
                PlayParticleSystemsInHierarchy(root);
        }

        /// <summary>
        /// Sci-Fi Arsenal flames and matrix shields use built-in <c>Particles/Standard Unlit</c>
        /// with soft-particle fading. That shader has no Universal Forward pass, and WebGL's
        /// mobile URP asset has no camera depth texture, so the mesh flame and the shield
        /// shell either never draw or multiply to zero alpha. Swap those materials to the
        /// Resources URP Particles/Unlit additive template (so the player build keeps the
        /// transparent variant) and align vertex streams with that shader.
        /// </summary>
        /// <param name="root">Spawned jet, shield, or other Sci-Fi VFX instance.</param>
        public static void RetargetBuiltInParticlesForWebGl(GameObject root)
        {
            if (root == null || !ShouldRetargetBuiltInParticles())
                return;
            if (!EnsureUrpParticleUnlit())
                return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null)
                    continue;

                bool changed = RetargetSharedMaterials(renderer);
                if (renderer is ParticleSystemRenderer particles)
                {
                    Material trail = particles.trailMaterial;
                    if (trail != null && NeedsWebGlParticleRetarget(trail))
                    {
                        particles.trailMaterial = GetOrCreateUrpParticleMaterial(trail);
                        changed = true;
                    }

                    if (changed)
                        particles.SetActiveVertexStreams(s_UrpParticleStreams);
                }
            }
        }

        /// <summary>
        /// URP projects cannot draw built-in particle shaders. WebGL always retargets;
        /// any other player does too while a scriptable render pipeline is active.
        /// </summary>
        static bool ShouldRetargetBuiltInParticles()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return true;
            return GraphicsSettings.currentRenderPipeline != null;
        }

        /// <summary>
        /// Prefer the Resources template so the additive transparent variant is in the build.
        /// Shader.Find is the editor fallback before that asset has imported.
        /// </summary>
        static bool EnsureUrpParticleUnlit()
        {
            if (s_urpParticlesUnlit != null)
                return true;

            if (s_UrpParticleTemplate == null)
                s_UrpParticleTemplate = Resources.Load<Material>(UrpParticleTemplateResource);
            if (s_UrpParticleTemplate != null && s_UrpParticleTemplate.shader != null)
                s_urpParticlesUnlit = s_UrpParticleTemplate.shader;

            if (s_urpParticlesUnlit == null)
                s_urpParticlesUnlit = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            return s_urpParticlesUnlit != null;
        }

        static bool RetargetSharedMaterials(Renderer renderer)
        {
            Material[] shared = renderer.sharedMaterials;
            if (shared == null || shared.Length == 0)
                return false;

            bool changed = false;
            for (int i = 0; i < shared.Length; i++)
            {
                Material src = shared[i];
                if (src == null || src.shader == null)
                    continue;
                if (!NeedsWebGlParticleRetarget(src))
                    continue;

                shared[i] = GetOrCreateUrpParticleMaterial(src);
                changed = true;
            }

            if (changed)
                renderer.sharedMaterials = shared;
            return changed;
        }

        static Material GetOrCreateUrpParticleMaterial(Material src)
        {
            int id = src.GetInstanceID();
            if (!s_WebGlParticleMaterials.TryGetValue(id, out Material edit) || edit == null)
            {
                edit = CreateWebGlParticleMaterial(src, s_urpParticlesUnlit);
                s_WebGlParticleMaterials[id] = edit;
            }

            return edit;
        }

        /// <summary>True for built-in / missing particle shaders. URP particle shaders stay.</summary>
        static bool NeedsWebGlParticleRetarget(Material mat)
        {
            string name = mat.shader.name;
            if (name.IndexOf("Universal Render Pipeline/Particles", StringComparison.Ordinal) >= 0)
                return false;
            if (!mat.shader.isSupported)
                return true;
            if (name.StartsWith("Particles/", StringComparison.Ordinal))
                return true;
            if (name.StartsWith("Legacy Shaders/Particles", StringComparison.Ordinal))
                return true;
            if (name.StartsWith("Mobile/Particles", StringComparison.Ordinal))
                return true;
            return name == "Hidden/InternalErrorShader";
        }

        /// <summary>
        /// Additive transparent stand-in. Copies <c>_MainTex</c> onto <c>_BaseMap</c>.
        /// Legacy Particles/Additive doubles <c>_TintColor</c> in the shader; Standard Unlit
        /// already stores the multiply color in <c>_Color</c>.
        /// </summary>
        static Material CreateWebGlParticleMaterial(Material src, Shader urp)
        {
            // Clone the included template so keywords (_SURFACE_TYPE_TRANSPARENT) survive
            // player shader stripping. A fresh Material(shader) plus runtime keywords does not.
            Material edit = s_UrpParticleTemplate != null
                ? new Material(s_UrpParticleTemplate)
                : new Material(urp);
            edit.name = src.name + "_WebGL";

            Texture tex = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
            if (tex == null)
                tex = src.mainTexture;
            if (tex != null)
            {
                edit.SetTexture("_BaseMap", tex);
                if (src.HasProperty("_MainTex"))
                {
                    edit.SetTextureScale("_BaseMap", src.GetTextureScale("_MainTex"));
                    edit.SetTextureOffset("_BaseMap", src.GetTextureOffset("_MainTex"));
                }
            }

            Color tint = Color.white;
            string shaderName = src.shader != null ? src.shader.name : string.Empty;
            bool legacyAdditive = shaderName.IndexOf("Additive", StringComparison.Ordinal) >= 0
                && shaderName.IndexOf("Standard", StringComparison.Ordinal) < 0;
            if (legacyAdditive && src.HasProperty("_TintColor"))
            {
                Color doubled = src.GetColor("_TintColor") * 2f;
                doubled.a = Mathf.Clamp01(src.GetColor("_TintColor").a * 2f);
                tint = doubled;
            }
            else if (src.HasProperty("_Color"))
            {
                tint = src.GetColor("_Color");
            }

            edit.SetColor("_BaseColor", tint);
            if (edit.HasProperty("_Color"))
                edit.SetColor("_Color", tint);

            edit.SetFloat("_Surface", 1f);
            edit.SetFloat("_Blend", 2f);
            edit.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            edit.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            edit.DisableKeyword("_ALPHAMODULATE_ON");
            edit.DisableKeyword("_ALPHATEST_ON");
            edit.SetOverrideTag("RenderType", "Transparent");
            edit.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            edit.SetFloat("_DstBlend", (float)BlendMode.One);
            edit.SetFloat("_SrcBlendAlpha", (float)BlendMode.SrcAlpha);
            edit.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
            edit.SetFloat("_ZWrite", 0f);
            edit.SetFloat("_AlphaClip", 0f);
            edit.SetFloat("_Cull", src.HasProperty("_Cull") ? src.GetFloat("_Cull") : 0f);
            edit.SetFloat("_ColorMode", 0f);
            edit.SetFloat("_SoftParticlesEnabled", 0f);
            edit.DisableKeyword("_SOFTPARTICLES_ON");
            edit.DisableKeyword("_FADING_ON");
            edit.SetFloat("_CameraFadingEnabled", 0f);
            edit.DisableKeyword("_DISTORTION_ON");
            edit.renderQueue = (int)RenderQueue.Transparent;
            return edit;
        }

        /// <summary>
        /// Disables Point/Spot lights on Sci-Fi VFX instances. One bright light on asteroid kill
        /// flashes the whole URP scene even when the camera and toroidal tiles are stable.
        /// Particles / meshes stay — only the light components are turned off.
        /// </summary>
        /// <param name="root">Impact or muzzle instance.</param>
        public static void StripSceneFlashLights(GameObject root)
        {
            if (root == null)
                return;

            // Sci-Fi Arsenal fade/flicker keep running after the Light is destroyed.
            // Flicker threw MissingComponentException; Fade called GetComponent every
            // Update on every live tracer/muzzle/impact (profiler: ~10 KB GC while firing).
            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null)
                    continue;
                string typeName = behaviour.GetType().Name;
                if (typeName == "SciFiLightFlicker" || typeName == "SciFiLightFade")
                {
                    behaviour.enabled = false;
                    UnityEngine.Object.Destroy(behaviour);
                }
            }

            Light[] lights = root.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light == null)
                    continue;
                light.enabled = false;
                light.intensity = 0f;
                // Destroy so nothing can re-enable the light next frame.
                UnityEngine.Object.Destroy(light);
            }
        }

        /// <summary>
        /// Scales muzzle / impact / tracer VFX via root transform. On first call, switches
        /// particle systems to Hierarchy scaling (and World → Local) so later Rents only set
        /// <c>localScale</c> (no module *= drift, no GetComponentsInChildren every kill).
        /// World-space particles ignore transform scale — drone 0.58× flashes stayed ship-sized.
        /// One-shot flashes do not move, so Local matches the authored look and then shrinks.
        /// </summary>
        /// <summary>
        /// Records prefab-local scales while the shell is still at identity.
        /// Call once, before <see cref="ApplyImpactVisualScale"/>.
        /// </summary>
        public static void CaptureTracerPrefabScale(GameObject root)
        {
            if (root == null)
                return;

            var snap = root.GetComponent<TracerPrefabScaleSnapshot>();
            if (snap != null && snap.Nodes != null && snap.Nodes.Length > 0)
                return;

            if (snap == null)
                snap = root.AddComponent<TracerPrefabScaleSnapshot>();

            Transform[] nodes = root.GetComponentsInChildren<Transform>(true);
            snap.Nodes = nodes;
            snap.LocalScales = new Vector3[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
                snap.LocalScales[i] = nodes[i] != null ? nodes[i].localScale : Vector3.one;
        }

        /// <summary>
        /// Puts a rented tracer back on the prefab-local scales captured by
        /// <see cref="CaptureTracerPrefabScale"/> before the shot's root scale is applied.
        /// </summary>
        public static void RestoreTracerPrefabScale(GameObject root)
        {
            if (root == null)
                return;

            var snap = root.GetComponent<TracerPrefabScaleSnapshot>();
            if (snap == null || snap.Nodes == null || snap.Nodes.Length == 0)
            {
                Vector3 rootScale = root.transform.localScale;
                bool unit = rootScale.x > 0.9f && rootScale.x < 1.1f
                    && rootScale.y > 0.9f && rootScale.y < 1.1f
                    && rootScale.z > 0.9f && rootScale.z < 1.1f;
                if (!unit)
                    return;
                CaptureTracerPrefabScale(root);
                snap = root.GetComponent<TracerPrefabScaleSnapshot>();
            }

            if (snap == null || snap.Nodes == null)
                return;

            int nodes = snap.Nodes.Length;
            for (int i = 0; i < nodes; i++)
            {
                if (snap.Nodes[i] != null && snap.LocalScales != null && i < snap.LocalScales.Length)
                    snap.Nodes[i].localScale = snap.LocalScales[i];
            }
        }

        /// <param name="lockUniformParticleSize">
        /// Tracers pass true: fire-power is root scale only. Projectile particle
        /// modules stay as the Sci-Fi Arsenal prefab authored them (BulletRed's
        /// glow speed, stretch length, sizes). Muzzle and impact pass false and
        /// switch to Local so a one-shot flash still follows transform scale.
        /// </param>
        public static void ApplyImpactVisualScale(GameObject root, float scale, bool lockUniformParticleSize = false)
        {
            if (root == null)
                return;

            float s = Mathf.Max(0.05f, scale);
            // Tracers: undo a previous shot's root shrink before this shot's scale.
            if (lockUniformParticleSize)
                RestoreTracerPrefabScale(root);

            root.transform.localScale = Vector3.one * s;

            var marker = root.GetComponent<VfxPreparedMarker>();
            if (!lockUniformParticleSize)
            {
                ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
                for (int i = 0; i < systems.Length; i++)
                {
                    if (systems[i] == null)
                        continue;
                    var main = systems[i].main;
                    // World-space particles ignore transform scale (drone tracers stayed ship-sized).
                    main.simulationSpace = ParticleSystemSimulationSpace.Local;
                    main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                }
            }

            // Trail width is world units — parent scale does not shrink the ribbon.
            TrailRenderer[] trails = root.GetComponentsInChildren<TrailRenderer>(true);
            if (marker == null)
                marker = root.AddComponent<VfxPreparedMarker>();
            if (marker.TrailBaseWidths == null || marker.TrailBaseWidths.Length != trails.Length)
            {
                marker.TrailBaseWidths = new float[trails.Length];
                for (int i = 0; i < trails.Length; i++)
                    marker.TrailBaseWidths[i] = trails[i] != null ? trails[i].widthMultiplier : 1f;
            }

            for (int i = 0; i < trails.Length; i++)
            {
                if (trails[i] != null)
                    trails[i].widthMultiplier = Mathf.Max(0.001f, marker.TrailBaseWidths[i] * s);
            }

            marker.HierarchyScaleReady = true;
        }

        /// <summary>
        /// Bumped when tracer shells must be rebuilt from the projectile prefab.
        /// Rev 1: start size and speed had been rewritten (BulletRed glow became a disc).
        /// Rev 2: team-colour restamp overwrote authored particle and material colours.
        /// </summary>
        const int AuthoredProjectileRevision = 2;

        sealed class ProjectileVisualRevision : MonoBehaviour
        {
            public int Revision;
        }

        /// <summary>Marks a shell whose visual child is still a straight prefab instance.</summary>
        public static void StampAuthoredProjectile(GameObject root)
        {
            if (root == null)
                return;
            var rev = root.GetComponent<ProjectileVisualRevision>();
            if (rev == null)
                rev = root.AddComponent<ProjectileVisualRevision>();
            rev.Revision = AuthoredProjectileRevision;
        }

        /// <summary>
        /// Replaces a pooled projectile visual that was mutated away from
        /// <paramref name="projectilePrefab"/>. One rebuild per shell.
        /// </summary>
        public static void EnsureAuthoredProjectileVisual(GameObject root, GameObject projectilePrefab)
        {
            if (root == null || projectilePrefab == null)
                return;

            var rev = root.GetComponent<ProjectileVisualRevision>();
            if (rev != null && rev.Revision == AuthoredProjectileRevision)
                return;

            for (int i = root.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(root.transform.GetChild(i).gameObject);

            var snap = root.GetComponent<TracerPrefabScaleSnapshot>();
            if (snap != null)
                UnityEngine.Object.DestroyImmediate(snap);

            GameObject visual = UnityEngine.Object.Instantiate(projectilePrefab, root.transform);
            visual.name = "ProjectileVisual";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            FixAllIn1MaterialsForUrp(visual);
            CaptureTracerPrefabScale(root);

            var marker = root.GetComponent<VfxPreparedMarker>();
            if (marker != null)
            {
                marker.TrailBaseWidths = null;
                marker.HierarchyScaleReady = false;
            }

            StampAuthoredProjectile(root);
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
            var ps = CreateStoppedParticleSystem(go);
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
            var ps = CreateStoppedParticleSystem(go);
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

        /// <summary>
        /// <see cref="ParticleSystem"/> play-on-awake starts as soon as the component is added.
        /// Duration / lifetime writes then log "Setting the duration while system is still playing".
        /// Stop and clear first (Fireballs and other banks without a muzzle prefab hit this path).
        /// </summary>
        static ParticleSystem CreateStoppedParticleSystem(GameObject go)
        {
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            return ps;
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
