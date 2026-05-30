using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Power-score colour legend for the ship upgrade tree (five category pairs).
    /// </summary>
    public class ShipUpgradeTreeLegendUI : MonoBehaviour
    {
        private const float ReferenceLegendWidth = 900f;
        private const float RowPreferredHeight = 26f;

        [SerializeField] private ShipUpgradeTreeLegendPairUI[] pairs;

        private bool _layoutNormalized;
        private float _lastAppliedWidth = -1f;

        private void Awake()
        {
            EnsureReadableLayout();
            RefreshStaticLabels();
        }

        public void RefreshStaticLabels()
        {
            if (pairs == null)
                return;

            for (int i = 0; i < pairs.Length && i < ShipAbilityCategoryColors.PowerBreakdownPairCount; i++)
            {
                if (pairs[i] != null)
                    pairs[i].Configure(i);
            }
        }

        public void ApplyResponsiveLayout(float availableWidth)
        {
            EnsureReadableLayout();

            if (Mathf.Abs(_lastAppliedWidth - availableWidth) < 8f)
                return;

            _lastAppliedWidth = availableWidth;
            float scale = Mathf.Clamp(availableWidth / ReferenceLegendWidth, 1f, 1.45f);

            if (pairs != null)
            {
                for (int i = 0; i < pairs.Length; i++)
                {
                    if (pairs[i] != null)
                        pairs[i].ApplyResponsiveScale(scale);
                }
            }

            var legendLe = GetComponent<LayoutElement>();
            if (legendLe != null)
            {
                float rowH = RowPreferredHeight * scale;
                legendLe.preferredHeight = rowH * 2f + 8f;
                legendLe.minHeight = legendLe.preferredHeight;
            }
        }

        /// <summary>Upgrades legacy single-row legends to two readable rows and moves stat labels below swatches.</summary>
        private void EnsureReadableLayout()
        {
            if (_layoutNormalized || pairs == null || pairs.Length == 0)
                return;

            _layoutNormalized = true;

            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i] != null)
                    pairs[i].EnsureVerticalStatLayout();
            }

            var singleRowHlg = GetComponent<HorizontalLayoutGroup>();
            if (singleRowHlg == null)
                return;

            var pairTransforms = new Transform[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
                pairTransforms[i] = pairs[i] != null ? pairs[i].transform : null;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(singleRowHlg);
            else
#endif
                Destroy(singleRowHlg);

            var rootVlg = gameObject.AddComponent<VerticalLayoutGroup>();
            rootVlg.spacing = 4f;
            rootVlg.childAlignment = TextAnchor.UpperLeft;
            rootVlg.childControlWidth = true;
            rootVlg.childControlHeight = true;
            rootVlg.childForceExpandWidth = true;
            rootVlg.childForceExpandHeight = false;

            var row0 = CreateLegendRow(transform, "Row0", TextAnchor.MiddleLeft);
            var row1 = CreateLegendRow(transform, "Row1", TextAnchor.MiddleCenter);

            for (int i = 0; i < pairTransforms.Length; i++)
            {
                if (pairTransforms[i] == null)
                    continue;
                var targetRow = i < 3 ? row0 : row1;
                pairTransforms[i].SetParent(targetRow, false);
            }

            var legendLe = GetComponent<LayoutElement>();
            if (legendLe != null)
            {
                legendLe.preferredHeight = 56f;
                legendLe.minHeight = 52f;
            }
        }

        private static Transform CreateLegendRow(Transform parent, string name, TextAnchor alignment)
        {
            var rowGo = new GameObject(name, typeof(RectTransform));
            rowGo.transform.SetParent(parent, false);
            var rowHlg = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowHlg.spacing = 10f;
            rowHlg.childAlignment = alignment;
            rowHlg.childControlWidth = true;
            rowHlg.childControlHeight = true;
            rowHlg.childForceExpandWidth = true;
            rowHlg.childForceExpandHeight = true;
            var rowLe = rowGo.AddComponent<LayoutElement>();
            rowLe.flexibleWidth = 1f;
            rowLe.preferredHeight = RowPreferredHeight;
            rowLe.minHeight = 22f;
            return rowGo.transform;
        }
    }
}
