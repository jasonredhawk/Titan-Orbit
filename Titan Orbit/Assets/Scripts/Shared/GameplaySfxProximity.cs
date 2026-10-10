using TitanOrbit.Generation;
using TitanOrbit.Shared;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Audio
{
    /// <summary>
    /// Loudness for world SFX around the local ship. The map is a torus, and the
    /// shared <see cref="AudioManager"/> sources are not placed in the world, so
    /// Unity spatial blend cannot hear "nearby" correctly.
    /// Full volume inside <see cref="FullVolumeRange"/>, then a linear falloff to
    /// silence at <see cref="HearRange"/> (same distances as gem-deposit beats and
    /// remote hull-collision one-shots). Your own guns sit on the ship, so they
    /// stay full volume. A fight on the other side of the map stays silent.
    /// </summary>
    public static class GameplaySfxProximity
    {
        /// <summary>Toroidal XZ distance that still plays at full volume.</summary>
        public const float FullVolumeRange = 18f;

        /// <summary>Beyond this toroidal XZ distance the event is silent.</summary>
        public const float HearRange = 48f;

        /// <summary>
        /// 1 next to the local ship, 0 at and beyond <see cref="HearRange"/>.
        /// Returns 1 when the ship pose or map size is not ready yet so boot
        /// frames are not muted before a listener exists.
        /// Vector3 only — a public <c>float3</c> overload forces every caller,
        /// including assemblies that do not reference Mathematics, to take that dependency.
        /// </summary>
        public static float VolumeAt(Vector3 worldPosition) =>
            VolumeAt(new float3(worldPosition.x, worldPosition.y, worldPosition.z));

        static float VolumeAt(float3 worldPosition)
        {
            if (!TryGetListener(out float3 listener))
                return 1f;
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return 1f;

            float dist = ToroidalMapEcs.ToroidalDistance(listener, worldPosition, mapW, mapH);
            if (dist >= HearRange)
                return 0f;
            if (dist <= FullVolumeRange)
                return 1f;
            return 1f - (dist - FullVolumeRange) / (HearRange - FullVolumeRange);
        }

        /// <summary>
        /// Scales every <see cref="AudioSource"/> on a pooled muzzle, tracer, or
        /// impact. Authored volume is captured once so a far mute does not stick
        /// when the shell is reused next to the player.
        /// </summary>
        public static void Apply(GameObject root, Vector3 worldPosition)
        {
            if (root == null)
                return;

            float hear = VolumeAt(worldPosition);
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
                Apply(sources[i], hear);
        }

        /// <summary>
        /// Sets <paramref name="source"/> to its authored volume times <paramref name="hear"/>.
        /// A hear of 0 mutes and stops so PlayOnAwake on a distant rent does not leak.
        /// </summary>
        public static void Apply(AudioSource source, float hear)
        {
            if (source == null)
                return;

            SfxAuthoredVolume authored = source.GetComponent<SfxAuthoredVolume>();
            if (authored == null)
            {
                authored = source.gameObject.AddComponent<SfxAuthoredVolume>();
                authored.BaseVolume = source.volume;
            }

            if (hear <= 0.001f)
            {
                source.volume = 0f;
                source.mute = true;
                if (source.isPlaying)
                    source.Stop();
                return;
            }

            source.mute = false;
            source.volume = authored.BaseVolume * hear;
        }

        /// <summary>
        /// Local ship presentation pose, then the gameplay camera XZ when the ship
        /// is gone (death cinematic). Y is ignored — hear range is on the torus.
        /// </summary>
        static bool TryGetListener(out float3 listener)
        {
            if (ShipDisplayPose.HasLocalPose)
            {
                Vector3 p = ShipDisplayPose.LocalPosition;
                listener = new float3(p.x, 0f, p.z);
                return true;
            }

            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 p = cam.transform.position;
                listener = new float3(p.x, 0f, p.z);
                return true;
            }

            listener = default;
            return false;
        }
    }

    /// <summary>Remembers a pooled source's prefab volume across hear-range mutes.</summary>
    [DisallowMultipleComponent]
    sealed class SfxAuthoredVolume : MonoBehaviour
    {
        public float BaseVolume = 1f;
    }
}
