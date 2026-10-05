using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Hides a destroyed asteroid proxy and hands the same GameObject back when that
    /// layout slot respawns. Position and size do not change, so the mesh is not rebuilt.
    /// </summary>
    public static class AsteroidVisualPool
    {
        static readonly Dictionary<int, GameObject> HiddenBySlot = new Dictionary<int, GameObject>();

        /// <summary>
        /// Returns the hidden proxy for <paramref name="slot"/> and activates it.
        /// </summary>
        public static bool TryTake(int slot, out GameObject go)
        {
            go = null;
            if (slot < 0)
                return false;
            if (!HiddenBySlot.TryGetValue(slot, out go) || go == null)
            {
                HiddenBySlot.Remove(slot);
                go = null;
                return false;
            }

            HiddenBySlot.Remove(slot);
            if (!go.activeSelf)
                go.SetActive(true);
            return true;
        }

        /// <summary>
        /// Deactivates <paramref name="go"/> and keeps it for the next respawn of <paramref name="slot"/>.
        /// A missing slot cannot be found later, so the caller should destroy that object.
        /// </summary>
        public static bool TryPark(int slot, GameObject go)
        {
            if (go == null || slot < 0)
                return false;

            if (HiddenBySlot.TryGetValue(slot, out GameObject existing) && existing != null && existing != go)
                Object.Destroy(existing);

            if (go.activeSelf)
                go.SetActive(false);
            HiddenBySlot[slot] = go;
            return true;
        }

        /// <summary>Destroys every parked proxy. Call when the visualizer is torn down.</summary>
        public static void DestroyParked()
        {
            foreach (KeyValuePair<int, GameObject> pair in HiddenBySlot)
            {
                if (pair.Value != null)
                    Object.Destroy(pair.Value);
            }

            HiddenBySlot.Clear();
        }
    }
}
