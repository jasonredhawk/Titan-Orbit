using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace TitanOrbit.UI
{
    /// <summary>
    /// Invisible padded hit target over a colourful power bar. One shared
    /// <see cref="Probe"/> reads the pointer while the Orbit Menu is open and maps
    /// it to a slot — we do not trust EventSystem enter/exit here.
    /// <para>
    /// [TITAN-ORBIT] IPointerEnter on this pad used to open
    /// <see cref="ShipPowerBarStatTooltip"/>. A nested tip canvas (or any later
    /// sibling over the pointer) made EventSystem fire Exit on the same hover,
    /// so the STAT TELEMETRY card vanished. LateUpdate containment against the
    /// dark tray ignores that hole. Among overlapping live bars we keep the
    /// closest visible tray so a stale full-card rect cannot steal the hover.
    /// </para>
    /// Click / drag still forward to the card Button / ScrollRect when this pad
    /// has raycastTarget, so purchase and list scroll keep working.
    /// Presentation-only — no ECS writes. Paired with <see cref="ShipUpgradeTreePowerBarUI"/>.
    /// </summary>
    public class ShipPowerBarStatHoverRelay : MonoBehaviour,
        IPointerClickHandler,
        IInitializePotentialDragHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        /// <summary>Live pads. The probe walks this once per frame — not 24 LateUpdates.</summary>
        static readonly List<ShipPowerBarStatHoverRelay> s_Live = new List<ShipPowerBarStatHoverRelay>(32);

        /// <summary>One hidden runner. Created on first enable; destroyed with play mode.</summary>
        static Probe s_Probe;

        /// <summary>Bar that owns the ten slot rects and the last painted breakdown.</summary>
        public ShipUpgradeTreePowerBarUI Owner;

        int _hoverSlot = -1;

        void OnEnable()
        {
            if (!s_Live.Contains(this))
                s_Live.Add(this);
            EnsureProbe();
        }

        void OnDisable()
        {
            s_Live.Remove(this);
            _hoverSlot = -1;
            ShipPowerBarStatTooltip.HideIfOwner(this);
        }

        /// <summary>
        /// Treat a click on the colourful bar as a click on the tree card / gear tile.
        /// [UNITY] Button.OnPointerClick is public — we call it so purchase still works.
        /// </summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            Button button = GetComponentInParent<Button>();
            if (button != null && button.interactable)
                button.OnPointerClick(eventData);
        }

        /// <summary>Forwards potential-drag so a ScrollRect parent can still start a flick.</summary>
        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            Forward(eventData, ExecuteEvents.initializePotentialDrag);
        }

        /// <summary>Forwards begin-drag to a parent ScrollRect (tree / shop lists).</summary>
        public void OnBeginDrag(PointerEventData eventData)
        {
            Forward(eventData, ExecuteEvents.beginDragHandler);
        }

        /// <summary>Forwards drag so scrolling does not stick when the pointer started on the bar.</summary>
        public void OnDrag(PointerEventData eventData)
        {
            Forward(eventData, ExecuteEvents.dragHandler);
        }

        /// <summary>Forwards end-drag to the same parent that received begin-drag.</summary>
        public void OnEndDrag(PointerEventData eventData)
        {
            Forward(eventData, ExecuteEvents.endDragHandler);
        }

        /// <summary>
        /// One pointer sample for every live bar. Called from <see cref="Probe.LateUpdate"/>.
        /// Picks the closest visible tray under the cursor — not the first enabled bar.
        /// </summary>
        public static void TickAll()
        {
            // --- Pointer vs tray ---
            // Do not gate on IsOrbitMenuVisible. That flag has been wrong during
            // warmup / close, and it would keep the STAT TELEMETRY card dead.
            // Inactive parents already drop us from s_Live via OnDisable.
            // [TITAN-ORBIT] First-in-list used to win. Warmup leftovers, GEAR tiles, and
            // a stale full-card track can all "contain" the same screen point. The player
            // then sees no card (wrong bar, off-screen tip) except on the rare frame
            // the real 16px tray was first. Closest visible tray wins instead.
            if (!TryReadPointerScreen(out Vector2 screen))
            {
                ShipPowerBarStatTooltip.Hide();
                ClearSlots();
                return;
            }

            ShipPowerBarStatHoverRelay hit = null;
            int slot = -1;
            float bestArea = float.MaxValue;
            float bestDist = float.MaxValue;
            for (int i = 0; i < s_Live.Count; i++)
            {
                ShipPowerBarStatHoverRelay relay = s_Live[i];
                if (relay == null || !relay.isActiveAndEnabled || relay.Owner == null)
                    continue;
                if (!relay.gameObject.activeInHierarchy)
                    continue;
                if (!IsShownForHover(relay.transform))
                    continue;

                UnityEngine.Camera cam = EventCameraFor(relay.transform);
                if (!relay.Owner.TryHitSlot(screen, cam, out int picked, out float area, out float dist))
                    continue;

                // Closest tray center = the bar under the cursor. A leftover full-card
                // rect loses to the 16px track. Same distance: smaller tray (MEGA vs L6).
                bool better = dist + 0.5f < bestDist
                    || (Mathf.Abs(dist - bestDist) <= 0.5f && area < bestArea);
                if (!better)
                    continue;

                bestArea = area;
                bestDist = dist;
                hit = relay;
                slot = picked;
            }

            if (hit == null)
            {
                ShipPowerBarStatTooltip.Hide();
                ClearSlots();
                return;
            }

            for (int i = 0; i < s_Live.Count; i++)
            {
                if (s_Live[i] != null && s_Live[i] != hit)
                    s_Live[i]._hoverSlot = -1;
            }

            if (slot == hit._hoverSlot && ShipPowerBarStatTooltip.ActiveSlot == slot
                && ReferenceEquals(ShipPowerBarStatTooltip.ActiveOwner, hit))
                return;

            hit._hoverSlot = slot;
            hit.Owner.ShowStatTooltip(slot);
        }

        /// <summary>
        /// False when a parent CanvasGroup has faded this bar out (hidden warmup,
        /// concealed dock). Those leftover rects must not steal the hover.
        /// </summary>
        /// <param name="t">Hover pad or bar transform.</param>
        /// <returns>True when the player can see this hierarchy.</returns>
        static bool IsShownForHover(Transform t)
        {
            if (t == null)
                return false;

            // --- Walk CanvasGroups ---
            // [UNITY] Alpha multiplies down the chain unless ignoreParentGroups is on.
            // Join warmup keeps the 24-node tree active at alpha 0 so layout can
            // measure — those trays still have world rects over the loading screen.
            float alpha = 1f;
            Transform walk = t;
            while (walk != null)
            {
                CanvasGroup group = walk.GetComponent<CanvasGroup>();
                if (group != null)
                {
                    if (group.ignoreParentGroups)
                        alpha = group.alpha;
                    else
                        alpha *= group.alpha;
                    if (alpha < 0.01f)
                        return false;
                }

                walk = walk.parent;
            }

            return true;
        }

        /// <summary>Screen-space camera for Overlay (null) vs Camera-space canvases.</summary>
        static UnityEngine.Camera EventCameraFor(Transform t)
        {
            Canvas canvas = t != null ? t.GetComponentInParent<Canvas>() : null;
            if (canvas == null)
                return null;
            if (canvas.rootCanvas != null)
                canvas = canvas.rootCanvas;
            return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        /// <summary>Mouse (or primary touch) in screen pixels. False when there is no pointer.</summary>
        static bool TryReadPointerScreen(out Vector2 screen)
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
            {
                screen = Mouse.current.position.ReadValue();
                return float.IsFinite(screen.x) && float.IsFinite(screen.y);
            }

            if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
            {
                screen = Touchscreen.current.primaryTouch.position.ReadValue();
                return float.IsFinite(screen.x) && float.IsFinite(screen.y);
            }

            screen = new Vector2(-1f, -1f);
            return false;
#else
            screen = UnityEngine.Input.mousePosition;
            return float.IsFinite(screen.x) && float.IsFinite(screen.y);
#endif
        }

        static void ClearSlots()
        {
            for (int i = 0; i < s_Live.Count; i++)
            {
                if (s_Live[i] != null)
                    s_Live[i]._hoverSlot = -1;
            }
        }

        static void EnsureProbe()
        {
            if (s_Probe != null)
                return;
            var go = new GameObject("ShipPowerBarHoverProbe");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            s_Probe = go.AddComponent<Probe>();
        }

        /// <summary>
        /// Re-raises a pointer event on the first parent that implements
        /// <typeparamref name="T"/> (usually ScrollRect). Skips this GameObject
        /// so we do not recurse into ourselves.
        /// </summary>
        void Forward<T>(PointerEventData eventData, ExecuteEvents.EventFunction<T> functor)
            where T : IEventSystemHandler
        {
            if (transform.parent == null)
                return;
            ExecuteEvents.ExecuteHierarchy(transform.parent.gameObject, eventData, functor);
        }

        /// <summary>Hidden runner — one LateUpdate for every power-bar pad.</summary>
        sealed class Probe : MonoBehaviour
        {
            void LateUpdate()
            {
                TickAll();
            }
        }
    }
}
