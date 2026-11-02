using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

namespace AsitirTool.Editor
{
    /// <summary>K帧 Scene 面板基类：IMGUI 内容，会话有效且面板开关打开时显示，宽度可拖边调整。</summary>
    public abstract class AsiKeyPanelOverlay : Overlay, ITransientOverlay
    {
        private IMGUIContainer _gui;

        protected abstract string Panel { get; }

        protected virtual float MinWidth
        {
            get { return 270f; }
        }

        public bool visible
        {
            get { return AsiKeyPanels.Active != null && AsiKeyPanels.IsShown(Panel); }
        }

        public override void OnCreated()
        {
            minSize = new Vector2(MinWidth, 0f);
            maxSize = new Vector2(1200f, 0f);
            AsiKeySession.Repaint += OnSessionRepaint;
        }

        public override void OnWillBeDestroyed()
        {
            AsiKeySession.Repaint -= OnSessionRepaint;
        }

        private void OnSessionRepaint()
        {
            if (_gui != null)
                _gui.MarkDirtyRepaint();
        }

        public override VisualElement CreatePanelContent()
        {
            _gui = new IMGUIContainer(OnPanelGUI);
            _gui.style.minWidth = MinWidth;
            return _gui;
        }

        private void OnPanelGUI()
        {
            AsiKeySession s = AsiKeyPanels.Active;
            if (s == null)
                return;
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 96f;
            DrawPanel(s);
            EditorGUIUtility.labelWidth = labelWidth;
            if (Event.current.isMouse)
                Event.current.Use();
        }

        protected abstract void DrawPanel(AsiKeySession s);
    }

    [Overlay(typeof(SceneView), "AsiKey.Motion", "K帧 Motion", "asikey-motion", defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Top)]
    public sealed class AsiKeyMotionOverlay : AsiKeyPanelOverlay
    {
        protected override string Panel
        {
            get { return AsiKeyPanels.Motion; }
        }

        protected override void DrawPanel(AsiKeySession s)
        {
            AsiKeyPanels.DrawMotionPanel(s);
        }
    }

    [Overlay(typeof(SceneView), "AsiKey.KeyInfo", "K帧 Key Info", "asikey-keyinfo", defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Top)]
    public sealed class AsiKeyKeyInfoOverlay : AsiKeyPanelOverlay
    {
        protected override string Panel
        {
            get { return AsiKeyPanels.KeyInfo; }
        }

        protected override void DrawPanel(AsiKeySession s)
        {
            AsiKeyPanels.DrawKeyInfo(s);
        }
    }

    [Overlay(typeof(SceneView), "AsiKey.Layers", "K帧 层", "asikey-layers", defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Top)]
    public sealed class AsiKeyLayersOverlay : AsiKeyPanelOverlay
    {
        protected override string Panel
        {
            get { return AsiKeyPanels.Layers; }
        }

        protected override void DrawPanel(AsiKeySession s)
        {
            AsiKeyPanels.DrawLayersPanel(s);
        }
    }

    [Overlay(typeof(SceneView), "AsiKey.Units", "K帧 单位", "asikey-units", defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Top)]
    public sealed class AsiKeyUnitsOverlay : AsiKeyPanelOverlay
    {
        protected override string Panel
        {
            get { return AsiKeyPanels.Units; }
        }

        protected override void DrawPanel(AsiKeySession s)
        {
            AsiKeyPanels.DrawUnitsPanel(s);
        }
    }

    [Overlay(typeof(SceneView), "AsiKey.Constraints", "K帧 约束", "asikey-constraints", defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Bottom)]
    public sealed class AsiKeyConstraintsOverlay : AsiKeyPanelOverlay
    {
        protected override string Panel
        {
            get { return AsiKeyPanels.Constraints; }
        }

        protected override float MinWidth
        {
            get { return 300f; }
        }

        protected override void DrawPanel(AsiKeySession s)
        {
            AsiKeyPanels.DrawConstraintsPanel(s, false);
        }
    }

    [Overlay(typeof(SceneView), "AsiKey.Shortcuts", "K帧 快捷键", "asikey-shortcuts", defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Bottom)]
    public sealed class AsiKeyShortcutsOverlay : AsiKeyPanelOverlay
    {
        protected override string Panel
        {
            get { return AsiKeyPanels.Shortcuts; }
        }

        protected override float MinWidth
        {
            get { return 250f; }
        }

        protected override void DrawPanel(AsiKeySession s)
        {
            AsiKeyShortcuts.DrawHelp();
        }
    }

    /// <summary>
    /// 时间轴面板。停靠在 Scene 顶部 / 底部工具栏区时用横向工具栏布局，铺满工具栏区剩下的宽度；左右两侧的工具栏区照旧折叠成按钮。
    /// 浮动时「贴底」贴在 Scene 底部并铺满宽度（跟随窗口大小），切换高度时保持底边不动。
    /// 收起时高度固定，展开分轨道视图时拖动拖拽条调整高度。大小由内容决定（不用 Overlay 自带的拖边缩放），位置在 Layout 事件之外设置。
    /// </summary>
    [Overlay(typeof(SceneView), AsiKeyTimelineOverlay.Id, "K帧 时间轴", "asikey-timeline", defaultDockZone = DockZone.Floating)]
    public sealed class AsiKeyTimelineOverlay : Overlay, ITransientOverlay, ICreateHorizontalToolbar
    {
        public const string Id = "AsiKey.Timeline";
        private const float Margin = 4f;
        private const float GripHeight = 5f;
        private const float MinWidth = 480f;
        // Unity Overlay 内部的 USS 类名（UnityEditor.Overlays.OverlayCanvas / OverlayContainer）。
        private const string ToolbarAreaClass = "overlay-toolbar-area";
        private const string SpacerClass = "overlay-container__spacer";
        private static readonly int GripHint = "AsiKeyTimelineGrip".GetHashCode();

        private VisualElement _root;
        private IMGUIContainer _gui;
        private VisualElement _area;
        private bool _builtToolbar;
        private float _width = 900f;
        private Vector2 _winSize;
        private bool _fitted;
        private bool _placePending;
        private float _bottomShift;
        private float _appliedHeight;
        private float _gripStartY;
        private float _gripStartHeight;
        private bool _gripDown;

        public bool visible
        {
            get { return AsiKeyPanels.Active != null && AsiKeyPanels.IsShown(AsiKeyPanels.Timeline); }
        }

        /// <summary>这个 Scene 的时间轴是否停靠在工具栏区（此时「贴底」不起作用）。</summary>
        public static bool IsInToolbar(SceneView sv)
        {
            Overlay o;
            return sv != null && sv.TryGetOverlay(Id, out o) && o.isInToolbar;
        }

        public override void OnCreated()
        {
            AsiKeySession.Repaint += OnSessionRepaint;
            AsiKeyPanels.TimelineFitRequested += RequestFit;
            AsiKeyPanels.TimelineLayoutChanged += OnLayoutChanged;
        }

        public override void OnWillBeDestroyed()
        {
            AsiKeySession.Repaint -= OnSessionRepaint;
            AsiKeyPanels.TimelineFitRequested -= RequestFit;
            AsiKeyPanels.TimelineLayoutChanged -= OnLayoutChanged;
            SetArea(null);
        }

        private static float PanelHeight()
        {
            return AsiKeyPanels.TimelineContentHeight + (AsiKeyPanels.TimelineExpanded ? GripHeight : 0f);
        }

        private void OnLayoutChanged()
        {
            if (_root == null)
                return;
            KeepBottom(PanelHeight() - _appliedHeight);
            ApplySize();
        }

        /// <summary>浮动时高度变了：贴底就重新放到底边，否则上移同样的量保持底边不动。</summary>
        private void KeepBottom(float grow)
        {
            if (!floating)
                return;
            if (AsiKeyPanels.TimelineDocked)
                _placePending = true;
            else
                _bottomShift += grow;
        }

        private void OnSessionRepaint()
        {
            if (_gui != null)
                _gui.MarkDirtyRepaint();
        }

        public override VisualElement CreatePanelContent()
        {
            // 从工具栏区拖出来变成浮动：当作要自由摆放，不再自动贴底。
            if (_builtToolbar && floating)
                AsiKeyPanels.TimelineDocked = false;
            return BuildContent(new VisualElement(), false);
        }

        public OverlayToolbar CreateHorizontalToolbarContent()
        {
            OverlayToolbar bar = new OverlayToolbar();
            BuildContent(bar, true);
            return bar;
        }

        private VisualElement BuildContent(VisualElement root, bool toolbar)
        {
            SetArea(null);
            _builtToolbar = toolbar;
            _root = root;
            _root.style.flexDirection = FlexDirection.Column;
            _gui = new IMGUIContainer(OnPanelGUI);
            _gui.focusable = true;
            _gui.style.flexGrow = 1f;
            _gui.style.alignSelf = Align.Stretch;
            _root.Add(_gui);
            _root.RegisterCallback<GeometryChangedEvent>(OnGeometry);
            ApplySize();
            _fitted = false;
            return _root;
        }

        private void ApplySize()
        {
            if (_root == null)
                return;
            _appliedHeight = PanelHeight();
            _root.style.width = _width;
            _root.style.height = _appliedHeight;
        }

        private void OnPanelGUI()
        {
            AsiKeySession s = AsiKeyPanels.Active;
            if (s == null)
                return;
            Event e = Event.current;
            Rect full = _gui.contentRect;
            if (float.IsNaN(full.width) || full.width < 10f || full.height < 10f)
                return;
            Rect content = full;
            if (AsiKeyPanels.TimelineExpanded)
            {
                // 停靠在顶部工具栏区时向下长，拖拽条放在底边。
                if (GUIUtility.hotControl == 0)
                    _gripDown = isInToolbar && containerWindow != null && _root.worldBound.center.y < containerWindow.position.height * 0.5f;
                Rect grip = new Rect(0f, _gripDown ? full.height - GripHeight : 0f, full.width, GripHeight);
                HandleGrip(grip, e);
                content = new Rect(0f, _gripDown ? 0f : GripHeight, full.width, full.height - GripHeight);
            }
            AsiKeyPanels.TimelineInToolbar = isInToolbar;
            AsiKeyPanels.DrawTimelinePanel(s, content);
            if (e.type == EventType.Repaint)
                CheckWindow();
            if (e.isMouse)
                e.Use();
        }

        private void HandleGrip(Rect grip, Event e)
        {
            int id = GUIUtility.GetControlID(GripHint, FocusType.Passive);
            EditorGUIUtility.AddCursorRect(grip, MouseCursor.ResizeVertical);
            if (e.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(grip.x, grip.y + 1f, grip.width, 2f), GUIUtility.hotControl == id ? new Color(0.35f, 0.55f, 0.9f, 0.9f) : new Color(0f, 0f, 0f, 0.35f));
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button != 0 || !grip.Contains(e.mousePosition))
                        break;
                    GUIUtility.hotControl = id;
                    _gripStartY = GUIUtility.GUIToScreenPoint(e.mousePosition).y;
                    _gripStartHeight = AsiKeyPanels.TimelineHeight;
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id)
                        break;
                    float dy = GUIUtility.GUIToScreenPoint(e.mousePosition).y - _gripStartY;
                    SetHeight(_gripStartHeight + (_gripDown ? dy : -dy));
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id)
                        break;
                    GUIUtility.hotControl = 0;
                    e.Use();
                    break;
            }
        }

        private void SetHeight(float h)
        {
            EditorWindow w = containerWindow;
            float max = w != null ? Mathf.Max(AsiKeyPanels.MinTimelineHeight, w.position.height - 90f) : 2000f;
            h = Mathf.Clamp(Mathf.Round(h), AsiKeyPanels.MinTimelineHeight, max);
            float old = AsiKeyPanels.TimelineHeight;
            if (Mathf.Approximately(h, old))
                return;
            AsiKeyPanels.TimelineHeight = h;
            KeepBottom(h - old);
            ApplySize();
        }

        /// <summary>窗口大小变了就重新铺满（工具栏区或贴底）；首次显示也铺一次。</summary>
        private void CheckWindow()
        {
            EditorWindow w = containerWindow;
            bool toolbar = isInToolbar;
            if (w == null || !toolbar && !AsiKeyPanels.TimelineDocked)
                return;
            Vector2 size = w.position.size;
            if (_fitted && size == _winSize)
                return;
            _fitted = true;
            _winSize = size;
            if (toolbar)
                _root.schedule.Execute(FitToolbar);
            else
                RequestFit();
        }

        private void RequestFit()
        {
            EditorApplication.delayCall -= Fit;
            EditorApplication.delayCall += Fit;
        }

        private void Fit()
        {
            EditorWindow w = containerWindow;
            if (w == null || _root == null || !displayed || isInToolbar)
                return;
            if (!floating)
                Undock();
            float chrome = ChromeWidth();
            _width = Mathf.Max(MinWidth, Mathf.Floor(w.position.width - Margin * 2f - chrome - 2f));
            _placePending = true;
            ApplySize();
            Place();
        }

        /// <summary>面板边框和内边距占的宽度（还没布局时按经验值）。</summary>
        private float ChromeWidth()
        {
            Vector2 outer = size;
            float inner = _root.layout.width;
            if (float.IsNaN(outer.x) || float.IsNaN(inner) || outer.x <= 0f || inner <= 0f)
                return 12f;
            return Mathf.Max(0f, outer.x - inner);
        }

        private void Place()
        {
            if (containerWindow == null || !floating)
                return;
            // 越界的 y 会被夹到窗口底边。
            floatingPosition = new Vector2(Margin, 100000f);
        }

        private void OnGeometry(GeometryChangedEvent evt)
        {
            if (isInToolbar)
            {
                FitToolbar();
                return;
            }
            if (!floating)
                return;
            if (_placePending)
            {
                _placePending = false;
                Place();
            }
            else if (Mathf.Abs(_bottomShift) > 0.01f)
            {
                Vector2 p = floatingPosition;
                floatingPosition = new Vector2(p.x, p.y - _bottomShift);
                _bottomShift = 0f;
            }
        }

        private void OnAreaGeometry(GeometryChangedEvent evt)
        {
            if (isInToolbar)
                FitToolbar();
        }

        private void SetArea(VisualElement area)
        {
            if (_area == area)
                return;
            if (_area != null)
                _area.UnregisterCallback<GeometryChangedEvent>(OnAreaGeometry);
            _area = area;
            if (_area != null)
                _area.RegisterCallback<GeometryChangedEvent>(OnAreaGeometry);
        }

        /// <summary>
        /// 在工具栏区里铺满：工具栏区宽度（不超过窗口宽）减去同一区里其它 Overlay 和本面板边框占的宽度。
        /// 只在 GeometryChanged 或 schedule 里调用，不在 IMGUI 绘制期间改尺寸。
        /// </summary>
        private void FitToolbar()
        {
            if (_root == null || _root.panel == null || !isInToolbar)
                return;
            VisualElement area = _root.parent;
            while (area != null && !area.ClassListContains(ToolbarAreaClass))
                area = area.parent;
            SetArea(area);
            if (area == null)
                return;
            // 面板根元素挂在工具栏区自身或它的 contentContainer 下（不同 Unity 小版本不一样）。
            VisualElement item = _root;
            while (item.parent != null && item.parent != area && item.parent != area.contentContainer)
                item = item.parent;
            VisualElement row = item.parent;
            if (row == null)
                return;
            float avail = row.contentRect.width;
            EditorWindow w = containerWindow;
            if (w != null)
                avail = Mathf.Min(avail, w.position.width);
            if (float.IsNaN(avail) || avail <= 0f || float.IsNaN(item.layout.width) || float.IsNaN(_root.layout.width))
                return;
            float others = 0f;
            foreach (VisualElement c in row.Children())
            {
                if (c == item || c.ClassListContains(SpacerClass) || c.resolvedStyle.display == DisplayStyle.None || c.resolvedStyle.position == Position.Absolute)
                    continue;
                others += c.layout.width + c.resolvedStyle.marginLeft + c.resolvedStyle.marginRight;
            }
            float chrome = Mathf.Max(0f, item.layout.width + item.resolvedStyle.marginLeft + item.resolvedStyle.marginRight - _root.layout.width);
            float width = Mathf.Max(MinWidth, Mathf.Floor(avail - others - chrome - 1f));
            if (Mathf.Abs(width - _width) < 0.5f && Mathf.Abs(_root.resolvedStyle.width - width) < 0.5f)
                return;
            _width = width;
            ApplySize();
        }
    }

    // ---------------- Scene 工具栏 ----------------

    [Overlay(typeof(SceneView), "AsiKey.Toolbar", "K帧", "asikey-toolbar", defaultDockZone = DockZone.TopToolbar, defaultDockPosition = DockPosition.Top)]
    public sealed class AsiKeyToolbarOverlay : ToolbarOverlay, ITransientOverlay
    {
        public AsiKeyToolbarOverlay()
            : base(AsiKeyAutoKeyToggle.Id, AsiKeySelectFilterDropdown.Id, AsiKeyGizmoToggle.Id, AsiKeyDisplayDropdown.Id, AsiKeyPanelsDropdown.Id,
                AsiKeyHelpToggle.Id, AsiKeyCurveButton.Id, AsiKeyWindowButton.Id)
        {
        }

        public bool visible
        {
            get { return AsiKeyPanels.Active != null; }
        }
    }

    /// <summary>跟着会话刷新显示状态的工具栏开关。</summary>
    internal abstract class AsiKeyToolbarToggle : EditorToolbarToggle
    {
        protected AsiKeyToolbarToggle(string label, string tip)
        {
            text = label;
            tooltip = tip;
            this.RegisterValueChangedCallback(e => Set(e.newValue));
            RegisterCallback<AttachToPanelEvent>(e =>
            {
                AsiKeySession.Repaint += Sync;
                Sync();
            });
            RegisterCallback<DetachFromPanelEvent>(e => AsiKeySession.Repaint -= Sync);
        }

        private void Sync()
        {
            SetValueWithoutNotify(Get());
            Decorate(value);
        }

        protected abstract bool Get();

        protected abstract void Set(bool on);

        protected virtual void Decorate(bool on)
        {
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class AsiKeyAutoKeyToggle : AsiKeyToolbarToggle
    {
        public const string Id = "AsiKey/AutoKey";

        public AsiKeyAutoKeyToggle()
            : base("自动关键点", "拖动控制器后松开鼠标时自动打注册关键帧")
        {
        }

        protected override bool Get()
        {
            AsiKeySession s = AsiKeyPanels.Active;
            return s != null && s.autoKey;
        }

        protected override void Set(bool on)
        {
            AsiKeyPanels.SetAutoKey(AsiKeyPanels.Active, on);
        }

        protected override void Decorate(bool on)
        {
            style.backgroundColor = on ? new StyleColor(new Color(0.72f, 0.16f, 0.16f)) : new StyleColor(StyleKeyword.Null);
        }
    }

    /// <summary>选择筛选下拉：仅选骨骼 / 仅选工程内单位 / 仅选择某角色骨骼 / 自定义可选单位。</summary>
    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class AsiKeySelectFilterDropdown : EditorToolbarDropdown
    {
        public const string Id = "AsiKey/SelectFilter";

        public AsiKeySelectFilterDropdown()
        {
            tooltip = "Scene 里的选择筛选：开启后只点选/框选通过筛选的控制器，点空白处清空选择（Shift 加选，Ctrl 减选）";
            clicked += ShowMenu;
            RegisterCallback<AttachToPanelEvent>(e =>
            {
                AsiKeySession.Repaint += Sync;
                Sync();
            });
            RegisterCallback<DetachFromPanelEvent>(e => AsiKeySession.Repaint -= Sync);
        }

        private void Sync()
        {
            AsiKeySession s = AsiKeyPanels.Active;
            AsiSelectFilter f = AsiKeyRigDrawer.Filter;
            text = f == AsiSelectFilter.None ? "选择筛选" : AsiKeyRigDrawer.FilterLabel(s != null ? s.project : null, f);
            style.backgroundColor = f != AsiSelectFilter.None ? new StyleColor(new Color(0.22f, 0.4f, 0.62f)) : new StyleColor(StyleKeyword.Null);
        }

        private void ShowMenu()
        {
            AsiKeySession s = AsiKeyPanels.Active;
            if (s == null)
                return;
            GenericMenu menu = new GenericMenu();
            AsiKeySceneMenu.AddFilterItems(menu, s, "");
            menu.DropDown(worldBound);
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class AsiKeyGizmoToggle : AsiKeyToolbarToggle
    {
        public const string Id = "AsiKey/Gizmo";

        public AsiKeyGizmoToggle()
            : base("变换轴", "K帧变换轴（仿 Max）：旋转环、环内拖动自由旋转（单击穿透选骨骼）、最外圈屏幕旋转；移动轴、平面、中心。关闭后用 Unity 的变换手柄")
        {
        }

        protected override bool Get()
        {
            return AsiKeyGizmo.Enabled;
        }

        protected override void Set(bool on)
        {
            AsiKeyGizmo.Enabled = on;
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class AsiKeyHelpToggle : AsiKeyToolbarToggle
    {
        public const string Id = "AsiKey/Help";

        public AsiKeyHelpToggle()
            : base("快捷键", "显示/隐藏快捷键面板")
        {
        }

        protected override bool Get()
        {
            return AsiKeyPanels.IsShown(AsiKeyPanels.Shortcuts);
        }

        protected override void Set(bool on)
        {
            AsiKeyPanels.SetShown(AsiKeyPanels.Shortcuts, on);
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class AsiKeyDisplayDropdown : EditorToolbarDropdown
    {
        public const string Id = "AsiKey/Display";

        public AsiKeyDisplayDropdown()
        {
            text = "显示";
            tooltip = "透视、父子连线、运动轨迹、外形大小、骨骼样式、显隐骨骼（含隐藏所有骨骼）";
            clicked += ShowMenu;
        }

        private void ShowMenu()
        {
            AsiKeySession s = AsiKeyPanels.Active;
            if (s == null)
                return;
            GenericMenu menu = new GenericMenu();
            AsiKeySceneMenu.AddDisplayItems(menu, s, "");
            menu.DropDown(worldBound);
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class AsiKeyPanelsDropdown : EditorToolbarDropdown
    {
        public const string Id = "AsiKey/Panels";

        public AsiKeyPanelsDropdown()
        {
            text = "面板";
            tooltip = "显示/隐藏时间轴、Motion、Key Info、层、快捷键、单位、约束面板";
            clicked += ShowMenu;
        }

        private void ShowMenu()
        {
            GenericMenu menu = new GenericMenu();
            AsiKeySceneMenu.AddPanelItems(menu, "");
            menu.DropDown(worldBound);
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class AsiKeyCurveButton : EditorToolbarButton
    {
        public const string Id = "AsiKey/Curve";

        public AsiKeyCurveButton()
        {
            text = "曲线";
            tooltip = "打开曲线编辑器";
            clicked += AsiKeyCurveEditorWindow.Open;
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class AsiKeyWindowButton : EditorToolbarButton
    {
        public const string Id = "AsiKey/Window";

        public AsiKeyWindowButton()
        {
            text = "K帧窗口";
            tooltip = "打开 K帧工具窗口（工程、骨骼、IK 链、外形、烘焙/导入）";
            clicked += AsiKeyWindow.Open;
        }
    }
}
