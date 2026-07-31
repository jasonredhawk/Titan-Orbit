using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using UnityEditor;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// When a <see cref="ShipFamilyDefinition"/> is edited in its inspector, refresh any
    /// <see cref="ShipFamilyStatsPreview"/> inspectors that are open and reference that asset
    /// (otherwise totals stay stale until the prefab is re-selected).
    /// </summary>
    internal static class ShipFamilyStatsPreviewLiveRefresh
    {
        private static readonly List<ShipFamilyStatsPreview> s_ActiveInspectors = new List<ShipFamilyStatsPreview>();

        internal static void RegisterInspectorTarget(ShipFamilyStatsPreview preview)
        {
            if (preview == null || s_ActiveInspectors.Contains(preview))
                return;
            s_ActiveInspectors.Add(preview);
        }

        internal static void UnregisterInspectorTarget(ShipFamilyStatsPreview preview)
        {
            if (preview == null)
                return;
            s_ActiveInspectors.Remove(preview);
        }

        internal static void OnShipFamilyDefinitionSerializedChanged(ShipFamilyDefinition def)
        {
            if (def == null)
                return;
            def.InvalidateComponentStatsLookup();
            for (int i = s_ActiveInspectors.Count - 1; i >= 0; i--)
            {
                var p = s_ActiveInspectors[i];
                if (p == null)
                {
                    s_ActiveInspectors.RemoveAt(i);
                    continue;
                }

                if (p.ShipFamily != def)
                    continue;
                p.RecalculateFromChildren();
            }
        }
    }
}
