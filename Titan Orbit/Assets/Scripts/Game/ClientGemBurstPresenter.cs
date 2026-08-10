using TitanOrbit.ECS;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Former immediate local gem explosion VFX — <b>disabled by design</b>.
    /// <para>
    /// Gems are server-authoritative: pose, speed, and direction come from ghosted
    /// <see cref="LocalTransform"/> / <see cref="GemKinematics"/>. The client waits for gem
    /// ghost Instantiates, then <see cref="GemClientMotionApplier"/> presents that data.
    /// No client-side invent of gem shells before the server spawn arrives.
    /// </para>
    /// APIs remain as no-ops so older call sites and debug isolators compile safely.
    /// </summary>
    public sealed class ClientGemBurstPresenter : MonoBehaviour
    {
        static ClientGemBurstPresenter _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstance()
        {
            // Intentionally do not auto-create — presenter is disabled. Methods are static no-ops.
            _instance = FindAnyObjectByType<ClientGemBurstPresenter>();
        }

        void Awake() => _instance = this;

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        /// <summary>No-op — local burst VFX disabled (ghost gems only).</summary>
        public static void PlayBurst(float3 worldPosition, float remainingValue, uint seed)
        {
            // Disabled: see type summary. Parameters unused by design.
            _ = worldPosition;
            _ = remainingValue;
            _ = seed;
        }

        /// <summary>No-op — nothing to dismiss when local burst is off.</summary>
        public static int DismissBurstNear(Vector3 worldPosition, float radius = -1f)
        {
            _ = worldPosition;
            _ = radius;
            return 0;
        }

        /// <summary>Legacy alias for <see cref="DismissBurstNear"/>.</summary>
        public static int ReturnAllNear(Vector3 worldPosition, float radius) =>
            DismissBurstNear(worldPosition, radius);

        /// <summary>Always false — handoff removed.</summary>
        public static bool TryTakeNear(
            Vector3 worldPosition,
            byte preferredBurstIndex,
            bool preferBurstIndex,
            out GameObject go,
            out Vector3 velocity,
            out Vector3 angularVelocity)
        {
            _ = worldPosition;
            _ = preferredBurstIndex;
            _ = preferBurstIndex;
            go = null;
            velocity = Vector3.zero;
            angularVelocity = Vector3.zero;
            return false;
        }

        /// <summary>Always false — handoff removed.</summary>
        public static bool TryClaimNear(Vector3 worldPosition, out Vector3 velocity, out Vector3 angularVelocity)
        {
            _ = worldPosition;
            velocity = Vector3.zero;
            angularVelocity = Vector3.zero;
            return false;
        }

        /// <summary>Legacy wrapper.</summary>
        public static void ClaimNear(Vector3 worldPosition) =>
            TryClaimNear(worldPosition, out _, out _);
    }
}
