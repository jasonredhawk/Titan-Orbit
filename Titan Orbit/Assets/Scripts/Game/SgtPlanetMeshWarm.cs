using System.Reflection;
using SpaceGraphicsToolkit;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Join-load helper for Space Graphics Toolkit planets.
    /// <para>
    /// [TITAN-ORBIT] Profiler (frame 495270) showed <c>SgtPlanet.LateUpdate</c> at ~845 ms
    /// on first <see cref="SgtPlanet.Rebuild"/> after spawn — vertex displace Instantiates a
    /// new mesh. Instantiates during loading does not always Rebuild (new component from
    /// <see cref="PlanetSpinVisualProxy"/> copy, or LateUpdate after Join Team). This helper
    /// asks whether a planet still needs Rebuild so warmup can pay that cost under the overlay,
    /// one planet per frame.
    /// </para>
    /// <c>generatedMesh</c> / <c>dirtyMesh</c> are private on the vendor type — we read them
    /// once via reflection. We do <b>not</b> call Rebuild on an already-built planet
    /// (that would Instantiates the mesh again).
    /// </summary>
    public static class SgtPlanetMeshWarm
    {
        static FieldInfo s_GeneratedMesh;
        static FieldInfo s_DirtyMesh;
        static bool s_Resolved;

        /// <summary>
        /// Caches the private field handles. Safe to call every probe — resolves once.
        /// </summary>
        static void EnsureFields()
        {
            if (s_Resolved)
                return;

            s_Resolved = true;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            s_GeneratedMesh = typeof(SgtPlanet).GetField("generatedMesh", flags);
            s_DirtyMesh = typeof(SgtPlanet).GetField("dirtyMesh", flags);
        }

        /// <summary>
        /// True when <see cref="SgtPlanet.LateUpdate"/> would call <see cref="SgtPlanet.Rebuild"/>
        /// this frame (no generated mesh yet, or marked dirty).
        /// </summary>
        /// <param name="planet">SGT planet on a hybrid proxy (or a child after spin migrate).</param>
        public static bool NeedsRebuild(SgtPlanet planet)
        {
            if (planet == null)
                return false;

            EnsureFields();
            if (s_GeneratedMesh == null)
                return true;

            var mesh = s_GeneratedMesh.GetValue(planet) as Mesh;
            bool dirty = s_DirtyMesh != null && (bool)s_DirtyMesh.GetValue(planet);
            return mesh == null || dirty;
        }
    }
}
