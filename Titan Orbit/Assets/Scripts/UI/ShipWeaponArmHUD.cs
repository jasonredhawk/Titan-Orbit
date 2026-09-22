using System.Globalization;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Compact arsenal strip under the ship stats, stacked with WEAPONS: every barrel on the local ship, grouped by
    /// combat class (gun / laser / missile / sniper). Click a cell to mute that
    /// barrel; click the class header to mute or arm the whole group. Each chip
    /// has two bars: the inner square is sequential hull energy (left to right
    /// across the strip), and the border is that barrel's fire-rate timer.
    /// Both full means the square can fire. User-off is a muted slate chip.
    /// <para>
    /// Regular family hulls show one GUN (or live bullet-type name) group.
    /// MEGA / Titan hulls show whichever classes the catalog actually mounted.
    /// Writes nothing to ECS directly — clicks latch <see cref="WeaponArmSelection"/>;
    /// <see cref="ShipWeaponArmSystem"/> then writes the ghosted
    /// <see cref="ShipWeaponArmState"/> on the predicted tick.
    /// </para>
    /// <para>
    /// Parks under <see cref="BulletTypeHUD"/> in the column below the ship stats
    /// (or under rockets when that strip is hidden). <see cref="SpaceBrakesHUD"/> docks under
    /// this glass. Hidden on the main menu, Join Team, Orbit Menu, and while the
    /// local ship is dead. Holds last paint during
    /// <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>.
    /// </para>
    /// Dark space-gamer chrome — same void glass as <see cref="BulletTypeHUD"/>.
    /// </summary>
    [DefaultExecutionOrder(66250)]
    public class ShipWeaponArmHUD : MonoBehaviour
    {
        /// <summary>Gun / laser / missile / sniper — one header row each.</summary>
        const int MaxGroups = 4;

        /// <summary>Pooled cells. MEGA hulls can exceed 40 barrels; extras still mute via the group header.</summary>
        const int MaxCells = 48;

        /// <summary>How many energy chips sit on one group row.</summary>
        const int CellsPerRow = 6;

        const float PanelPad = 6f;
        const float HeaderHeight = 14f;
        /// <summary>Air under ARSENAL before the first class row / chips.</summary>
        const float HeaderContentGap = 6f;
        const float GroupHeaderHeight = 15f;
        /// <summary>Air between GUN ×4 and the chip row.</summary>
        const float GroupHeaderCellGap = 3f;
        const float CellWidth = 16f;
        const float CellHeight = 12f;
        const float CellGap = 2f;
        const float GroupGap = 5f;

        /// <summary>Matches the WEAPONS / rocket tile column (108 + 6+6 pad).</summary>
        const float PanelWidth = 120f;

        /// <summary>Air between the WEAPONS glass and this strip.</summary>
        const float DockGap = 8f;
        const float ChipWidth = 28f;

        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.92f);
        static readonly Color CaptionSelected = new Color(0.95f, 0.98f, 1f, 1f);
        static readonly Color CaptionDim = new Color(0.62f, 0.78f, 0.95f, 0.55f);
        static readonly Color BodyColor = new Color(0.94f, 0.97f, 1f, 1f);
        static readonly Color BodyDim = new Color(0.88f, 0.92f, 0.98f, 0.5f);
        static readonly Color ReadyColor = new Color(0.45f, 0.92f, 0.62f, 1f);
        static readonly Color OffColor = new Color(0.95f, 0.55f, 0.32f, 1f);
        static readonly Color CaretColor = new Color(0.45f, 0.95f, 1f, 1f);
        static readonly Color LabelOutline = new Color(0.02f, 0.04f, 0.08f, 0.95f);
        static readonly Color HeaderColor = new Color(0.55f, 0.72f, 0.88f, 0.7f);
        static readonly Color RuleColor = new Color(0.18f, 0.28f, 0.40f, 0.75f);
        static readonly Color CellBack = new Color(0.04f, 0.07f, 0.12f, 0.94f);
        static readonly Color EnergyDim = new Color(0.20f, 0.36f, 0.52f, 0.88f);
        static readonly Color EnergyCharging = new Color(0.32f, 0.62f, 0.82f, 0.92f);
        static readonly Color EnergyReady = new Color(0.45f, 0.95f, 1f, 0.96f);
        static readonly Color UserOffFill = new Color(0.26f, 0.28f, 0.34f, 0.88f);
        static readonly Color UserOffTint = new Color(0.40f, 0.43f, 0.50f, 0.82f);
        static readonly Color CooldownFill = new Color(0.22f, 0.28f, 0.36f, 0.7f);
        static readonly Color RoFTrack = new Color(0.10f, 0.16f, 0.24f, 0.95f);
        static readonly Color RoFCharging = new Color(0.95f, 0.72f, 0.28f, 0.96f);
        static readonly Color RoFReady = new Color(0.55f, 0.98f, 0.62f, 1f);
        static readonly Color KeycapFill = new Color(0.04f, 0.10f, 0.16f, 0.96f);
        static readonly Color KeycapOff = new Color(0.12f, 0.06f, 0.06f, 0.9f);
        const float BorderThickness = 2f;

        /// <summary>What the player sees on one energy chip.</summary>
        enum CellLook : byte
        {
            UserOff = 0,
            Starved = 1,
            Charging = 2,
            Ready = 3,
            Cooldown = 4,
        }

        /// <summary>One class header (GUN ×4 + ALL chip).</summary>
        sealed class GroupRow
        {
            public GameObject Root;
            public RectTransform Rect;
            public Button Button;
            public TextMeshProUGUI KindLabel;
            public Image Chip;
            public TextMeshProUGUI ChipLabel;
            public byte Kind;
            public int Count;
            public bool AnyArmed;
        }

        /// <summary>One barrel chip — inner energy bar, border fire-rate bar, click to mute.</summary>
        sealed class BarrelCell
        {
            public GameObject Root;
            public RectTransform Rect;
            public Button Button;
            public Image Background;
            public Image Fill;
            public Image Caret;
            public Outline Outline;
            public Image RoFTop;
            public Image RoFRight;
            public Image RoFBottom;
            public Image RoFLeft;
            public Image RoFTrackTop;
            public Image RoFTrackRight;
            public Image RoFTrackBottom;
            public Image RoFTrackLeft;
            public int MountIndex;
            public byte Kind;
        }

        /// <summary>
        /// Live overlay instance. <see cref="SpaceBrakesHUD"/> docks under this strip
        /// after we LateUpdate.
        /// </summary>
        static ShipWeaponArmHUD _instance;

        Canvas _canvas;
        RectTransform _panel;
        GameObject _mainMenuPanel;
        BulletVfxBank _bank;
        PlanetShipFamilyConfig _familyConfig;
        static Sprite s_fillSprite;

        readonly GroupRow[] _groups = new GroupRow[MaxGroups];
        readonly BarrelCell[] _cells = new BarrelCell[MaxCells];
        readonly int[] _kindCounts = new int[MaxGroups];
        readonly int[] _kindArmed = new int[MaxGroups];

        /// <summary>Per-mount energy fill (0–1). Sequential hull pool, left to right.</summary>
        readonly float[] _mountFill = new float[ShipWeaponArmState.MaxTrackedMounts];

        /// <summary>Per-mount fire-rate border fill (0–1). 1 = delay finished.</summary>
        readonly float[] _mountRoFFill = new float[ShipWeaponArmState.MaxTrackedMounts];

        /// <summary>Displayed ready delay, counted down between ghost snapshots.</summary>
        readonly float[] _shownCooldown = new float[ShipWeaponArmState.MaxTrackedMounts];

        /// <summary>Last ghost ready delay, so a new snapshot can resync the border.</summary>
        readonly float[] _lastGhostCooldown = new float[ShipWeaponArmState.MaxTrackedMounts];

        bool _cooldownSmoothInit;

        /// <summary>Per-mount look. Same index as <see cref="_mountFill"/>.</summary>
        readonly CellLook[] _mountLook = new CellLook[ShipWeaponArmState.MaxTrackedMounts];

        /// <summary>Armed mount indices in HUD strip order (kind, then buffer index).</summary>
        readonly int[] _cascadeOrder = new int[ShipWeaponArmState.MaxTrackedMounts];

        int _lastMountCount = -1;
        int _lastKindSignature = int.MinValue;
        string _lastGunCaption = string.Empty;
        bool _hasPainted;

        /// <summary>[UNITY] Creates the HUD once after the first scene load.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (FindFirstObjectByType<ShipWeaponArmHUD>() != null)
                return;

            var go = new GameObject(nameof(ShipWeaponArmHUD));
            DontDestroyOnLoad(go);
            go.AddComponent<ShipWeaponArmHUD>();
        }

        /// <summary>Builds the top-left overlay canvas and empty group / cell pools.</summary>
        void Awake()
        {
            _instance = this;
            _familyConfig = PlanetShipFamilyConfig.LoadDefault();
            _bank = BulletVfxBank.LoadDefault();
            BuildUi();
            SetVisible(false);
        }

        /// <summary>Drops the static dock pointer so brakes cannot sit under a destroyed panel.</summary>
        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        /// <summary>
        /// Bottom edge of the arsenal glass in the shared 1920×1080 overlay
        /// (top-left anchor space). <see cref="SpaceBrakesHUD"/> calls this after we
        /// LateUpdate so CTRL docks under this strip.
        /// </summary>
        /// <param name="y">Overlay Y of the panel bottom when visible, or 0 when hidden.</param>
        /// <param name="visible">True when at least one barrel group is showing.</param>
        /// <returns>True when this HUD exists and has a panel to measure.</returns>
        public static bool TryGetOverlayDockBottomY(out float y, out bool visible)
        {
            y = 0f;
            visible = false;
            if (_instance == null || _instance._panel == null)
                return false;

            visible = _instance._panel.gameObject.activeSelf;
            if (!visible)
                return true;

            y = RocketLoadoutHUD.OverlayPanelBottomY(_instance._panel);
            return true;
        }

        /// <summary>
        /// Reads the local ship, paints energy chips, and hides on menus / death.
        /// No ECS gathers during join Instantiates — last paint stays up.
        /// </summary>
        void LateUpdate()
        {
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl() ||
                IsMainMenuShowing() ||
                MoonOrbitClientState.IsOrbitMenuVisible ||
                HUDController.LocalPlayerDeathHidesHud ||
                HUDController.MinimapExpandedObscuresHud ||
                HUDController.CommsMatrixObscuresHud)
            {
                if (HUDController.LocalPlayerDeathHidesHud ||
                    ClientTeamFlowState.ShouldSuppressLocalPlayerControl() ||
                    IsMainMenuShowing())
                    WeaponArmSelection.Clear();
                SetVisible(false);
                return;
            }

            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                if (_panel != null &&
                    _panel.gameObject.activeSelf &&
                    (EcsGameBridge.HasLocalPlayerShip() || ShipDisplayPose.HasLocalPose))
                    return;

                SetVisible(false);
                return;
            }

            if (!EcsGameBridge.HasLocalPlayerShip())
            {
                WeaponArmSelection.Clear();
                SetVisible(false);
                return;
            }

            if (!TryPaintLocalShip())
            {
                SetVisible(false);
                return;
            }

            SetVisible(true);
            ApplyDock();
        }

        /// <summary>
        /// Parks this strip under the WEAPONS fire-type glass, or under rockets when
        /// that strip is hidden, or under the ship stats when both are hidden.
        /// Execution order 66250 runs after <see cref="BulletTypeHUD"/> (66220).
        /// </summary>
        void ApplyDock()
        {
            if (_panel == null)
                return;

            float dockBottom = 0f;
            bool stacked = false;

            if (BulletTypeHUD.TryGetOverlayDockBottomY(out dockBottom, out bool bulletsVisible) &&
                bulletsVisible)
            {
                stacked = true;
            }
            else if (RocketLoadoutHUD.TryGetOverlayDockBottomY(out dockBottom, out bool rocketsVisible) &&
                     rocketsVisible)
            {
                stacked = true;
            }

            RocketLoadoutHUD.PlaceInLeftColumn(_panel, stacked, dockBottom, DockGap);
        }

        /// <summary>
        /// Reads mounts, mute mask, and energy from the client-world local ship,
        /// then refreshes group headers and cell fills.
        /// </summary>
        /// <returns>True when at least one barrel should show.</returns>
        bool TryPaintLocalShip()
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity ship) ||
                ship == Entity.Null)
                return false;

            var em = world.EntityManager;
            if (!em.HasBuffer<ShipWeaponMountElement>(ship))
                return false;

            var mounts = em.GetBuffer<ShipWeaponMountElement>(ship);
            if (mounts.Length <= 0)
                return false;

            var gunners = em.HasBuffer<MegaShipGunnerSlotElement>(ship)
                ? em.GetBuffer<MegaShipGunnerSlotElement>(ship)
                : default;
            ShipWeaponKind.RestoreMountKindsFromGhostedSlots(mounts, gunners);

            var ghostArm = ShipWeaponArmState.Resolve(em, ship);
            var arm = WeaponArmSelection.Overlay(in ghostArm, mounts);

            bool isMega = em.HasComponent<MegaShipState>(ship)
                          && em.GetComponentData<MegaShipState>(ship).IsMega;

            var shipState = em.HasComponent<ShipState>(ship)
                ? em.GetComponentData<ShipState>(ship)
                : default;
            var weaponCfg = em.HasComponent<ShipWeaponConfig>(ship)
                ? em.GetComponentData<ShipWeaponConfig>(ship)
                : default;

            float energy = shipState.CurrentEnergy;
            var megaState = isMega && em.HasComponent<MegaShipState>(ship)
                ? em.GetComponentData<MegaShipState>(ship)
                : default;
            bool laserLockout = isMega
                                && megaState.CannonLaserLockout
                                && !megaState.CannonLaserPulseOn;

            float abilityEnergy = 0f;
            if (!isMega && em.HasComponent<ShipLoadoutState>(ship))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(ship);
                int bank = BulletBankFireResolve.ResolveFireBankIndex(in loadout);
                int extras = 0;
                if (em.HasComponent<ShipAttributeUpgradeState>(ship))
                {
                    extras = BulletBankCombatLogic.CountFirePowerExtraLevels(
                        shipState.ShipLevel,
                        em.GetComponentData<ShipAttributeUpgradeState>(ship).FirePower);
                }

                abilityEnergy = BulletBankCombatLogic.GetAbilityEnergyDrain(bank, extras);
                if (shipState.MaxEnergy > 1.05f)
                    abilityEnergy = Mathf.Min(abilityEnergy, shipState.MaxEnergy - 1.05f);
            }

            string gunCaption = ResolveGunCaption(em, ship, isMega);
            CountKinds(mounts, in arm);
            int kindSignature = _kindCounts[0] | (_kindCounts[1] << 8) | (_kindCounts[2] << 16) | (_kindCounts[3] << 24);
            bool layoutDirty = mounts.Length != _lastMountCount
                               || kindSignature != _lastKindSignature
                               || gunCaption != _lastGunCaption
                               || !_hasPainted;
            _lastMountCount = mounts.Length;
            _lastKindSignature = kindSignature;
            _lastGunCaption = gunCaption;
            _hasPainted = true;

            DynamicBuffer<ShipWeaponReadyElement> ready = default;
            if (em.HasBuffer<ShipWeaponReadyElement>(ship))
                ready = em.GetBuffer<ShipWeaponReadyElement>(ship);

            ComputeMountCharges(
                mounts, ready, in arm, isMega, laserLockout, energy,
                in weaponCfg, abilityEnergy);

            int paintedCells = PaintGroupsAndCells(mounts, gunCaption, layoutDirty);

            return paintedCells > 0 || VisibleGroupCount() > 0;
        }

        /// <summary>How many class headers have at least one barrel.</summary>
        int VisibleGroupCount()
        {
            int n = 0;
            for (int k = 0; k < MaxGroups; k++)
            {
                if (_kindCounts[k] > 0)
                    n++;
            }

            return n;
        }

        /// <summary>Tallies barrels and armed barrels per <see cref="ShipWeaponKind"/>.</summary>
        void CountKinds(DynamicBuffer<ShipWeaponMountElement> mounts, in ShipWeaponArmState arm)
        {
            for (int k = 0; k < MaxGroups; k++)
            {
                _kindCounts[k] = 0;
                _kindArmed[k] = 0;
            }

            int count = mounts.Length;
            for (int i = 0; i < count; i++)
            {
                int kind = mounts[i].WeaponKind;
                if (kind < 0 || kind >= MaxGroups)
                    kind = ShipWeaponKind.Gun;
                _kindCounts[kind]++;
                if (ShipWeaponArmState.IsArmed(in arm, i))
                    _kindArmed[kind]++;
            }
        }

        /// <summary>
        /// Stacks visible class headers and energy chips, then sizes the glass.
        /// Clip fills come from <see cref="_mountFill"/> (already computed this frame).
        /// </summary>
        int PaintGroupsAndCells(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            string gunCaption,
            bool layoutDirty)
        {
            float y = -PanelPad - HeaderHeight - HeaderContentGap;
            int cellCursor = 0;
            int groupCursor = 0;

            for (int kind = 0; kind < MaxGroups; kind++)
            {
                int count = _kindCounts[kind];
                if (count <= 0)
                    continue;

                GroupRow group = _groups[groupCursor++];
                bool anyArmed = _kindArmed[kind] > 0;
                string caption = layoutDirty || group.KindLabel == null || string.IsNullOrEmpty(group.KindLabel.text)
                    ? FormatGroupCaption(kind, count, gunCaption)
                    : group.KindLabel.text;
                PaintGroupHeader(group, kind, count, anyArmed, caption, y, layoutDirty);
                y -= GroupHeaderHeight + GroupHeaderCellGap;

                int shown = 0;
                int mountCount = mounts.Length;
                for (int i = 0; i < mountCount && cellCursor < MaxCells; i++)
                {
                    byte mountKind = mounts[i].WeaponKind;
                    if (mountKind != kind)
                        continue;

                    BarrelCell cell = _cells[cellCursor++];
                    float fill = i < _mountFill.Length ? _mountFill[i] : 0f;
                    float rof = i < _mountRoFFill.Length ? _mountRoFFill[i] : 0f;
                    CellLook look = i < _mountLook.Length ? _mountLook[i] : CellLook.Starved;

                    int row = shown / CellsPerRow;
                    int col = shown % CellsPerRow;
                    float cellX = PanelPad + col * (CellWidth + CellGap);
                    float cellY = y - row * (CellHeight + CellGap);
                    PaintCell(cell, i, mountKind, fill, rof, look, cellX, cellY, layoutDirty);
                    shown++;
                }

                int rows = shown <= 0 ? 0 : (shown + CellsPerRow - 1) / CellsPerRow;
                y -= rows * (CellHeight + CellGap) + GroupGap;
            }

            for (int g = groupCursor; g < MaxGroups; g++)
                HideGroup(_groups[g]);
            for (int c = cellCursor; c < MaxCells; c++)
                HideCell(_cells[c]);

            float contentHeight = Mathf.Max(HeaderHeight + CellHeight + PanelPad * 2f, -y + PanelPad);
            if (_panel != null)
                _panel.sizeDelta = new Vector2(PanelWidth, contentHeight);

            return cellCursor;
        }

        /// <summary>Writes one class header. Text is dirty-checked so TMP does not rebuild every frame.</summary>
        void PaintGroupHeader(
            GroupRow group,
            int kind,
            int count,
            bool anyArmed,
            string caption,
            float y,
            bool layoutDirty)
        {
            if (group?.Root == null)
                return;

            group.Root.SetActive(true);
            group.Kind = (byte)kind;
            group.Count = count;
            group.AnyArmed = anyArmed;
            if (layoutDirty)
            {
                group.Rect.sizeDelta = new Vector2(PanelWidth - PanelPad * 2f, GroupHeaderHeight);
                group.Rect.anchoredPosition = new Vector2(PanelPad, y);
            }

            if (group.KindLabel != null && group.KindLabel.text != caption)
                group.KindLabel.text = caption;
            if (group.KindLabel != null)
                group.KindLabel.color = anyArmed ? CaptionSelected : CaptionDim;

            if (group.ChipLabel != null)
            {
                string chip = anyArmed ? "ALL" : "OFF";
                if (group.ChipLabel.text != chip)
                    group.ChipLabel.text = chip;
                group.ChipLabel.color = anyArmed ? ReadyColor : OffColor;
            }

            if (group.Chip != null)
                group.Chip.color = anyArmed ? KeycapFill : KeycapOff;
        }

        /// <summary>
        /// Inner square is sequential energy. Border is the fire-rate timer,
        /// drawn clockwise from the top edge.
        /// </summary>
        void PaintCell(
            BarrelCell cell,
            int mountIndex,
            byte kind,
            float fill,
            float rof,
            CellLook look,
            float x,
            float y,
            bool layoutDirty)
        {
            if (cell?.Root == null)
                return;

            cell.Root.SetActive(true);
            cell.MountIndex = mountIndex;
            cell.Kind = kind;
            if (layoutDirty)
            {
                cell.Rect.sizeDelta = new Vector2(CellWidth, CellHeight);
                cell.Rect.anchoredPosition = new Vector2(x, y);
            }

            bool userOff = look == CellLook.UserOff;
            if (cell.Fill != null)
            {
                cell.Fill.fillAmount = userOff ? 1f : Mathf.Clamp01(fill);
                cell.Fill.color = ColorForEnergy(look, fill);
            }

            if (cell.Background != null)
                cell.Background.color = userOff ? UserOffFill : CellBack;
            if (cell.Caret != null)
                cell.Caret.enabled = !userOff && fill > 0.001f && fill < 0.999f;
            if (cell.Outline != null)
                cell.Outline.enabled = false;

            PaintRoFBorder(cell, userOff ? 0f : Mathf.Clamp01(rof), userOff);
        }

        /// <summary>
        /// Inner fill is the hull pool left to right. Border fill is
        /// <c>1 - cooldown / (1/fireRate)</c>, moved between snapshots.
        /// Both full and not lockout is ready.
        /// </summary>
        void ComputeMountCharges(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            DynamicBuffer<ShipWeaponReadyElement> ready,
            in ShipWeaponArmState arm,
            bool isMega,
            bool laserLockout,
            float energy,
            in ShipWeaponConfig weaponCfg,
            float abilityEnergy)
        {
            int mountCount = mounts.Length;
            if (mountCount > ShipWeaponArmState.MaxTrackedMounts)
                mountCount = ShipWeaponArmState.MaxTrackedMounts;

            EnsureCooldownSmooth();
            for (int i = 0; i < ShipWeaponArmState.MaxTrackedMounts; i++)
            {
                _mountFill[i] = 0f;
                _mountRoFFill[i] = 0f;
                _mountLook[i] = CellLook.Starved;
                _cascadeOrder[i] = -1;
            }

            for (int i = 0; i < mountCount; i++)
            {
                if (!ShipWeaponArmState.IsArmed(in arm, i))
                    _mountLook[i] = CellLook.UserOff;
            }

            int orderCount = ShipWeaponFireLogic.BuildArmedStripOrder(
                mounts, in arm, _cascadeOrder, skipCannonLasers: false);
            float remaining = energy;

            for (int n = 0; n < orderCount; n++)
            {
                int i = _cascadeOrder[n];
                if (i < 0 || i >= mounts.Length)
                    continue;
                if (_mountLook[i] == CellLook.UserOff)
                    continue;

                ShipWeaponMountElement mount = mounts[i];
                float cost = ResolveMountShotCost(mount, isMega, in weaponCfg, abilityEnergy);
                float fill = ShipWeaponFireLogic.SequentialSquareFill(remaining, cost);
                _mountFill[i] = fill;
                bool energyFull = ShipWeaponFireLogic.TryTakeSequentialSlot(ref remaining, cost);

                float ghost = mount.FireCooldown;
                if (ready.IsCreated && i < ready.Length)
                    ghost = ready[i].FireCooldown;
                if (float.IsNaN(ghost) || ghost < 0f)
                    ghost = 0f;

                float interval = ShipWeaponFireLogic.ReadyInterval(mount, weaponCfg.FireRate);
                float shown = SmoothReady(i, ghost);
                float rof = 1f - Mathf.Clamp01(shown / interval);
                _mountRoFFill[i] = rof;

                bool rofReady = shown <= 0.001f;
                bool laserWaiting = ShipWeaponKind.IsCannonLaser(mount) && laserLockout;
                if (!energyFull)
                    _mountLook[i] = fill > 0.001f ? CellLook.Charging : CellLook.Starved;
                else if (!rofReady || laserWaiting)
                    _mountLook[i] = CellLook.Cooldown;
                else
                    _mountLook[i] = CellLook.Ready;
            }
        }

        /// <summary>First paint treats every slot as unsynced so the ghost value wins.</summary>
        void EnsureCooldownSmooth()
        {
            if (_cooldownSmoothInit)
                return;

            for (int i = 0; i < _shownCooldown.Length; i++)
            {
                _shownCooldown[i] = -1f;
                _lastGhostCooldown[i] = -1f;
            }

            _cooldownSmoothInit = true;
        }

        /// <summary>
        /// Counts the ready delay down between snapshots. A new ghost value
        /// (including a shot that restarts the timer) snaps the border back.
        /// </summary>
        float SmoothReady(int index, float ghost)
        {
            float prev = _lastGhostCooldown[index];
            float shown = _shownCooldown[index];
            if (shown < 0f || Mathf.Abs(ghost - prev) > 0.0005f)
                shown = ghost;
            else
                shown = Mathf.Max(0f, shown - Time.deltaTime);

            _shownCooldown[index] = shown;
            _lastGhostCooldown[index] = ghost;
            return shown;
        }

        /// <summary>Energy this barrel must hold before it can fire (HUD clip size).</summary>
        static float ResolveMountShotCost(
            in ShipWeaponMountElement mount,
            bool isMega,
            in ShipWeaponConfig weaponCfg,
            float abilityEnergy)
        {
            return ShipWeaponFireLogic.GetShotCost(
                mount, isMega, weaponCfg.BulletDamage, weaponCfg.FireRate, abilityEnergy);
        }


        /// <summary>Inner-square color from energy only. RoF lives on the border.</summary>
        static Color ColorForEnergy(CellLook look, float fill)
        {
            if (look == CellLook.UserOff)
                return UserOffTint;
            if (fill >= 0.999f)
                return EnergyReady;
            if (fill > 0.001f)
                return EnergyCharging;
            return EnergyDim;
        }

        /// <summary>Clockwise fire-rate ring. Dark track stays; bright fill grows.</summary>
        static void PaintRoFBorder(BarrelCell cell, float rof, bool userOff)
        {
            Color fill = userOff
                ? UserOffTint
                : (rof >= 0.999f ? RoFReady : RoFCharging);
            Color track = userOff ? UserOffFill : RoFTrack;
            SetRoFTrack(cell.RoFTrackTop, track);
            SetRoFTrack(cell.RoFTrackRight, track);
            SetRoFTrack(cell.RoFTrackBottom, track);
            SetRoFTrack(cell.RoFTrackLeft, track);
            SetRoFEdge(cell.RoFTop, EdgeFill(rof, 0), fill);
            SetRoFEdge(cell.RoFRight, EdgeFill(rof, 1), fill);
            SetRoFEdge(cell.RoFBottom, EdgeFill(rof, 2), fill);
            SetRoFEdge(cell.RoFLeft, EdgeFill(rof, 3), fill);
        }

        /// <summary>How much of this quarter of the ring is lit (0–1).</summary>
        static float EdgeFill(float rof, int edge)
        {
            return Mathf.Clamp01((rof - edge * 0.25f) / 0.25f);
        }

        static void SetRoFTrack(Image img, Color track)
        {
            if (img == null)
                return;
            img.enabled = true;
            img.color = track;
        }

        static void SetRoFEdge(Image img, float amount, Color fill)
        {
            if (img == null)
                return;
            img.enabled = true;
            img.fillAmount = Mathf.Clamp01(amount);
            img.color = fill;
        }

        /// <summary>ALL-CAPS class line, e.g. <c>LASERBOLT ×4</c>.</summary>
        static string FormatGroupCaption(int kind, int count, string gunCaption)
        {
            string label = kind == ShipWeaponKind.Gun && !string.IsNullOrEmpty(gunCaption)
                ? gunCaption
                : KindLabel(kind);
            return string.Concat(label, " ×", count.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Player-facing class word. Cannon prints LASER to match the beam.</summary>
        static string KindLabel(int kind)
        {
            switch (kind)
            {
                case ShipWeaponKind.Cannon: return ShipWeaponLoadout.LabelLaser;
                case ShipWeaponKind.Missile: return ShipWeaponLoadout.LabelMissile;
                case ShipWeaponKind.Sniper: return ShipWeaponLoadout.LabelSniper;
                default: return ShipWeaponLoadout.LabelGun;
            }
        }

        /// <summary>
        /// Live gun-group name from the hull's fire bank (LASERBOLT) so this
        /// strip matches the Weapons tiles beside it.
        /// </summary>
        string ResolveGunCaption(EntityManager em, Entity ship, bool isMega)
        {
            int bank = 0;
            if (em.HasComponent<ShipLoadoutState>(ship))
                bank = BulletBankFireResolve.ResolveFireBankIndex(em.GetComponentData<ShipLoadoutState>(ship));

            if (_bank == null)
                _bank = BulletVfxBank.LoadDefault();
            if (_bank != null)
            {
                string name = _bank.GetCategoryName(bank);
                if (!string.IsNullOrEmpty(name))
                    return name.ToUpperInvariant();
            }

            if (!isMega && em.HasComponent<ShipState>(ship))
            {
                if (_familyConfig == null)
                    _familyConfig = PlanetShipFamilyConfig.LoadDefault();
                if (_familyConfig != null)
                {
                    string family = _familyConfig.GetFamilyDisplayNameForDefaultBank(bank);
                    if (!string.IsNullOrEmpty(family))
                        return family.ToUpperInvariant();
                }
            }

            return ShipWeaponLoadout.LabelGun;
        }

        /// <summary>Mute or arm every barrel of that class.</summary>
        void OnGroupClicked(int groupIndex)
        {
            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return;
            if (PlanetaryDefenseTurretClientState.IsControlling)
                return;
            if (groupIndex < 0 || groupIndex >= MaxGroups)
                return;

            GroupRow group = _groups[groupIndex];
            if (group == null || group.Root == null || !group.Root.activeSelf)
                return;

            bool enable = !group.AnyArmed;
            WeaponArmSelection.Request(ShipWeaponArmState.ModeKind, group.Kind, enable);
        }

        /// <summary>Mute or arm one barrel. Reads the live mask so a second tap flips back.</summary>
        void OnCellClicked(int cellIndex)
        {
            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return;
            if (PlanetaryDefenseTurretClientState.IsControlling)
                return;
            if (cellIndex < 0 || cellIndex >= MaxCells)
                return;

            BarrelCell cell = _cells[cellIndex];
            if (cell == null || cell.Root == null || !cell.Root.activeSelf)
                return;

            bool currentlyArmed = true;
            if (TryReadOverlayArm(out ShipWeaponArmState arm))
                currentlyArmed = ShipWeaponArmState.IsArmed(in arm, cell.MountIndex);

            WeaponArmSelection.Request(ShipWeaponArmState.ModeMount, cell.MountIndex, !currentlyArmed);
        }

        /// <summary>Ghosted mask plus the optimistic HUD click, when the local ship is available.</summary>
        static bool TryReadOverlayArm(out ShipWeaponArmState arm)
        {
            arm = ShipWeaponArmState.AllOn;
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity ship) ||
                ship == Entity.Null)
                return false;
            if (!world.EntityManager.HasBuffer<ShipWeaponMountElement>(ship))
                return false;

            var mounts = world.EntityManager.GetBuffer<ShipWeaponMountElement>(ship);
            arm = WeaponArmSelection.Overlay(ShipWeaponArmState.Resolve(world.EntityManager, ship), mounts);
            return true;
        }

        static void HideGroup(GroupRow group)
        {
            if (group?.Root != null)
                group.Root.SetActive(false);
        }

        static void HideCell(BarrelCell cell)
        {
            if (cell == null || cell.Root == null)
                return;
            cell.Root.SetActive(false);
            if (cell.Caret != null)
                cell.Caret.enabled = false;
            if (cell.Outline != null)
                cell.Outline.enabled = false;
        }

        bool IsMainMenuShowing()
        {
            if (_mainMenuPanel == null)
                _mainMenuPanel = GameObject.Find("MainMenuPanel");
            return _mainMenuPanel != null && _mainMenuPanel.activeInHierarchy;
        }

        void SetVisible(bool visible)
        {
            if (_canvas != null)
                _canvas.enabled = true;
            if (_panel != null)
                _panel.gameObject.SetActive(visible);
        }

        /// <summary>Builds dark-glass canvas, group headers, and the barrel-chip pool.</summary>
        void BuildUi()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 80;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            if (s_fillSprite == null)
                s_fillSprite = CreateWhiteSprite();

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.GetComponent<RectTransform>();
            RocketLoadoutHUD.PlaceInLeftColumn(_panel, false, 0f, 0f);
            _panel.sizeDelta = new Vector2(PanelWidth, HeaderHeight + CellHeight + PanelPad * 2f);
            var bg = panelGo.GetComponent<Image>();
            bg.color = FillColor;
            bg.raycastTarget = true;

            var accentGo = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accentGo.transform.SetParent(_panel, false);
            var accentRt = accentGo.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 0f);
            accentRt.anchorMax = new Vector2(0f, 1f);
            accentRt.pivot = new Vector2(0f, 0.5f);
            accentRt.sizeDelta = new Vector2(3f, 0f);
            accentRt.anchoredPosition = Vector2.zero;
            accentGo.GetComponent<Image>().color = ShipAbilityCategoryColors.WeaponForHud;
            accentGo.GetComponent<Image>().raycastTarget = false;

            var header = CreateLabel(_panel, "Header", "ARSENAL", 7.5f, HeaderColor, TextAlignmentOptions.Left);
            var headerRt = header.rectTransform;
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.anchoredPosition = new Vector2(0f, -2f);
            headerRt.sizeDelta = new Vector2(-16f, HeaderHeight);
            header.characterSpacing = 2.4f;

            var ruleGo = new GameObject("HeaderRule", typeof(RectTransform), typeof(Image));
            ruleGo.transform.SetParent(_panel, false);
            var ruleRt = ruleGo.GetComponent<RectTransform>();
            ruleRt.anchorMin = new Vector2(0f, 1f);
            ruleRt.anchorMax = new Vector2(1f, 1f);
            ruleRt.pivot = new Vector2(0.5f, 1f);
            ruleRt.anchoredPosition = new Vector2(0f, -HeaderHeight);
            ruleRt.sizeDelta = new Vector2(-16f, 1f);
            var ruleImg = ruleGo.GetComponent<Image>();
            ruleImg.color = RuleColor;
            ruleImg.raycastTarget = false;

            for (int i = 0; i < MaxGroups; i++)
                _groups[i] = BuildGroup(_panel, i);
            for (int i = 0; i < MaxCells; i++)
                _cells[i] = BuildCell(_panel, i);
        }

        /// <summary>One class header button with an ALL / OFF chip on the right.</summary>
        GroupRow BuildGroup(RectTransform parent, int index)
        {
            int captured = index;
            var group = new GroupRow();

            var go = new GameObject($"Group{index}", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(PanelWidth - PanelPad * 2f, GroupHeaderHeight);
            var img = go.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.02f);
            var btn = go.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => OnGroupClicked(captured));

            group.Root = go;
            group.Rect = rt;
            group.Button = btn;

            var kind = CreateLabel(rt, "Kind", "GUN ×1", 8f, CaptionSelected, TextAlignmentOptions.Left);
            var kindRt = kind.rectTransform;
            kindRt.anchorMin = new Vector2(0f, 0f);
            kindRt.anchorMax = new Vector2(1f, 1f);
            kindRt.offsetMin = new Vector2(2f, 0f);
            kindRt.offsetMax = new Vector2(-ChipWidth - 2f, 0f);
            kind.characterSpacing = 0.4f;
            group.KindLabel = kind;

            var chipGo = new GameObject("Chip", typeof(RectTransform), typeof(Image));
            chipGo.transform.SetParent(rt, false);
            var chipRt = chipGo.GetComponent<RectTransform>();
            chipRt.anchorMin = new Vector2(1f, 0.5f);
            chipRt.anchorMax = new Vector2(1f, 0.5f);
            chipRt.pivot = new Vector2(1f, 0.5f);
            chipRt.anchoredPosition = Vector2.zero;
            chipRt.sizeDelta = new Vector2(ChipWidth, 11f);
            var chipImg = chipGo.GetComponent<Image>();
            chipImg.color = KeycapFill;
            chipImg.raycastTarget = false;
            group.Chip = chipImg;

            var chipLabel = CreateLabel(chipRt, "ChipLabel", "ALL", 7f, ReadyColor, TextAlignmentOptions.Center);
            Stretch(chipLabel.rectTransform);
            group.ChipLabel = chipLabel;

            go.SetActive(false);
            return group;
        }

        /// <summary>
        /// One chip: dark well, inner energy fill, clockwise fire-rate border.
        /// </summary>
        BarrelCell BuildCell(RectTransform parent, int index)
        {
            int captured = index;
            var cell = new BarrelCell();

            var go = new GameObject($"Cell{index}", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(CellWidth, CellHeight);
            var img = go.GetComponent<Image>();
            img.color = CellBack;
            var btn = go.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => OnCellClicked(captured));

            cell.Root = go;
            cell.Rect = rt;
            cell.Button = btn;
            cell.Background = img;
            cell.Outline = AddFocusOutline(go);

            cell.RoFTrackTop = AddRoFEdge(rt, "RoFTrackTop", 0, filled: false);
            cell.RoFTrackRight = AddRoFEdge(rt, "RoFTrackRight", 1, filled: false);
            cell.RoFTrackBottom = AddRoFEdge(rt, "RoFTrackBottom", 2, filled: false);
            cell.RoFTrackLeft = AddRoFEdge(rt, "RoFTrackLeft", 3, filled: false);
            cell.RoFTop = AddRoFEdge(rt, "RoFTop", 0, filled: true);
            cell.RoFRight = AddRoFEdge(rt, "RoFRight", 1, filled: true);
            cell.RoFBottom = AddRoFEdge(rt, "RoFBottom", 2, filled: true);
            cell.RoFLeft = AddRoFEdge(rt, "RoFLeft", 3, filled: true);

            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(rt, false);
            var fillRt = fillGo.GetComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = new Vector2(BorderThickness, BorderThickness);
            fillRt.offsetMax = new Vector2(-BorderThickness, -BorderThickness);
            var fillImg = fillGo.GetComponent<Image>();
            fillImg.sprite = s_fillSprite;
            fillImg.type = Image.Type.Filled;
            fillImg.fillMethod = Image.FillMethod.Horizontal;
            fillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImg.fillAmount = 0f;
            fillImg.color = EnergyDim;
            fillImg.raycastTarget = false;
            cell.Fill = fillImg;

            var caretGo = new GameObject("Caret", typeof(RectTransform), typeof(Image));
            caretGo.transform.SetParent(rt, false);
            var caretRt = caretGo.GetComponent<RectTransform>();
            caretRt.anchorMin = new Vector2(0f, 0f);
            caretRt.anchorMax = new Vector2(0f, 1f);
            caretRt.pivot = new Vector2(0f, 0.5f);
            caretRt.sizeDelta = new Vector2(2f, 0f);
            caretRt.anchoredPosition = new Vector2(BorderThickness, 0f);
            caretRt.offsetMin = new Vector2(caretRt.offsetMin.x, BorderThickness);
            caretRt.offsetMax = new Vector2(caretRt.offsetMax.x, -BorderThickness);
            var caretImg = caretGo.GetComponent<Image>();
            caretImg.color = CaretColor;
            caretImg.raycastTarget = false;
            caretImg.enabled = false;
            cell.Caret = caretImg;

            go.SetActive(false);
            return cell;
        }

        /// <summary>
        /// One edge of the fire-rate ring. 0 top, 1 right, 2 bottom, 3 left.
        /// Filled edges grow clockwise.
        /// </summary>
        static Image AddRoFEdge(RectTransform parent, string name, int edge, bool filled)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            float t = BorderThickness;
            switch (edge)
            {
                case 0:
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.sizeDelta = new Vector2(0f, t);
                    rt.anchoredPosition = Vector2.zero;
                    break;
                case 1:
                    rt.anchorMin = new Vector2(1f, 0f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(1f, 0.5f);
                    rt.sizeDelta = new Vector2(t, 0f);
                    rt.anchoredPosition = Vector2.zero;
                    break;
                case 2:
                    rt.anchorMin = new Vector2(0f, 0f);
                    rt.anchorMax = new Vector2(1f, 0f);
                    rt.pivot = new Vector2(0.5f, 0f);
                    rt.sizeDelta = new Vector2(0f, t);
                    rt.anchoredPosition = Vector2.zero;
                    break;
                default:
                    rt.anchorMin = new Vector2(0f, 0f);
                    rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0f, 0.5f);
                    rt.sizeDelta = new Vector2(t, 0f);
                    rt.anchoredPosition = Vector2.zero;
                    break;
            }

            var img = go.GetComponent<Image>();
            img.sprite = s_fillSprite;
            img.raycastTarget = false;
            if (filled)
            {
                img.type = Image.Type.Filled;
                img.fillAmount = 0f;
                switch (edge)
                {
                    case 0:
                        img.fillMethod = Image.FillMethod.Horizontal;
                        img.fillOrigin = (int)Image.OriginHorizontal.Left;
                        break;
                    case 1:
                        img.fillMethod = Image.FillMethod.Vertical;
                        img.fillOrigin = (int)Image.OriginVertical.Top;
                        break;
                    case 2:
                        img.fillMethod = Image.FillMethod.Horizontal;
                        img.fillOrigin = (int)Image.OriginHorizontal.Right;
                        break;
                    default:
                        img.fillMethod = Image.FillMethod.Vertical;
                        img.fillOrigin = (int)Image.OriginVertical.Bottom;
                        break;
                }
            }
            else
            {
                img.type = Image.Type.Simple;
                img.color = RoFTrack;
            }

            return img;
        }

        static Outline AddFocusOutline(GameObject rowGo)
        {
            var outline = rowGo.AddComponent<Outline>();
            outline.effectColor = CaretColor;
            outline.effectDistance = new Vector2(1f, -1f);
            outline.useGraphicAlpha = false;
            outline.enabled = false;
            return outline;
        }

        static TextMeshProUGUI CreateLabel(
            Transform parent,
            string name,
            string text,
            float size,
            Color color,
            TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(-16f, 18f);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.fontSizeMin = 6f;
            tmp.outlineWidth = 0.18f;
            tmp.outlineColor = LabelOutline;
            ApplyHudFont(tmp);
            return tmp;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static void ApplyHudFont(TextMeshProUGUI tmp)
        {
            if (tmp == null)
                return;
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
        }

        /// <summary>1×1 white sprite so <see cref="Image.Type.Filled"/> has a source.</summary>
        static Sprite CreateWhiteSprite()
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply(false, false);
            tex.filterMode = FilterMode.Point;
            tex.hideFlags = HideFlags.HideAndDontSave;
            var sprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }
}
