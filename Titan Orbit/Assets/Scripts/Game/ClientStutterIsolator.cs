using TitanOrbit;
using TitanOrbit.NetCode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [EDITOR/DEBUG] Optional on-screen toggles to bisect destroy stutter vs phantom collision.
    /// <para>
    /// Disabled by default. Enable <b>Debug — Stutter Isolator → Enable Isolator</b> on
    /// <see cref="Core.GameManager"/> (NceGameRoot Inspector), then use Shift+F1–F7 in Play Mode.
    /// Hotkeys flip <see cref="TitanOrbitDebugFlags"/> isolation bits used by VFX / floats /
    /// toroidal collision / ship soft-track / gem burst.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(70100)]
    public sealed class ClientStutterIsolator : MonoBehaviour
    {
        bool _showPanel = true;

        /// <summary>[UNITY] Auto-install after scene load (client only; stays idle until enabled).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
            if (FindAnyObjectByType<ClientStutterIsolator>() != null)
                return;

            var session = FindAnyObjectByType<TitanOrbitSessionManager>();
            if (session != null)
            {
                session.gameObject.AddComponent<ClientStutterIsolator>();
                return;
            }

            var go = new GameObject("ClientStutterIsolator");
            DontDestroyOnLoad(go);
            go.AddComponent<ClientStutterIsolator>();
        }

        void Update()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            // --- Master switch from GameManager Inspector ---
            if (!TitanOrbitDebugFlags.StutterIsolatorEnabled)
                return;

            // --- Hotkeys (hold LeftShift + F#) via Input System package ---
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (!keyboard.leftShiftKey.isPressed && !keyboard.rightShiftKey.isPressed)
                return;

            if (keyboard.f1Key.wasPressedThisFrame)
                TitanOrbitDebugFlags.IsolateDisableImpactVfx =
                    !TitanOrbitDebugFlags.IsolateDisableImpactVfx;
            if (keyboard.f2Key.wasPressedThisFrame)
                TitanOrbitDebugFlags.IsolateDisableFloatingCounts =
                    !TitanOrbitDebugFlags.IsolateDisableFloatingCounts;
            if (keyboard.f3Key.wasPressedThisFrame)
                TitanOrbitDebugFlags.IsolateDisableAsteroidShipCollision =
                    !TitanOrbitDebugFlags.IsolateDisableAsteroidShipCollision;
            if (keyboard.f4Key.wasPressedThisFrame)
                TitanOrbitDebugFlags.IsolateDisableShipSoftTrack =
                    !TitanOrbitDebugFlags.IsolateDisableShipSoftTrack;
            if (keyboard.f5Key.wasPressedThisFrame)
                TitanOrbitDebugFlags.IsolateDisableGemBurst =
                    !TitanOrbitDebugFlags.IsolateDisableGemBurst;
            if (keyboard.f7Key.wasPressedThisFrame)
                _showPanel = !_showPanel;
        }

        void OnGUI()
        {
            if (!_showPanel ||
                !TitanOrbitDebugFlags.StutterIsolatorEnabled ||
                !TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            const float w = 440f;
            const float h = 200f;
            GUILayout.BeginArea(new Rect(12f, 12f, w, h), GUI.skin.box);
            GUILayout.Label("Destroy stutter isolator (Shift+F1..F5, F7 hide)");
            DrawFlag("F1 Impact VFX OFF", TitanOrbitDebugFlags.IsolateDisableImpactVfx);
            DrawFlag("F2 Floating counts OFF", TitanOrbitDebugFlags.IsolateDisableFloatingCounts);
            DrawFlag("F3 Asteroid toroidal collision OFF",
                TitanOrbitDebugFlags.IsolateDisableAsteroidShipCollision);
            DrawFlag("F4 Ship soft-track OFF (raw pose)", TitanOrbitDebugFlags.IsolateDisableShipSoftTrack);
            DrawFlag("F5 Gem burst OFF", TitanOrbitDebugFlags.IsolateDisableGemBurst);
            GUILayout.Label("Disable master switch on GameManager to turn this off.");
            GUILayout.EndArea();
        }

        /// <summary>One line for the OnGUI panel.</summary>
        static void DrawFlag(string label, bool on)
        {
            GUILayout.Label((on ? "[ON]  " : "[off] ") + label);
        }
    }
}
