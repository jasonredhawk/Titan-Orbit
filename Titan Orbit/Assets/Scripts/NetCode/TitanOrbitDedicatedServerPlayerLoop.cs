using System.Collections.Generic;
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
    /// Unity 6 names the stall <c>WaitForLastPresentationAndUpdateTime</c> (old docs said
    /// <c>PresentAndWait</c>). Docker 2026-08-31: that leaf was still in the loop, frames
    /// were ~300 ms, MaxSteps=4 → wallSim≈11 Hz, client ships snap back.
    /// <para>
    /// Do <b>not</b> strip present-wait before UGS lobby publish. The 12.39 image stripped
    /// it at BeforeSceneLoad and <c>EnsureGuestSessionForOnlineAsync</c> never completed —
    /// no lobby, Join Game empty. Render/UI leaves are safe at boot; present-wait is
    /// applied after the lobby is live. Do not strip <c>BatchModeUpdate</c> (pumps HTTP
    /// in <c>-batchmode</c>).
    /// </para>
    /// </summary>
    public static class TitanOrbitDedicatedServerPlayerLoop
    {
        static readonly string[] EarlyStripNameParts =
        {
            "PlayerEmitCanvasGeometry",
            "PlayerUpdateCanvases",
            "UpdateAllRenderers",
            "UpdateAllSkinnedMeshes",
            "DirectorRenderImage",
            "UIElementsRepaintPanels",
            "UIElementsUpdatePanels",
            "UIElementsRenderBatchModeOffscreen",
            "UpdateCustomRenderTextures",
            "UpdateLightProbeProxyVolumes",
            "EnlightenRuntimeUpdate",
            "UpdateVideoTextures",
            "UpdateVideo",
            "VFXUpdate",
            "ParticleSystemBeginUpdateAll",
            "ParticleSystemEndUpdateAll",
            "UpdateCameraMotionVectors",
            "ClearIntermediateRenderers",
            "RendererNotifyInvisible",
            "UpdateMainGameViewRect",
            "UpdateCanvasRectTransform",
            "PresentationSystemGroup"
        };

        static readonly string[] PresentWaitStripNameParts =
        {
            // Unity 6 TimeUpdate — this is the ~300 ms NullGfx wait (not "PresentAndWait").
            "WaitForLastPresentationAndUpdateTime",
            "PresentAndWait",
            "PresentBeforeUpdate",
            "PresentAfterDraw",
            "PlayerSendFramePostPresent",
            "ResetFrameStatsAfterPresent",
            "WaitForTargetFPS",
            "FinishFrameRendering",
            "GraphicsWarmupPreloadedShaders",
            "EndGraphicsJobsAfterScriptUpdate",
            "EndGraphicsJobsAfterScriptLateUpdate",
            "GpuTimestamp"
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void BeforeSceneLoad()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return;
            if (!TitanOrbitDedicatedServerAutoBoot.IsDedicatedServerProcess())
                return;

#if UNITY_SERVER && !UNITY_EDITOR
            OnDemandRendering.renderFrameInterval = 1000;
            Apply("beforeScene", includePresentWait: false);
#endif
        }

        /// <summary>
        /// Image 13.05 stripped present-wait after CreateLobby. UnityWebRequest heartbeats then
        /// hung, UGS expired the lobby (~30s), Join Game went empty, and wall frames stayed
        /// ~330 ms anyway. Do not strip <c>WaitForLastPresentationAndUpdateTime</c> until
        /// heartbeats still complete without it.
        /// </summary>
        public static void ApplyPresentWaitStripAfterLobby()
        {
            DedicatedServerFileLog.Append(
                "pace",
                "playerLoop afterLobby skipped present-wait strip (keeps UGS heartbeat / Join Game list)");
            Debug.Log("[TitanOrbitDedicatedServerPlayerLoop] afterLobby skipped present-wait strip " +
                      "(UGS heartbeat must keep running)");
        }

        static void Apply(string when, bool includePresentWait)
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var removed = new List<string>(32);
            loop.subSystemList = Filter(loop.subSystemList, removed, includePresentWait);
            PlayerLoop.SetPlayerLoop(loop);

            var remaining = new List<string>(64);
            CollectLeaves(loop.subSystemList, remaining, "");

            DedicatedServerFileLog.Append(
                "pace",
                "playerLoop " + when + " removed=" +
                (removed.Count == 0 ? "(none)" : string.Join(",", removed)));
            DedicatedServerFileLog.Append(
                "pace",
                "playerLoop " + when + " remaining=" + string.Join(",", remaining));
            Debug.Log("[TitanOrbitDedicatedServerPlayerLoop] " + when +
                      " presentWait=" + includePresentWait +
                      " removed=" + removed.Count);
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

        static PlayerLoopSystem[] Filter(PlayerLoopSystem[] list, List<string> removed, bool includePresentWait)
        {
            if (list == null || list.Length == 0)
                return list;

            var next = new List<PlayerLoopSystem>(list.Length);
            for (int i = 0; i < list.Length; i++)
            {
                var sys = list[i];
                string name = sys.type != null ? sys.type.Name : "";
                if (ShouldStrip(name, includePresentWait))
                {
                    removed.Add(name);
                    continue;
                }

                if (sys.subSystemList != null && sys.subSystemList.Length > 0)
                    sys.subSystemList = Filter(sys.subSystemList, removed, includePresentWait);
                next.Add(sys);
            }

            return next.ToArray();
        }

        static bool ShouldStrip(string typeName, bool includePresentWait)
        {
            if (string.IsNullOrEmpty(typeName))
                return false;
            if (MatchesAny(typeName, EarlyStripNameParts))
                return true;
            return includePresentWait && MatchesAny(typeName, PresentWaitStripNameParts);
        }

        static bool MatchesAny(string typeName, string[] parts)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (typeName.IndexOf(parts[i], System.StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }
    }
}
