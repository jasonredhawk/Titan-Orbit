using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit
{
    /// <summary>
    /// Counts material instances created while firing and drawing text.
    /// Flushed by <c>WebGlHeapHeartbeat</c> — not logged per shot.
    /// </summary>
    public static class MaterialCloneProbe
    {
        public static int ApplyColorCalls;
        public static int ApplyColorClones;
        public static int ApplyColorRingClones;
        public static int ApplyColorTrailClones;
        public static int TrailReads;
        public static int TrailClones;
        public static int FixClones;
        public static int TracerShells;
        public static int OneShotShells;
        public static int PrepareCold;
        public static int PrepareWarm;
        public static int FontMaterialCalls;
        public static int FontMaterialClones;
        public static int StoreRefreshes;
        public static int StoreLabelSets;

        static readonly HashSet<int> s_seen = new HashSet<int>(256);

        /// <summary>
        /// Call with the shared material captured BEFORE <c>Renderer.materials</c>.
        /// A new instance id means that getter cloned.
        /// </summary>
        public static void NoteApplyColor(Material sharedBefore, Material after)
        {
            ApplyColorCalls++;
            NoteNamedClone(sharedBefore, after, ref ApplyColorClones, ref ApplyColorRingClones, ref ApplyColorTrailClones);
        }

        /// <summary>Call with the value returned by <c>ParticleSystemRenderer.trailMaterial</c>.</summary>
        public static void NoteTrailMaterial(Material trail)
        {
            TrailReads++;
            if (trail == null)
                return;
            if (!s_seen.Add(trail.GetInstanceID()))
                return;
            TrailClones++;
        }

        public static void NoteFixClone(Material created)
        {
            if (created == null)
                return;
            if (!s_seen.Add(created.GetInstanceID()))
                return;
            FixClones++;
        }

        /// <summary>
        /// <c>TMP_Text.fontMaterial</c> clones on first access. Count unique ids.
        /// </summary>
        public static void NoteFontMaterial(Material sharedBefore, Material after)
        {
            FontMaterialCalls++;
            if (after == null)
                return;
            int id = after.GetInstanceID();
            int sharedId = sharedBefore != null ? sharedBefore.GetInstanceID() : 0;
            if (id == sharedId)
                return;
            if (!s_seen.Add(id))
                return;
            FontMaterialClones++;
        }

        static void NoteNamedClone(Material sharedBefore, Material after, ref int clones, ref int ring, ref int trail)
        {
            if (after == null)
                return;
            int id = after.GetInstanceID();
            int sharedId = sharedBefore != null ? sharedBefore.GetInstanceID() : 0;
            if (id == sharedId)
                return;
            if (!s_seen.Add(id))
                return;
            clones++;
            string name = after.name ?? string.Empty;
            if (name.IndexOf("ringred", System.StringComparison.Ordinal) >= 0)
                ring++;
            else if (name.IndexOf("trailred", System.StringComparison.Ordinal) >= 0)
                trail++;
        }
    }
}
