using UnityEngine;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Looping burn impact VFX parented to a ship hull so fire moves with the target.
    /// Self-destructs when the parent is destroyed or the burn duration ends.
    /// </summary>
    public sealed class ShipBurnVfxAnchor : MonoBehaviour
    {
        private Transform attachTarget;
        private Vector3 localOffset;
        private float endTime;

        public static GameObject SpawnAttached(
            Transform attachParent,
            Vector3 localOffset,
            GameObject impactPrefab,
            float pitch,
            float scale,
            float durationSeconds)
        {
            if (attachParent == null || impactPrefab == null || durationSeconds <= 0f)
                return null;

            localOffset.y = 0f;
            GameObject go = Instantiate(impactPrefab, attachParent);
            go.transform.localPosition = localOffset;
            go.transform.localRotation = Quaternion.identity;
            VfxUrpCompat.ApplyImpactVisualScale(go, scale);

            BulletVisualFactory.SetAudioPitchInHierarchy(go, pitch);
            VfxUrpCompat.FixAllIn1MaterialsForUrp(go);
            BulletVisualFactory.ConfigureLoopingImpactParticles(go, durationSeconds, simulateInLocalSpace: true);
            VfxUrpCompat.PlayParticleSystemsInHierarchy(go);

            var anchor = go.AddComponent<ShipBurnVfxAnchor>();
            anchor.attachTarget = attachParent;
            anchor.localOffset = localOffset;
            anchor.endTime = Time.time + durationSeconds;
            return go;
        }

        public void ExtendDuration(float additionalSeconds)
        {
            if (additionalSeconds <= 0f) return;
            endTime = Mathf.Max(endTime, Time.time + additionalSeconds);
        }

        public void SetDurationFromNow(float durationSeconds)
        {
            if (durationSeconds <= 0f) return;
            endTime = Time.time + durationSeconds;
        }

        private void LateUpdate()
        {
            if (attachTarget == null)
            {
                Destroy(gameObject);
                return;
            }

            transform.localPosition = localOffset;
            if (Time.time >= endTime)
                Destroy(gameObject);
        }
    }
}
