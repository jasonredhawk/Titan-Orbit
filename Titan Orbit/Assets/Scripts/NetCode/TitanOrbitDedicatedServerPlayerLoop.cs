using System.Collections.Generic;
using System.Text;
using TitanOrbit.Diagnostics;
using UnityEngine;
using UnityEngine.LowLevel;
#if UNITY_SERVER && !UNITY_EDITOR
using UnityEngine.Rendering;
#endif

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Dedicated headless: drop NullGfx present/wait from the player loop.
    /// GCE 2026-08-30: vSync=0, targetFps=-1, App UI stripped, and the process still spent
    /// ~850 ms/frame (wallSim≈5 Hz, 100% of one core, empty lobby). WaitForTargetFPS sampler
    /// was 0 — the leftover is <c>PresentAndWait</c> on <c>-nographics</c> NullGfxDevice +
    /// SDL dummy. Editor Local Host has a real display and does not hit this path.
    /// </summary>
    public static class TitanOrbitDedicatedServerPlayerLoop
    {
        static readonly string[] StripNameParts =
        {
            "PresentAndWait",
            "WaitForTargetFPS",
            "PlayerEmitCanvasGeometry",
            "UpdateAllRenderers",
            "UpdateAllSkinnedMeshes",
            "DirectorRenderImage"
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void BeforeSceneLoad()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return;
            if (!TitanOrbitDedicatedServerAutoBoot.IsDedicatedServerProcess())
                return;

#if UNITY_SERVER && !UNITY_EDITOR
            OnDemandRendering.renderFrameInterval = 50;
            Apply();
#endif
        }

        static void Apply()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var leaves = new List<string>(64);
            CollectLeaves(loop.subSystemList, leaves, "");

            var removed = new List<string>(16);
            loop.subSystemList = Filter(loop.subSystemList, removed, parentIsPostLate: false);
            PlayerLoop.SetPlayerLoop(loop);

            DedicatedServerFileLog.Append("pace", "playerLoopLeaves=" + string.Join(",", leaves));
            DedicatedServerFileLog.Append(
                "pace",
                "playerLoop removed=" + (removed.Count == 0 ? "(none)" : string.Join(",", removed)));
        }

        static void CollectLeaves(PlayerLoopSystem[] list, List<string> leaves, string prefix)
        {
            if (list == null)
                return;
            for (int i = 0; i < list.Length; i++)
            {
                var sys = list[i];
                string name = sys.type != null ? sys.type.Name : "native";
                string path = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
                if (sys.subSystemList == null || sys.subSystemList.Length == 0)
                    leaves.Add(path);
                else
                    CollectLeaves(sys.subSystemList, leaves, path);
            }
        }

        static PlayerLoopSystem[] Filter(PlayerLoopSystem[] list, List<string> removed, bool parentIsPostLate)
        {
            if (list == null || list.Length == 0)
                return list;

            var next = new List<PlayerLoopSystem>(list.Length);
            for (int i = 0; i < list.Length; i++)
            {
                var sys = list[i];
                string name = sys.type != null ? sys.type.Name : "";
                bool isPostLate = name.IndexOf("PostLateUpdate", System.StringComparison.Ordinal) >= 0;
                // Native PostLateUpdate children have no managed Type (GCE 175835 missed PresentAndWait).
                if (ShouldStrip(name) || (parentIsPostLate && string.IsNullOrEmpty(name)))
                {
                    removed.Add(string.IsNullOrEmpty(name) ? "nativePostLate" : name);
                    continue;
                }

                if (sys.subSystemList != null && sys.subSystemList.Length > 0)
                    sys.subSystemList = Filter(sys.subSystemList, removed, isPostLate || parentIsPostLate);
                next.Add(sys);
            }

            return next.ToArray();
        }

        static bool ShouldStrip(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return false;
            for (int i = 0; i < StripNameParts.Length; i++)
            {
                if (typeName.IndexOf(StripNameParts[i], System.StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }
    }
}
