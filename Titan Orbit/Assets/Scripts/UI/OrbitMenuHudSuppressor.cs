using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>Hides gameplay HUD canvases while the moon orbit menu is open.</summary>
    public class OrbitMenuHudSuppressor : MonoBehaviour
    {
        struct HiddenUiState
        {
            public CanvasGroup Group;
            public bool AddedGroup;
            public float Alpha;
            public bool Interactable;
            public bool BlocksRaycasts;
        }

        readonly List<HiddenUiState> _hidden = new List<HiddenUiState>();
        bool _isHiding;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (FindFirstObjectByType<OrbitMenuHudSuppressor>() != null)
                return;

            var go = new GameObject(nameof(OrbitMenuHudSuppressor));
            DontDestroyOnLoad(go);
            go.AddComponent<OrbitMenuHudSuppressor>();
        }

        void LateUpdate()
        {
            // --- Toggle hide when orbit station opens/closes ---
            bool shouldHide = OrbitStationUI.Instance != null
                ? OrbitStationUI.Instance.IsMoonDockMenuOpen
                : MoonOrbitClientState.IsOrbitMenuVisible;
            if (shouldHide == _isHiding)
                return;

            _isHiding = shouldHide;
            if (shouldHide)
                HideGameplayHud();
            else
                RestoreGameplayHud();
        }

        void OnDisable() => RestoreGameplayHud();

        void HideGameplayHud()
        {
            // --- Alpha-zero all gameplay canvases except orbit station ---
            RestoreGameplayHud();

            Transform keepVisible = OrbitStationUI.Instance != null
                ? OrbitStationUI.Instance.transform
                : null;

            var canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < canvases.Length; i++)
            {
                var canvas = canvases[i];
                if (canvas == null)
                    continue;

                if (keepVisible != null && keepVisible.IsChildOf(canvas.transform))
                {
                    HideCanvasSiblingsExcept(canvas.transform, keepVisible, _hidden);
                    continue;
                }

                if (keepVisible != null && canvas.transform.IsChildOf(keepVisible))
                    continue;

                PushCanvasGroupHide(canvas.gameObject, _hidden);
            }
        }

        static void HideCanvasSiblingsExcept(Transform canvasTransform, Transform keepVisible, List<HiddenUiState> hidden)
        {
            for (int i = 0; i < canvasTransform.childCount; i++)
            {
                Transform child = canvasTransform.GetChild(i);
                if (child == keepVisible || child.IsChildOf(keepVisible))
                    continue;

                PushCanvasGroupHide(child.gameObject, hidden);
            }
        }

        static void PushCanvasGroupHide(GameObject root, List<HiddenUiState> hidden)
        {
            if (root == null)
                return;

            var group = root.GetComponent<CanvasGroup>();
            bool added = group == null;
            if (group == null)
                group = root.AddComponent<CanvasGroup>();

            hidden.Add(new HiddenUiState
            {
                Group = group,
                AddedGroup = added,
                Alpha = group.alpha,
                Interactable = group.interactable,
                BlocksRaycasts = group.blocksRaycasts,
            });

            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        void RestoreGameplayHud()
        {
            // --- Restore saved CanvasGroup state ---
            for (int i = 0; i < _hidden.Count; i++)
            {
                HiddenUiState state = _hidden[i];
                if (state.Group == null)
                    continue;

                state.Group.alpha = state.Alpha;
                state.Group.interactable = state.Interactable;
                state.Group.blocksRaycasts = state.BlocksRaycasts;
                if (state.AddedGroup)
                    Destroy(state.Group);
            }

            _hidden.Clear();
        }
    }
}
