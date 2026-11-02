using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 时间轴，布局参照 3ds Max：时间滑块、轨道条、选择范围条、播放控制条。轨道条把显示轨道的关键帧按帧合成一个色块，
    /// 各通道（质心水平/垂直/旋转、关键帧类型）的颜色从上到下叠放。展开后上方多出分轨道摄影表（缩放、平移、框选）。
    /// 两个视图共用关键帧选择、移动（Shift 复制）、复制粘贴、删除，以及 Ctrl+Alt 拖动改时间范围。
    /// </summary>
    public static partial class AsiKeyPanels
    {
        private const string ViewPrefix = "AsiKey.TL.";
        public const float TransportHeight = 22f;
        public const float SliderHeight = 18f;
        public const float BarHeight = 32f;
        public const float SpanHeight = 8f;

        /// <summary>收起时的时间轴内容高度：时间滑块 + 轨道条 + 选择范围条 + 播放控制条。</summary>
        public const float CompactHeight = SliderHeight + BarHeight + SpanHeight + TransportHeight;

        public const float MinTimelineHeight = CompactHeight + 64f;

        private static bool s_viewLoaded;
        private static float _tlFirst = -2f;
        private static float _tlPpf = 10f;
        private static bool _tlOnlySelected = true;
        private static float _tlRowScroll;
        private static float _scaleFactor = 1f;
        private static float _keysWidth = 600f;
        private static int s_expanded = -1;

        private enum Drag { None, Scrub, Move, Box, Pan, Range }
        private static Drag _drag;
        private static Vector2 _dragStart;
        private static float _moveDelta;

        private enum BarDrag { None, Scrub, Move, Box, Range, SpanStart, SpanEnd, SpanMove }
        private static BarDrag _barDrag;
        private static Vector2 _barDragStart;
        private static float _barMoveDelta;
        private static float _barGrab;
        private static Rect _barInner;

        /// <summary>按下时按住 Shift：松开时复制选中关键帧到拖动位置，原关键帧不动。</summary>
        private static bool _copyDrag;

        private enum RangeEdit { Start, End, Slide }
        private static RangeEdit _rangeEdit;
        private static int _rangeStart0;
        private static int _rangeEnd0;
        private static int _rangeGroup;
        private static float _rangeMouse0;
        private static float _rangePpf;
        private static bool s_eatContext;

        private const float SpanGrip = 6f;
        private static bool _spanShown;
        private static float _spanMin;
        private static float _spanMax;
        private static float _spanPivot;
        private static float _spanFactor = 1f;
        private static float _spanDelta;
        private static float _spanGrab;

        private struct Row
        {
            public string name;
            public AsiKeyTrack track;
            public int layer;
            /// <summary>链接约束下标。普通轨道为 -1；没有影响度轨道的链接约束用它单独占一行。</summary>
            public int link;
        }

        private static readonly List<Row> _rows = new List<Row>();
        private static readonly List<AsiKeyTrack> _rowTracks = new List<AsiKeyTrack>();
        private static readonly Comparison<AsiKeyTrack> s_trackOrder = (a, b) => TrackOrder(a).CompareTo(TrackOrder(b));

        /// <summary>同一帧上的关键帧：通道位掩码（AsiKeyLabels.Channel 顺序）。</summary>
        private struct KeyGroup
        {
            public float frame;
            public int mask;
            public bool selected;
        }

        private static readonly Dictionary<int, KeyGroup> s_groups = new Dictionary<int, KeyGroup>();
        private static readonly List<AsiKey> s_groupKeys = new List<AsiKey>();

        private struct CopiedKey
        {
            public AsiKeyTrack track;
            public int layer;
            public AsiKey key;
        }

        private static readonly List<CopiedKey> s_copied = new List<CopiedKey>();
        private static float s_copiedMin;

        private const float NameWidth = 170f;
        private const float RulerHeight = 18f;
        private const float RowHeight = 17f;
        private const float Gutter = 22f;
        private const float ThumbWidth = 60f;
        private const float KeyZone = 19f;
        private static readonly int TimelineHint = "AsiKeyTimeline".GetHashCode();
        private static readonly int BarHint = "AsiKeyTrackBar".GetHashCode();
        private static GUIStyle s_thumbStyle;
        private static GUIStyle s_tickStyle;

        private const string LegendTip =
            "轨道条：选中控制器在当前层的关键帧（没选中或关掉「仅选中」时显示全部）。\n" +
            "同一帧的各通道叠成一个色块，从上到下：\n" +
            "品红 质心水平　青 质心垂直　绿 质心旋转\n" +
            "灰 注册　橙 固定　黄 滑动　蓝 自由　紫 约束影响度　琥珀 链接关键帧　浅黄 层权重\n" +
            "链接关键帧是跟某个对象的相对绑定，可以拖动；拖动只改帧号，相对位置和角度不变。\n" +
            "白框表示选中。按住 Shift 拖动选中关键帧是复制。\n" +
            "下方蓝条：选中关键帧的时间范围，拖两端缩放、拖中间移动（Shift 复制）。\n" +
            "Ctrl+Alt+左键/右键/中键拖动：改起始帧/结束帧/平移整个时间范围（像抓住时间轴拖，向右拖帧号变小）。";

        /// <summary>展开时的时间轴内容高度（不含面板标题栏和顶边拖动条）。</summary>
        public static float TimelineHeight
        {
            get { return Mathf.Max(MinTimelineHeight, EditorPrefs.GetFloat(ViewPrefix + "Height", 230f)); }
            set { EditorPrefs.SetFloat(ViewPrefix + "Height", Mathf.Max(MinTimelineHeight, value)); }
        }

        /// <summary>展开分轨道摄影表（相当于 Max 轨道条左端的迷你曲线编辑器）。</summary>
        public static bool TimelineExpanded
        {
            get
            {
                if (s_expanded < 0)
                    s_expanded = EditorPrefs.GetBool(ViewPrefix + "Expanded", false) ? 1 : 0;
                return s_expanded == 1;
            }
            set
            {
                if (value == TimelineExpanded)
                    return;
                s_expanded = value ? 1 : 0;
                EditorPrefs.SetBool(ViewPrefix + "Expanded", value);
                if (TimelineLayoutChanged != null)
                    TimelineLayoutChanged();
                Repaint();
            }
        }

        /// <summary>时间轴内容高度：收起时固定，展开时可拖动顶边调整。</summary>
        public static float TimelineContentHeight
        {
            get { return TimelineExpanded ? TimelineHeight : CompactHeight; }
        }

        /// <summary>正在绘制的时间轴停靠在 Scene 工具栏区（由时间轴 Overlay 绘制前设置）。</summary>
        public static bool TimelineInToolbar { get; set; }

        /// <summary>浮动的时间轴贴在 Scene 底部并铺满宽度。停靠在工具栏区时不起作用。</summary>
        public static bool TimelineDocked
        {
            get { return EditorPrefs.GetBool(ViewPrefix + "Docked", true); }
            set
            {
                if (value == TimelineDocked)
                    return;
                EditorPrefs.SetBool(ViewPrefix + "Docked", value);
                if (value && TimelineFitRequested != null)
                    TimelineFitRequested();
            }
        }

        /// <summary>请求时间轴面板重新贴底铺满。</summary>
        public static event Action TimelineFitRequested;

        /// <summary>时间轴内容高度变了（展开/收起）。</summary>
        public static event Action TimelineLayoutChanged;

        public static void RequestTimelineFit()
        {
            if (TimelineFitRequested != null)
                TimelineFitRequested();
        }

        private static void LoadView()
        {
            if (s_viewLoaded)
                return;
            s_viewLoaded = true;
            _tlFirst = SessionState.GetFloat(ViewPrefix + "First", -2f);
            _tlPpf = SessionState.GetFloat(ViewPrefix + "Ppf", 10f);
            _tlOnlySelected = EditorPrefs.GetBool(ViewPrefix + "OnlySelected", true);
        }

        private static void SaveView()
        {
            SessionState.SetFloat(ViewPrefix + "First", _tlFirst);
            SessionState.SetFloat(ViewPrefix + "Ppf", _tlPpf);
        }

        private static void SetOnlySelected(bool on)
        {
            _tlOnlySelected = on;
            EditorPrefs.SetBool(ViewPrefix + "OnlySelected", on);
            Repaint();
        }

        /// <summary>
        /// 绘制时间轴。r 是面板内容区，底部依次是时间滑块、轨道条、选择范围条、播放控制条，展开时上方是分轨道摄影表。
        /// </summary>
        public static void DrawTimelinePanel(AsiKeySession s, Rect r)
        {
            if (!Begin(s))
                return;
            LoadView();
            BuildRows(s);
            CollectGroups();
            ComputeSelectionSpan();
            bool expanded = TimelineExpanded;
            float y = r.yMax - CompactHeight;
            if (expanded && y - r.y > 20f)
                DrawTimeline(new Rect(r.x, r.y, r.width, y - r.y), s);
            Rect slider = new Rect(r.x, y, r.width, SliderHeight);
            Rect bar = new Rect(r.x, slider.yMax, r.width, BarHeight);
            Rect span = new Rect(r.x, bar.yMax, r.width, SpanHeight);
            DrawTrackBar(slider, bar, span, s, expanded);
            DrawTransport(new Rect(r.x, span.yMax, r.width, TransportHeight), s, expanded);
        }

        // ---------------- 播放控制 ----------------

        private static void DrawTransport(Rect r, AsiKeySession s, bool expanded)
        {
            GUILayout.BeginArea(r, EditorStyles.toolbar);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("|◀", EditorStyles.toolbarButton, GUILayout.Width(26f)))
                s.GoTo(_project.startFrame);
            if (GUILayout.Button(new GUIContent("◀◆", "上一关键帧（" + AsiKeyShortcuts.Binding(AsiKeyShortcuts.PrevKey) + "）"), EditorStyles.toolbarButton, GUILayout.Width(30f)))
                s.JumpKey(-1);
            if (GUILayout.Button(new GUIContent("◀", "上一帧（" + AsiKeyShortcuts.Binding(AsiKeyShortcuts.PrevFrame) + "）"), EditorStyles.toolbarButton, GUILayout.Width(24f)))
                s.GoTo(Mathf.Round(s.frame) - 1f);
            if (GUILayout.Button(new GUIContent(s.playing ? "■" : "▶", "播放/暂停（" + AsiKeyShortcuts.Binding(AsiKeyShortcuts.Play) + "）"), EditorStyles.toolbarButton, GUILayout.Width(28f)))
                s.TogglePlay();
            if (GUILayout.Button(new GUIContent("▶", "下一帧（" + AsiKeyShortcuts.Binding(AsiKeyShortcuts.NextFrame) + "）"), EditorStyles.toolbarButton, GUILayout.Width(24f)))
                s.GoTo(Mathf.Round(s.frame) + 1f);
            if (GUILayout.Button(new GUIContent("◆▶", "下一关键帧（" + AsiKeyShortcuts.Binding(AsiKeyShortcuts.NextKey) + "）"), EditorStyles.toolbarButton, GUILayout.Width(30f)))
                s.JumpKey(1);
            if (GUILayout.Button("▶|", EditorStyles.toolbarButton, GUILayout.Width(26f)))
                s.GoTo(_project.endFrame);
            s.loop = GUILayout.Toggle(s.loop, "循环", EditorStyles.toolbarButton, GUILayout.Width(36f));

            GUILayout.Space(8f);
            GUILayout.Label("帧", GUILayout.Width(14f));
            EditorGUI.BeginChangeCheck();
            float f = EditorGUILayout.FloatField(s.frame, EditorStyles.toolbarTextField, GUILayout.Width(48f));
            if (EditorGUI.EndChangeCheck())
                s.GoTo(f);
            GUILayout.Label("范围", GUILayout.Width(28f));
            EditorGUI.BeginChangeCheck();
            int start = EditorGUILayout.IntField(_project.startFrame, EditorStyles.toolbarTextField, GUILayout.Width(40f));
            int end = EditorGUILayout.IntField(_project.endFrame, EditorStyles.toolbarTextField, GUILayout.Width(40f));
            GUILayout.Label("帧率", GUILayout.Width(28f));
            float fps = EditorGUILayout.FloatField(_project.frameRate, EditorStyles.toolbarTextField, GUILayout.Width(36f));
            if (EditorGUI.EndChangeCheck())
            {
                Mutate("时间范围", () =>
                {
                    _project.startFrame = start;
                    _project.endFrame = Mathf.Max(start + 1, end);
                    _project.frameRate = Mathf.Clamp(fps, 1f, 240f);
                });
            }

            GUILayout.Space(8f);
            EditorGUI.BeginChangeCheck();
            bool only = GUILayout.Toggle(_tlOnlySelected, new GUIContent("仅选中", "只显示选中控制器的关键帧；没选中时显示全部"), EditorStyles.toolbarButton, GUILayout.Width(44f));
            if (EditorGUI.EndChangeCheck())
                SetOnlySelected(only);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(_selKeys.Count == 0))
            {
                GUILayout.Label("缩放所选", GUILayout.Width(50f));
                _scaleFactor = EditorGUILayout.FloatField(_scaleFactor, EditorStyles.toolbarTextField, GUILayout.Width(36f));
                if (GUILayout.Button(new GUIContent("缩放", "以当前帧为中心缩放选中关键帧的时间"), EditorStyles.toolbarButton, GUILayout.Width(36f)))
                    ScaleSelected(s, _scaleFactor);
            }
            if (expanded && GUILayout.Button(new GUIContent("全部显示", "分轨道视图显示整个时间范围（F）"), EditorStyles.toolbarButton, GUILayout.Width(56f)))
                FrameAll();
            if (TimelineInToolbar)
            {
                using (new EditorGUI.DisabledScope(true))
                    GUILayout.Toggle(false, new GUIContent("贴底", "已停靠在 Scene 工具栏区，宽度跟随工具栏区；拖出工具栏区变成浮动后才可用"), EditorStyles.toolbarButton, GUILayout.Width(36f));
            }
            else
            {
                TimelineDocked = GUILayout.Toggle(TimelineDocked, new GUIContent("贴底", "浮动时贴在 Scene 底部并铺满宽度；关闭后可以拖动面板标题栏自由摆放。也可以把面板拖进 Scene 底部的工具栏区停靠"), EditorStyles.toolbarButton, GUILayout.Width(36f));
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // ---------------- 显示的轨道 ----------------

        /// <summary>选中控制器的轨道（质心总是显示水平、垂直、旋转三条），或当前层全部有关键帧的轨道。</summary>
        private static void BuildRows(AsiKeySession s)
        {
            _rows.Clear();
            _rowTracks.Clear();
            int layer = _project.activeLayer;
            List<int> sel = s.SelectedBones();
            if (_tlOnlySelected && sel.Count > 0)
            {
                for (int k = 0; k < sel.Count; k++)
                {
                    int i = sel[k];
                    if (i < 0 || i >= _project.bones.Count)
                        continue;
                    if (_project.bones[i].isCom)
                    {
                        AddRowTrack(layer, AsiTrackKind.ComHorizontal, i);
                        AddRowTrack(layer, AsiTrackKind.ComVertical, i);
                        AddRowTrack(layer, AsiTrackKind.ComRotation, i);
                        continue;
                    }
                    int c = _project.ChainOf(i);
                    if (c < 0)
                        c = AsiKeyFingers.TipChain(_project, i);
                    if (c >= 0)
                        AddRowTrack(layer, AsiTrackKind.Chain, c);
                    else
                        AddRowTrack(layer, AsiTrackKind.Bone, i);
                }
                for (int k = 0; k < _project.constraints.Count; k++)
                {
                    if (sel.Contains(_project.constraints[k].bone))
                        AddRowTrack(layer, AsiTrackKind.Constraint, k);
                }
            }
            else
            {
                List<AsiKeyTrack> tracks = _project.layers[layer].tracks;
                for (int t = 0; t < tracks.Count; t++)
                {
                    if (tracks[t].keys.Count == 0 || tracks[t].kind == AsiTrackKind.LayerWeight)
                        continue;
                    _rowTracks.Add(tracks[t]);
                }
            }
            _rowTracks.Sort(s_trackOrder);
            AddWeightRows();
            for (int t = 0; t < _rowTracks.Count; t++)
            {
                Row row;
                row.name = _project.TrackName(_rowTracks[t]);
                row.track = _rowTracks[t];
                row.layer = _rowTracks[t].kind == AsiTrackKind.LayerWeight ? _rowTracks[t].target : layer;
                row.link = -1;
                _rows.Add(row);
            }
            AddLinkRows(_tlOnlySelected && sel.Count > 0 ? sel : null);
        }

        /// <summary>打开了「时间轴」的叠加层，把权重关键帧排进行里。</summary>
        private static void AddWeightRows()
        {
            for (int l = 1; l < _project.layers.Count; l++)
            {
                if (!_project.layers[l].showWeight)
                    continue;
                AsiKeyTrack tr = _project.FindTrack(l, AsiTrackKind.LayerWeight, l);
                if (tr != null && !_rowTracks.Contains(tr))
                    _rowTracks.Add(tr);
            }
        }

        /// <summary>没有影响度关键帧的链接约束也占一行，好让链接关键帧能画出来、能拖。</summary>
        private static void AddLinkRows(List<int> onlyBones)
        {
            for (int k = 0; k < _project.constraints.Count; k++)
            {
                AsiKeyConstraint c = _project.constraints[k];
                if (c.kind != AsiConstraintKind.Link || c.links.Count == 0)
                    continue;
                if (onlyBones != null && !onlyBones.Contains(c.bone))
                    continue;
                bool listed = false;
                for (int r = 0; r < _rows.Count; r++)
                {
                    AsiKeyTrack tr = _rows[r].track;
                    if (tr != null && tr.kind == AsiTrackKind.Constraint && tr.target == k)
                    {
                        listed = true;
                        break;
                    }
                }
                if (listed)
                    continue;
                Row row;
                row.name = string.IsNullOrEmpty(c.name) ? "链接" : c.name;
                row.track = null;
                row.layer = _project.activeLayer;
                row.link = k;
                _rows.Add(row);
            }
        }

        private static void AddRowTrack(int layer, AsiTrackKind kind, int target)
        {
            AsiKeyTrack t = _project.FindTrack(layer, kind, target);
            if (t != null && !_rowTracks.Contains(t))
                _rowTracks.Add(t);
        }

        private static int TrackOrder(AsiKeyTrack t)
        {
            switch (t.kind)
            {
                case AsiTrackKind.ComHorizontal: return t.target * 4;
                case AsiTrackKind.ComVertical: return t.target * 4 + 1;
                case AsiTrackKind.ComRotation: return t.target * 4 + 2;
                case AsiTrackKind.Chain:
                    return t.target >= 0 && t.target < _project.chains.Count ? _project.chains[t.target].Root * 4 : 0;
                case AsiTrackKind.Constraint:
                    return t.target >= 0 && t.target < _project.constraints.Count ? _project.constraints[t.target].bone * 4 + 3 : int.MaxValue;
                case AsiTrackKind.LayerWeight:
                    return 100000 + t.target;
                default:
                    return t.target * 4 + 1;
            }
        }

        private static int GroupId(float frame)
        {
            return Mathf.RoundToInt(frame * 100f);
        }

        /// <summary>把显示轨道的关键帧按帧合并（拖动中按拖动后的位置；复制拖动时原位置不选中，拖动位置显示复制出的选中色块）。</summary>
        private static void CollectGroups()
        {
            s_groups.Clear();
            bool copying = CopyDragging;
            for (int t = 0; t < _rowTracks.Count; t++)
            {
                AsiKeyTrack tr = _rowTracks[t];
                for (int k = 0; k < tr.keys.Count; k++)
                {
                    AsiKey key = tr.keys[k];
                    int bit = 1 << AsiKeyLabels.ChannelOf(tr, key);
                    bool sel = _selKeys.Contains(key);
                    AddGroup(DisplayFrame(key), bit, sel && !copying);
                    if (sel && copying)
                        AddGroup(DragFrame(key.frame), bit, true);
                }
            }
        }

        private static void AddGroup(float f, int bit, bool selected)
        {
            int id = GroupId(f);
            KeyGroup g;
            if (!s_groups.TryGetValue(id, out g))
            {
                g.frame = f;
                g.mask = 0;
                g.selected = false;
            }
            g.mask |= bit;
            g.selected |= selected;
            s_groups[id] = g;
        }

        /// <summary>全部层里选中关键帧的时间范围；跨两个以上不同时间时显示选择范围条。</summary>
        private static void ComputeSelectionSpan()
        {
            _spanShown = false;
            if (_selKeys.Count < 2)
                return;
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int l = 0; l < _project.layers.Count; l++)
            {
                List<AsiKeyTrack> tracks = _project.layers[l].tracks;
                for (int t = 0; t < tracks.Count; t++)
                {
                    List<AsiKey> keys = tracks[t].keys;
                    for (int k = 0; k < keys.Count; k++)
                    {
                        if (!_selKeys.Contains(keys[k]))
                            continue;
                        min = Mathf.Min(min, keys[k].frame);
                        max = Mathf.Max(max, keys[k].frame);
                    }
                }
            }
            if (max - min < 0.01f)
                return;
            _spanMin = min;
            _spanMax = max;
            _spanShown = true;
        }

        private static void GroupKeys(int id, List<AsiKey> result)
        {
            result.Clear();
            for (int t = 0; t < _rowTracks.Count; t++)
            {
                List<AsiKey> keys = _rowTracks[t].keys;
                for (int k = 0; k < keys.Count; k++)
                {
                    if (GroupId(keys[k].frame) == id)
                        result.Add(keys[k]);
                }
            }
        }

        /// <summary>一帧的关键帧色块：每个通道一条横带，按通道顺序从上到下。</summary>
        private static void DrawKeyStack(float x, float y, float w, float h, int mask, bool selected)
        {
            int n = 0;
            for (int c = 0; c < AsiKeyLabels.ChannelCount; c++)
            {
                if ((mask & (1 << c)) != 0)
                    n++;
            }
            if (n == 0)
                return;
            Rect box = new Rect(Mathf.Round(x - w * 0.5f), Mathf.Round(y), w, Mathf.Round(h));
            EditorGUI.DrawRect(new Rect(box.x - 1f, box.y - 1f, box.width + 2f, box.height + 2f), selected ? Color.white : new Color(0f, 0f, 0f, 0.75f));
            float top = box.y;
            int band = 0;
            for (int c = 0; c < AsiKeyLabels.ChannelCount; c++)
            {
                if ((mask & (1 << c)) == 0)
                    continue;
                band++;
                float bottom = band == n ? box.yMax : Mathf.Round(box.y + box.height * band / n);
                Color col = AsiKeyLabels.ChannelColor(c);
                EditorGUI.DrawRect(new Rect(box.x, top, box.width, bottom - top), selected ? Color.Lerp(col, Color.white, 0.35f) : col);
                top = bottom;
            }
        }

        private static bool SpanDragging
        {
            get { return _barDrag == BarDrag.SpanStart || _barDrag == BarDrag.SpanEnd || _barDrag == BarDrag.SpanMove; }
        }

        private static bool CopyDragging
        {
            get { return _copyDrag && (_drag == Drag.Move || _barDrag == BarDrag.Move || SpanDragging); }
        }

        /// <summary>选中关键帧拖动后的帧位置（没在拖动时原样返回）。</summary>
        private static float DragFrame(float f)
        {
            if (_drag == Drag.Move)
                return f + _moveDelta;
            if (_barDrag == BarDrag.Move)
                return f + _barMoveDelta;
            if (SpanDragging)
                return _spanPivot + (f - _spanPivot) * _spanFactor + _spanDelta;
            return f;
        }

        private static float DisplayFrame(AsiKey k)
        {
            if (!_selKeys.Contains(k) || CopyDragging)
                return k.frame;
            return DragFrame(k.frame);
        }

        private static float TickStepFor(float ppf)
        {
            float[] steps = { 1f, 2f, 5f, 10f, 20f, 50f, 100f, 200f, 500f };
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] * ppf >= 40f)
                    return steps[i];
            }
            return 1000f;
        }

        private static string FrameText(float f)
        {
            float r = Mathf.Round(f);
            return Mathf.Abs(f - r) < 0.005f ? ((int)r).ToString() : f.ToString("0.0");
        }

        // ---------------- 时间滑块与轨道条 ----------------

        private static GUIStyle ThumbStyle
        {
            get
            {
                if (s_thumbStyle == null)
                {
                    s_thumbStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                    s_thumbStyle.alignment = TextAnchor.MiddleCenter;
                    s_thumbStyle.padding = new RectOffset(0, 0, 0, 0);
                    s_thumbStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
                }
                return s_thumbStyle;
            }
        }

        private static GUIStyle TickStyle
        {
            get
            {
                if (s_tickStyle == null)
                {
                    s_tickStyle = new GUIStyle(EditorStyles.miniLabel);
                    s_tickStyle.alignment = TextAnchor.UpperLeft;
                    s_tickStyle.padding = new RectOffset(0, 0, 0, 0);
                    s_tickStyle.normal.textColor = new Color(0.68f, 0.68f, 0.68f);
                }
                return s_tickStyle;
            }
        }

        private static float BarPpf
        {
            get { return _barInner.width / Mathf.Max(1f, _project.endFrame - _project.startFrame); }
        }

        private static float BarX(float f)
        {
            return _barInner.x + (f - _project.startFrame) * BarPpf;
        }

        private static float BarFrame(float x)
        {
            return _project.startFrame + (x - _barInner.x) / BarPpf;
        }

        private static float BarKeyWidth
        {
            get { return Mathf.Clamp(Mathf.Floor(BarPpf * 0.6f), 4f, 10f); }
        }

        /// <summary>
        /// 时间滑块（拖动滑块或点轨道跳帧）、下方的轨道条和选择范围条，三者帧位置对齐，始终显示整个时间范围。
        /// </summary>
        private static void DrawTrackBar(Rect slider, Rect bar, Rect spanStrip, AsiKeySession s, bool expanded)
        {
            Event e = Event.current;
            Rect track = new Rect(slider.x + Gutter, slider.y, slider.width - Gutter * 2f, slider.height);
            Rect keys = new Rect(bar.x + Gutter, bar.y, bar.width - Gutter * 2f, bar.height);
            Rect span = new Rect(spanStrip.x + Gutter, spanStrip.y, spanStrip.width - Gutter * 2f, spanStrip.height);
            _barInner = new Rect(track.x + ThumbWidth * 0.5f, bar.y, Mathf.Max(10f, track.width - ThumbWidth), bar.height);

            if (GUI.Button(new Rect(slider.x + 1f, slider.y + 1f, Gutter - 2f, slider.height - 2f),
                    new GUIContent("<", "上一帧（" + AsiKeyShortcuts.Binding(AsiKeyShortcuts.PrevFrame) + "）"), EditorStyles.miniButton))
                s.GoTo(Mathf.Round(s.frame) - 1f);
            if (GUI.Button(new Rect(track.xMax + 1f, slider.y + 1f, Gutter - 2f, slider.height - 2f),
                    new GUIContent(">", "下一帧（" + AsiKeyShortcuts.Binding(AsiKeyShortcuts.NextFrame) + "）"), EditorStyles.miniButton))
                s.GoTo(Mathf.Round(s.frame) + 1f);
            if (GUI.Button(new Rect(bar.x + 1f, bar.y + 6f, Gutter - 2f, 18f),
                    new GUIContent(expanded ? "▼" : "▲", expanded ? "收起分轨道视图" : "展开分轨道视图：每条轨道一行，可以缩放、平移、框选"), EditorStyles.miniButton))
                TimelineExpanded = !expanded;
            GUI.Label(new Rect(keys.xMax, bar.y + 6f, Gutter, 18f), new GUIContent("?", LegendTip), EditorStyles.centeredGreyMiniLabel);

            int id = GUIUtility.GetControlID(BarHint, FocusType.Keyboard);
            if (e.type == EventType.Repaint)
            {
                DrawSlider(track, s, GUIUtility.hotControl == id && _barDrag == BarDrag.Scrub);
                DrawBarKeys(keys, s, GUIUtility.keyboardControl == id);
                DrawSpanBar(span);
            }
            if (_spanShown && !SpanDragging)
            {
                float x0 = BarX(_spanMin);
                float x1 = BarX(_spanMax);
                EditorGUIUtility.AddCursorRect(new Rect(x0 - SpanGrip, span.y, SpanGrip * 2f, span.height), MouseCursor.ResizeHorizontal);
                EditorGUIUtility.AddCursorRect(new Rect(x1 - SpanGrip, span.y, SpanGrip * 2f, span.height), MouseCursor.ResizeHorizontal);
            }
            HandleBarInput(id, track, keys, span, s);
        }

        private static void DrawSpanBar(Rect span)
        {
            EditorGUI.DrawRect(span, new Color(0.12f, 0.12f, 0.12f));
            if (!_spanShown)
                return;
            float x0 = Mathf.Round(BarX(DragFrame(_spanMin)));
            float x1 = Mathf.Round(BarX(DragFrame(_spanMax)));
            if (x1 < x0)
            {
                float t = x0;
                x0 = x1;
                x1 = t;
            }
            bool active = SpanDragging;
            EditorGUI.DrawRect(new Rect(x0, span.y + 1f, Mathf.Max(1f, x1 - x0), span.height - 2f),
                active ? new Color(0.5f, 0.7f, 1f, 0.95f) : new Color(0.36f, 0.5f, 0.78f, 0.9f));
            Color grip = new Color(0.92f, 0.92f, 0.92f);
            EditorGUI.DrawRect(new Rect(x0 - 2f, span.y, 4f, span.height), grip);
            EditorGUI.DrawRect(new Rect(x1 - 2f, span.y, 4f, span.height), grip);
        }

        /// <summary>拖动时在轨道条中间显示的提示（时间范围、选中范围）。</summary>
        private static void DrawBarHint(Rect keys, string text)
        {
            GUIContent c = new GUIContent(text);
            float w = ThumbStyle.CalcSize(c).x + 10f;
            Rect box = new Rect(Mathf.Round(keys.center.x - w * 0.5f), keys.y + 1f, w, KeyZone);
            EditorGUI.DrawRect(box, new Color(0f, 0f, 0f, 0.8f));
            GUI.Label(box, c, ThumbStyle);
        }

        private static void DrawSlider(Rect track, AsiKeySession s, bool hot)
        {
            EditorGUI.DrawRect(track, new Color(0.13f, 0.13f, 0.13f));
            float x = BarX(Mathf.Clamp(s.frame, _project.startFrame, _project.endFrame));
            Rect thumb = new Rect(Mathf.Round(x - ThumbWidth * 0.5f), track.y + 1f, ThumbWidth, track.height - 2f);
            EditorGUI.DrawRect(thumb, hot ? new Color(0.3f, 0.48f, 0.8f) : new Color(0.4f, 0.4f, 0.4f));
            EditorGUI.DrawRect(new Rect(thumb.x, thumb.y, thumb.width, 1f), new Color(1f, 1f, 1f, 0.18f));
            GUI.Label(thumb, FrameText(s.frame) + " / " + _project.endFrame, ThumbStyle);
        }

        private static void DrawBarKeys(Rect keys, AsiKeySession s, bool focused)
        {
            EditorGUI.DrawRect(keys, new Color(0.15f, 0.15f, 0.15f));
            Rect zone = new Rect(keys.x, keys.y + 1f, keys.width, KeyZone);
            EditorGUI.DrawRect(zone, focused ? new Color(0.24f, 0.25f, 0.28f) : new Color(0.22f, 0.22f, 0.22f));

            float ppf = BarPpf;
            int start = _project.startFrame;
            int end = _project.endFrame;
            float ruleY = zone.yMax;
            if (ppf >= 4f)
            {
                for (int f = start; f <= end; f++)
                    EditorGUI.DrawRect(new Rect(Mathf.Round(BarX(f)), ruleY, 1f, 3f), new Color(1f, 1f, 1f, 0.16f));
            }
            float step = TickStepFor(ppf);
            for (float f = Mathf.Ceil(start / step) * step; f <= end + 0.001f; f += step)
            {
                float x = Mathf.Round(BarX(f));
                EditorGUI.DrawRect(new Rect(x, ruleY, 1f, 6f), new Color(1f, 1f, 1f, 0.45f));
                GUI.Label(new Rect(x + 2f, ruleY, 40f, keys.yMax - ruleY), f.ToString("0"), TickStyle);
            }

            float cx = BarX(s.frame);
            if (cx >= keys.x && cx <= keys.xMax)
            {
                float w = Mathf.Max(2f, ppf);
                EditorGUI.DrawRect(new Rect(Mathf.Round(cx - w * 0.5f), zone.y, w, zone.height), new Color(1f, 1f, 1f, 0.1f));
                EditorGUI.DrawRect(new Rect(Mathf.Round(cx), zone.yMax, 1f, keys.yMax - zone.yMax), new Color(1f, 0.3f, 0.3f, 0.8f));
            }

            float kw = BarKeyWidth;
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (KeyGroup g in s_groups.Values)
                {
                    if (g.selected != (pass == 1))
                        continue;
                    float x = BarX(g.frame);
                    if (x < keys.x || x > keys.xMax)
                        continue;
                    DrawKeyStack(x, zone.y + 2f, kw, zone.height - 4f, g.mask, g.selected);
                }
            }
            DrawBarLinks(s, keys, zone);

            if (_barDrag == BarDrag.Box)
            {
                float a = _barDragStart.x;
                float b = Event.current.mousePosition.x;
                EditorGUI.DrawRect(Rect.MinMaxRect(Mathf.Min(a, b), keys.y, Mathf.Max(a, b), keys.yMax), new Color(0.4f, 0.6f, 1f, 0.18f));
            }
            if (_barDrag == BarDrag.Range || _drag == Drag.Range)
                DrawBarHint(keys, "时间范围 " + start + " – " + end + "（" + (end - start) + " 帧）");
            else if (SpanDragging)
                DrawBarHint(keys, (_copyDrag ? "复制到 " : "选中 ") + FrameText(DragFrame(_spanMin)) + " – " + FrameText(DragFrame(_spanMax)));
            else if (CopyDragging)
                DrawBarHint(keys, "复制 " + (DragFrame(0f) >= 0f ? "+" : "") + FrameText(DragFrame(0f)) + " 帧");
        }

        private static bool HitGroup(float mx, out int id)
        {
            id = 0;
            bool found = false;
            float best = Mathf.Max(5f, BarKeyWidth * 0.5f + 2f);
            foreach (KeyValuePair<int, KeyGroup> kv in s_groups)
            {
                float d = Mathf.Abs(BarX(kv.Value.frame) - mx);
                if (d < best)
                {
                    best = d;
                    id = kv.Key;
                    found = true;
                }
            }
            return found;
        }

        private static bool AllSelected(List<AsiKey> keys)
        {
            for (int k = 0; k < keys.Count; k++)
            {
                if (!_selKeys.Contains(keys[k]))
                    return false;
            }
            return keys.Count > 0;
        }

        private static void HandleBarInput(int id, Rect track, Rect keys, Rect span, AsiKeySession s)
        {
            Event e = Event.current;
            Vector2 m = e.mousePosition;
            if (GUIUtility.keyboardControl == id && HandleTimelineKeyboard(s, e))
                return;
            EventType type = e.GetTypeForControl(id);
            if (e.rawType == EventType.MouseUp && _barDrag != BarDrag.None)
            {
                type = EventType.MouseUp;
                if (GUIUtility.hotControl == id)
                    GUIUtility.hotControl = 0;
            }
            switch (type)
            {
                case EventType.MouseDown:
                {
                    bool onSlider = track.Contains(m);
                    bool onSpan = span.Contains(m);
                    if (!onSlider && !onSpan && !keys.Contains(m))
                        return;
                    GUI.FocusControl(null);
                    GUIUtility.keyboardControl = id;
                    if (RangeModifier(e))
                    {
                        BeginRange(e.button, m.x, BarPpf);
                        _barDrag = BarDrag.Range;
                        GUIUtility.hotControl = id;
                        e.Use();
                        break;
                    }
                    if (onSpan)
                    {
                        if (e.button != 0 || !BeginSpanDrag(m.x, e.shift))
                            return;
                        GUIUtility.hotControl = id;
                        e.Use();
                        Repaint();
                        break;
                    }
                    if (onSlider)
                    {
                        if (e.button != 0)
                            return;
                        _barDrag = BarDrag.Scrub;
                        GUIUtility.hotControl = id;
                        float thumbX = BarX(Mathf.Clamp(s.frame, _project.startFrame, _project.endFrame));
                        _barGrab = Mathf.Abs(m.x - thumbX) <= ThumbWidth * 0.5f ? m.x - thumbX : 0f;
                        s.GoTo(Mathf.Round(BarFrame(m.x - _barGrab)));
                        e.Use();
                        break;
                    }
                    AsiLinkEntry barLink;
                    bool onBarLink = HitBarLink(m.x, out barLink);
                    int group = 0;
                    bool onKey = !onBarLink && HitGroup(m.x, out group);
                    if (onKey)
                        GroupKeys(group, s_groupKeys);
                    if (e.button == 1)
                    {
                        if (onBarLink && !_selLinks.Contains(barLink))
                            SelectLink(barLink, false, false);
                        else if (onKey && !AllSelected(s_groupKeys))
                        {
                            _selLinks.Clear();
                            _selKeys.Clear();
                            _selKeys.UnionWith(s_groupKeys);
                        }
                        ShowTimelineMenu(s, onKey ? group / 100f : BarFrame(m.x));
                        e.Use();
                        break;
                    }
                    if (e.button != 0)
                        return;
                    bool ctrl = e.control || e.command;
                    if (onBarLink)
                    {
                        SelectLink(barLink, e.shift, ctrl);
                        if (e.clickCount == 2)
                            s.GoTo(barLink.frame);
                        _barDrag = BarDrag.Move;
                        _barDragStart = m;
                        _barMoveDelta = 0f;
                        _copyDrag = e.shift;
                    }
                    else if (onKey)
                    {
                        bool all = AllSelected(s_groupKeys);
                        if (ctrl)
                        {
                            if (all)
                                _selKeys.ExceptWith(s_groupKeys);
                            else
                                _selKeys.UnionWith(s_groupKeys);
                        }
                        else if (e.shift)
                        {
                            _selKeys.UnionWith(s_groupKeys);
                        }
                        else if (!all)
                        {
                            _selLinks.Clear();
                            _selKeys.Clear();
                            _selKeys.UnionWith(s_groupKeys);
                        }
                        if (e.clickCount == 2)
                            s.GoTo(group / 100f);
                        _barDrag = BarDrag.Move;
                        _barDragStart = m;
                        _barMoveDelta = 0f;
                        _copyDrag = e.shift;
                    }
                    else
                    {
                        if (!e.shift && !ctrl)
                        {
                            _selKeys.Clear();
                            _selLinks.Clear();
                        }
                        _barDrag = BarDrag.Box;
                        _barDragStart = m;
                    }
                    GUIUtility.hotControl = id;
                    e.Use();
                    Repaint();
                    break;
                }
                case EventType.MouseDrag:
                    if (_barDrag == BarDrag.None || GUIUtility.hotControl != id)
                        return;
                    if (_barDrag == BarDrag.Scrub)
                        s.GoTo(Mathf.Round(BarFrame(m.x - _barGrab)));
                    else if (_barDrag == BarDrag.Move)
                        _barMoveDelta = Mathf.Round((m.x - _barDragStart.x) / BarPpf);
                    else if (_barDrag == BarDrag.Range)
                        UpdateRange(m.x);
                    else if (SpanDragging)
                        UpdateSpanDrag(m.x);
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseUp:
                    if (_barDrag == BarDrag.Move && Mathf.Abs(_barMoveDelta) > 0f)
                        ApplyKeyDrag(0f, 1f, _barMoveDelta, _copyDrag);
                    else if (_barDrag == BarDrag.Box)
                        BarBoxSelect(_barDragStart.x, m.x);
                    else if (_barDrag == BarDrag.Range)
                        EndRange();
                    else if (SpanDragging && (Mathf.Abs(_spanFactor - 1f) > 1e-5f || Mathf.Abs(_spanDelta) > 0f))
                        ApplyKeyDrag(_spanPivot, _spanFactor, _spanDelta, _copyDrag);
                    if (_barDrag != BarDrag.None)
                    {
                        _barDrag = BarDrag.None;
                        _barMoveDelta = 0f;
                        _spanFactor = 1f;
                        _spanDelta = 0f;
                        _copyDrag = false;
                        e.Use();
                        Repaint();
                    }
                    break;
                case EventType.ContextClick:
                    if (s_eatContext)
                    {
                        s_eatContext = false;
                        e.Use();
                    }
                    break;
            }
        }

        private static bool RangeModifier(Event e)
        {
            return (e.control || e.command) && e.alt && e.button <= 2;
        }

        /// <summary>Ctrl+Alt 拖动改工程时间范围：左键起始帧、右键结束帧、中键整体平移。像抓住时间轴拖（同 Max）：向右拖帧号变小、向左拖变大。
        /// 按下时的帧宽换算鼠标位移，整个拖动合并为一步撤销。</summary>
        private static void BeginRange(int button, float x, float ppf)
        {
            _rangeEdit = button == 0 ? RangeEdit.Start : button == 1 ? RangeEdit.End : RangeEdit.Slide;
            _rangeStart0 = _project.startFrame;
            _rangeEnd0 = _project.endFrame;
            _rangeMouse0 = x;
            _rangePpf = Mathf.Max(0.01f, ppf);
            Undo.IncrementCurrentGroup();
            _rangeGroup = Undo.GetCurrentGroup();
            s_eatContext = button == 1;
        }

        private static void UpdateRange(float x)
        {
            int d = Mathf.RoundToInt((_rangeMouse0 - x) / _rangePpf);
            int start = _rangeStart0;
            int end = _rangeEnd0;
            switch (_rangeEdit)
            {
                case RangeEdit.Start:
                    start = Mathf.Min(_rangeStart0 + d, _rangeEnd0 - 1);
                    break;
                case RangeEdit.End:
                    end = Mathf.Max(_rangeEnd0 + d, _rangeStart0 + 1);
                    break;
                default:
                    start += d;
                    end += d;
                    break;
            }
            if (start == _project.startFrame && end == _project.endFrame)
                return;
            Mutate("时间范围", () =>
            {
                _project.startFrame = start;
                _project.endFrame = end;
            });
        }

        private static void EndRange()
        {
            Undo.CollapseUndoOperations(_rangeGroup);
        }

        /// <summary>选择范围条按下：靠近两端拖端点缩放（另一端为轴心），中间整体移动。</summary>
        private static bool BeginSpanDrag(float x, bool copy)
        {
            if (!_spanShown)
                return false;
            float x0 = BarX(_spanMin);
            float x1 = BarX(_spanMax);
            if (Mathf.Min(Mathf.Abs(x - x0), Mathf.Abs(x - x1)) <= SpanGrip)
            {
                if (x <= (x0 + x1) * 0.5f)
                {
                    _barDrag = BarDrag.SpanStart;
                    _spanPivot = _spanMax;
                    _spanGrab = x - x0;
                }
                else
                {
                    _barDrag = BarDrag.SpanEnd;
                    _spanPivot = _spanMin;
                    _spanGrab = x - x1;
                }
            }
            else if (x > x0 && x < x1)
            {
                _barDrag = BarDrag.SpanMove;
                _spanGrab = x;
            }
            else
            {
                return false;
            }
            _spanFactor = 1f;
            _spanDelta = 0f;
            _copyDrag = copy;
            return true;
        }

        /// <summary>拖动的端点按整帧步进；选中范围最短 1 帧（原本不足 1 帧时不短于原长）。</summary>
        private static void UpdateSpanDrag(float x)
        {
            float length = _spanMax - _spanMin;
            float shortest = Mathf.Min(1f, length);
            if (_barDrag == BarDrag.SpanMove)
            {
                _spanDelta = Mathf.Round((x - _spanGrab) / BarPpf);
                return;
            }
            float f = BarFrame(x - _spanGrab);
            if (_barDrag == BarDrag.SpanStart)
            {
                float a = Mathf.Min(_spanMin + Mathf.Round(f - _spanMin), _spanMax - shortest);
                _spanFactor = (_spanMax - a) / length;
            }
            else
            {
                float b = Mathf.Max(_spanMax + Mathf.Round(f - _spanMax), _spanMin + shortest);
                _spanFactor = (b - _spanMin) / length;
            }
        }

        private static void BarBoxSelect(float a, float b)
        {
            float x0 = Mathf.Min(a, b);
            float x1 = Mathf.Max(a, b);
            for (int t = 0; t < _rowTracks.Count; t++)
            {
                List<AsiKey> keys = _rowTracks[t].keys;
                for (int k = 0; k < keys.Count; k++)
                {
                    float x = BarX(keys[k].frame);
                    if (x >= x0 && x <= x1)
                        _selKeys.Add(keys[k]);
                }
            }
            for (int k = 0; k < _project.constraints.Count; k++)
            {
                AsiKeyConstraint c = _project.constraints[k];
                if (c.kind != AsiConstraintKind.Link)
                    continue;
                for (int i = 0; i < c.links.Count; i++)
                {
                    float x = BarX(c.links[i].frame);
                    if (x >= x0 && x <= x1)
                        _selLinks.Add(c.links[i]);
                }
            }
        }

        // ---------------- 分轨道摄影表（展开时） ----------------

        private static float FrameToX(Rect area, float f)
        {
            return area.x + (f - _tlFirst) * _tlPpf;
        }

        private static float XToFrame(Rect area, float x)
        {
            return _tlFirst + (x - area.x) / _tlPpf;
        }

        private static void FrameAll()
        {
            if (_project == null)
                return;
            float span = Mathf.Max(1f, _project.endFrame - _project.startFrame);
            float width = Mathf.Max(100f, _keysWidth - 20f);
            _tlPpf = Mathf.Clamp(width / (span + 4f), 0.5f, 60f);
            _tlFirst = _project.startFrame - 2f;
            SaveView();
            Repaint();
        }

        private static void DrawTimeline(Rect r, AsiKeySession s)
        {
            EditorGUI.DrawRect(r, new Color(0.17f, 0.17f, 0.17f));
            Rect ruler = new Rect(r.x + NameWidth, r.y, r.width - NameWidth, RulerHeight);
            Rect keys = new Rect(r.x + NameWidth, r.y + RulerHeight, r.width - NameWidth, r.height - RulerHeight);
            _keysWidth = keys.width;
            EditorGUI.DrawRect(new Rect(r.x, r.y, NameWidth, RulerHeight), new Color(0.13f, 0.13f, 0.13f));
            GUI.Label(new Rect(r.x + 4f, r.y + 1f, NameWidth - 8f, RulerHeight), _project.ActiveLayer.name, EditorStyles.miniBoldLabel);

            int totalRows = _rows.Count + 1;
            float content = totalRows * RowHeight;
            _tlRowScroll = Mathf.Clamp(_tlRowScroll, 0f, Mathf.Max(0f, content - keys.height));

            DrawRuler(ruler, s);
            GUI.BeginClip(new Rect(r.x, keys.y, r.width, keys.height));
            Rect localKeys = new Rect(NameWidth, 0f, keys.width, keys.height);
            float rangeA = FrameToX(localKeys, _project.startFrame);
            float rangeB = FrameToX(localKeys, _project.endFrame);
            EditorGUI.DrawRect(new Rect(NameWidth, 0f, Mathf.Max(0f, rangeA - NameWidth), keys.height), new Color(0f, 0f, 0f, 0.25f));
            if (rangeB < localKeys.xMax)
                EditorGUI.DrawRect(new Rect(rangeB, 0f, localKeys.xMax - rangeB, keys.height), new Color(0f, 0f, 0f, 0.25f));
            DrawGrid(localKeys);

            for (int row = 0; row < totalRows; row++)
            {
                float y = row * RowHeight - _tlRowScroll;
                if (y + RowHeight < 0f || y > keys.height)
                    continue;
                Rect nameRect = new Rect(0f, y, NameWidth, RowHeight);
                Rect lane = new Rect(NameWidth, y, keys.width, RowHeight);
                if ((row & 1) == 0)
                    EditorGUI.DrawRect(lane, new Color(1f, 1f, 1f, 0.025f));
                EditorGUI.DrawRect(nameRect, new Color(0.2f, 0.2f, 0.2f));
                if (row == 0)
                {
                    GUI.Label(new Rect(6f, y, NameWidth - 8f, RowHeight), "汇总", EditorStyles.miniBoldLabel);
                    DrawSummaryKeys(localKeys, lane);
                }
                else
                {
                    Row rw = _rows[row - 1];
                    GUI.Label(new Rect(6f, y, NameWidth - 8f, RowHeight), rw.name, EditorStyles.miniLabel);
                    if (rw.track != null)
                        DrawTrackKeys(localKeys, lane, rw.track);
                    int link = RowLink(rw);
                    if (link >= 0)
                        DrawLinkMarks(localKeys, lane.y, RowHeight, s, link, false, true);
                }
            }
            float cx = FrameToX(localKeys, s.frame);
            if (cx >= NameWidth)
                EditorGUI.DrawRect(new Rect(cx, 0f, 1f, keys.height), new Color(1f, 0.25f, 0.25f, 0.9f));
            if (_drag == Drag.Box)
            {
                Vector2 a = _dragStart - new Vector2(r.x, keys.y);
                Vector2 b = Event.current.mousePosition;
                Rect box = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
                EditorGUI.DrawRect(box, new Color(0.4f, 0.6f, 1f, 0.15f));
            }
            GUI.EndClip();

            HandleTimelineInput(r, ruler, keys, s);
        }

        private static void DrawRuler(Rect ruler, AsiKeySession s)
        {
            EditorGUI.DrawRect(ruler, new Color(0.13f, 0.13f, 0.13f));
            float step = TickStepFor(_tlPpf);
            float first = Mathf.Floor(XToFrame(ruler, ruler.x) / step) * step;
            for (float f = first; FrameToX(ruler, f) < ruler.xMax; f += step)
            {
                float x = FrameToX(ruler, f);
                if (x < ruler.x)
                    continue;
                EditorGUI.DrawRect(new Rect(x, ruler.yMax - 6f, 1f, 6f), new Color(1f, 1f, 1f, 0.4f));
                GUI.Label(new Rect(x + 2f, ruler.y, 40f, ruler.height), f.ToString("0"), EditorStyles.miniLabel);
            }
            float cx = FrameToX(ruler, s.frame);
            if (cx >= ruler.x)
            {
                EditorGUI.DrawRect(new Rect(cx - 1f, ruler.y, 3f, ruler.height), new Color(1f, 0.25f, 0.25f));
            }
        }

        private static void DrawGrid(Rect area)
        {
            float step = TickStepFor(_tlPpf);
            float first = Mathf.Floor(XToFrame(area, area.x) / step) * step;
            for (float f = first; FrameToX(area, f) < area.xMax; f += step)
            {
                float x = FrameToX(area, f);
                if (x >= area.x)
                    EditorGUI.DrawRect(new Rect(x, area.y, 1f, area.height), new Color(1f, 1f, 1f, 0.05f));
            }
        }

        private static void DrawKeyDiamond(Rect area, float y, float frame, Color col, bool selected)
        {
            float x = FrameToX(area, frame);
            if (x < area.x - 6f || x > area.xMax + 6f)
                return;
            float cy = y + RowHeight * 0.5f;
            Vector3[] pts =
            {
                new Vector3(x, cy - 5f), new Vector3(x + 5f, cy), new Vector3(x, cy + 5f), new Vector3(x - 5f, cy)
            };
            Handles.color = selected ? Color.Lerp(col, Color.white, 0.6f) : col;
            Handles.DrawAAConvexPolygon(pts);
            if (selected)
            {
                Handles.color = Color.white;
                Handles.DrawAAPolyLine(2f, pts[0], pts[1], pts[2], pts[3], pts[0]);
            }
        }

        private static void DrawKeySquare(Rect area, float y, float frame, Color col, bool selected)
        {
            float x = FrameToX(area, frame);
            if (x < area.x - 6f || x > area.xMax + 6f)
                return;
            float cy = y + RowHeight * 0.5f;
            Rect box = new Rect(x - 4f, cy - 4f, 8f, 8f);
            EditorGUI.DrawRect(box, selected ? Color.Lerp(col, Color.white, 0.45f) : col);
            EditorGUI.DrawRect(new Rect(box.x, box.y, box.width, 1f), Color.white);
            EditorGUI.DrawRect(new Rect(box.x, box.yMax - 1f, box.width, 1f), Color.white);
        }

        private static int RowLink(Row row)
        {
            if (row.link >= 0)
                return row.link;
            AsiKeyTrack tr = row.track;
            if (tr == null || tr.kind != AsiTrackKind.Constraint)
                return -1;
            if (tr.target < 0 || tr.target >= _project.constraints.Count)
                return -1;
            return _project.constraints[tr.target].kind == AsiConstraintKind.Link ? tr.target : -1;
        }

        private static float ShownLinkFrame(AsiLinkEntry le)
        {
            if (le == null || !_selLinks.Contains(le))
                return le != null ? le.frame : 0f;
            if (CopyDragging)
                return le.frame;
            return DragFrame(le.frame);
        }

        private static void DrawLinkMarks(Rect area, float y, float h, AsiKeySession s, int constraint, bool bar, bool label)
        {
            if (constraint < 0 || constraint >= _project.constraints.Count || Event.current.type != EventType.Repaint)
                return;
            AsiKeyConstraint c = _project.constraints[constraint];
            if (c.kind != AsiConstraintKind.Link)
                return;
            bool copying = CopyDragging;
            for (int i = 0; i < c.links.Count; i++)
            {
                AsiLinkEntry le = c.links[i];
                bool sel = _selLinks.Contains(le);
                DrawOneLink(area, y, h, s, le, ShownLinkFrame(le), sel && !copying, bar, label);
                if (sel && copying)
                    DrawOneLink(area, y, h, s, le, DragFrame(le.frame), true, bar, label);
            }
        }

        private static void DrawOneLink(Rect area, float y, float h, AsiKeySession s, AsiLinkEntry le, float frame, bool selected, bool bar, bool label)
        {
            float x = bar ? BarX(frame) : FrameToX(area, frame);
            if (x < area.x - 8f || x > area.xMax + 8f)
                return;
            float cy = y + h * 0.5f;
            Vector3[] pts =
            {
                new Vector3(x, cy - 6f), new Vector3(x + 6f, cy), new Vector3(x, cy + 6f), new Vector3(x - 6f, cy)
            };
            Handles.color = selected ? Color.Lerp(AsiKeyLabels.LinkKeyColor, Color.white, 0.55f) : AsiKeyLabels.LinkKeyColor;
            Handles.DrawAAConvexPolygon(pts);
            Handles.color = Color.white;
            Handles.DrawAAPolyLine(selected ? 2.5f : 1.5f, pts[0], pts[1], pts[2], pts[3], pts[0]);
            if (!label)
                return;
            string name = s != null ? s.TargetName(le.bone, le.obj) : "";
            if (string.IsNullOrEmpty(name))
                name = "世界";
            GUI.Label(new Rect(x + 8f, cy - 7f, 80f, 14f), name, EditorStyles.whiteMiniLabel);
        }

        private static void DrawBarLinks(AsiKeySession s, Rect keys, Rect zone)
        {
            for (int k = 0; k < _project.constraints.Count; k++)
            {
                AsiKeyConstraint c = _project.constraints[k];
                if (c.kind != AsiConstraintKind.Link || c.links.Count == 0)
                    continue;
                if (_tlOnlySelected)
                {
                    List<int> sel = s.SelectedBones();
                    if (sel.Count > 0 && !sel.Contains(c.bone))
                        continue;
                }
                DrawLinkMarks(keys, zone.y, zone.height, s, k, true, false);
            }
        }

        private static void DrawTrackKeys(Rect area, Rect lane, AsiKeyTrack tr)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            bool copying = CopyDragging;
            for (int k = 0; k < tr.keys.Count; k++)
            {
                AsiKey key = tr.keys[k];
                Color col = AsiKeyLabels.ChannelColor(AsiKeyLabels.ChannelOf(tr, key));
                bool sel = _selKeys.Contains(key);
                bool weight = tr.kind == AsiTrackKind.Constraint;
                if (weight)
                    DrawKeySquare(area, lane.y, DisplayFrame(key), col, sel && !copying);
                else
                    DrawKeyDiamond(area, lane.y, DisplayFrame(key), col, sel && !copying);
                if (sel && copying)
                {
                    if (weight)
                        DrawKeySquare(area, lane.y, DragFrame(key.frame), col, true);
                    else
                        DrawKeyDiamond(area, lane.y, DragFrame(key.frame), col, true);
                }
            }
        }

        private static void DrawSummaryKeys(Rect area, Rect lane)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            foreach (KeyGroup g in s_groups.Values)
            {
                float x = FrameToX(area, g.frame);
                if (x < area.x - 6f || x > area.xMax + 6f)
                    continue;
                DrawKeyStack(x, lane.y + 3f, 6f, RowHeight - 6f, g.mask, g.selected);
            }
        }

        /// <summary>鼠标位置下的关键帧（行号从 1 开始，0 为汇总行）。</summary>
        private static bool HitKey(Rect keys, Vector2 mouse, out int row, out AsiKey hit)
        {
            row = Mathf.FloorToInt((mouse.y - keys.y + _tlRowScroll) / RowHeight);
            hit = null;
            if (row < 1 || row > _rows.Count)
                return false;
            AsiKeyTrack tr = _rows[row - 1].track;
            if (tr == null)
                return false;
            float best = 7f;
            for (int k = 0; k < tr.keys.Count; k++)
            {
                float x = FrameToX(keys, tr.keys[k].frame);
                float d = Mathf.Abs(x - mouse.x);
                if (d < best)
                {
                    best = d;
                    hit = tr.keys[k];
                }
            }
            return hit != null;
        }

        private static bool HitLink(Rect keys, Vector2 mouse, out AsiLinkEntry hit)
        {
            hit = null;
            int row = Mathf.FloorToInt((mouse.y - keys.y + _tlRowScroll) / RowHeight);
            if (row < 1 || row > _rows.Count)
                return false;
            int link = RowLink(_rows[row - 1]);
            if (link < 0)
                return false;
            AsiKeyConstraint c = _project.constraints[link];
            float best = 8f;
            for (int i = 0; i < c.links.Count; i++)
            {
                float x = FrameToX(keys, ShownLinkFrame(c.links[i]));
                float d = Mathf.Abs(x - mouse.x);
                if (d < best)
                {
                    best = d;
                    hit = c.links[i];
                }
            }
            return hit != null;
        }

        private static bool HitBarLink(float mx, out AsiLinkEntry hit)
        {
            hit = null;
            float best = 8f;
            for (int k = 0; k < _project.constraints.Count; k++)
            {
                AsiKeyConstraint c = _project.constraints[k];
                if (c.kind != AsiConstraintKind.Link)
                    continue;
                for (int i = 0; i < c.links.Count; i++)
                {
                    float d = Mathf.Abs(BarX(ShownLinkFrame(c.links[i])) - mx);
                    if (d < best)
                    {
                        best = d;
                        hit = c.links[i];
                    }
                }
            }
            return hit != null;
        }

        private static void SelectLink(AsiLinkEntry le, bool add, bool toggle)
        {
            if (!add && !toggle)
            {
                _selKeys.Clear();
                _selLinks.Clear();
            }
            if (toggle)
            {
                if (!_selLinks.Remove(le))
                    _selLinks.Add(le);
            }
            else
                _selLinks.Add(le);
        }

        private static void HandleTimelineInput(Rect r, Rect ruler, Rect keys, AsiKeySession s)
        {
            Event e = Event.current;
            Vector2 m = e.mousePosition;
            int id = GUIUtility.GetControlID(TimelineHint, FocusType.Keyboard);
            if (GUIUtility.keyboardControl == id && HandleTimelineKeyboard(s, e))
                return;
            EventType type = e.GetTypeForControl(id);
            if (e.rawType == EventType.MouseUp && _drag != Drag.None)
            {
                type = EventType.MouseUp;
                if (GUIUtility.hotControl == id)
                    GUIUtility.hotControl = 0;
            }
            switch (type)
            {
                case EventType.ScrollWheel:
                    if (!r.Contains(m))
                        return;
                    if (e.shift)
                    {
                        _tlRowScroll += e.delta.y * 6f;
                    }
                    else
                    {
                        float anchor = XToFrame(keys, m.x);
                        _tlPpf = Mathf.Clamp(_tlPpf * (e.delta.y > 0 ? 0.85f : 1.18f), 0.3f, 80f);
                        _tlFirst = anchor - (m.x - keys.x) / _tlPpf;
                        SaveView();
                    }
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseDown:
                    if (!r.Contains(m))
                        return;
                    GUI.FocusControl(null);
                    GUIUtility.keyboardControl = id;
                    if (RangeModifier(e))
                    {
                        BeginRange(e.button, m.x, _tlPpf);
                        _drag = Drag.Range;
                        GUIUtility.hotControl = id;
                        e.Use();
                        break;
                    }
                    if (e.button == 2 || e.button == 0 && e.alt)
                    {
                        _drag = Drag.Pan;
                        _dragStart = m;
                        GUIUtility.hotControl = id;
                        e.Use();
                        break;
                    }
                    if (ruler.Contains(m) && e.button == 0)
                    {
                        _drag = Drag.Scrub;
                        GUIUtility.hotControl = id;
                        s.GoTo(Mathf.Round(XToFrame(ruler, m.x)));
                        e.Use();
                        break;
                    }
                    if (!keys.Contains(m) || m.x < keys.x)
                        return;
                    int row;
                    AsiKey hit = null;
                    AsiLinkEntry linkHit;
                    bool onLink = HitLink(keys, m, out linkHit);
                    bool onKey = !onLink && HitKey(keys, m, out row, out hit);
                    if (e.button == 1)
                    {
                        if (onLink && !_selLinks.Contains(linkHit))
                            SelectLink(linkHit, false, false);
                        else if (onKey && !_selKeys.Contains(hit))
                        {
                            _selLinks.Clear();
                            _selKeys.Clear();
                            _selKeys.Add(hit);
                        }
                        ShowTimelineMenu(s, XToFrame(keys, m.x));
                        e.Use();
                        break;
                    }
                    if (e.button != 0)
                        return;
                    if (onLink)
                    {
                        bool toggle = e.control || e.command;
                        SelectLink(linkHit, e.shift, toggle);
                        if (e.clickCount == 2)
                            s.GoTo(linkHit.frame);
                        _drag = Drag.Move;
                        _dragStart = m;
                        _moveDelta = 0f;
                        _copyDrag = e.shift;
                    }
                    else if (onKey)
                    {
                        if (e.control || e.command)
                        {
                            if (!_selKeys.Remove(hit))
                                _selKeys.Add(hit);
                        }
                        else if (e.shift)
                        {
                            _selKeys.Add(hit);
                        }
                        else if (!_selKeys.Contains(hit))
                        {
                            _selLinks.Clear();
                            _selKeys.Clear();
                            _selKeys.Add(hit);
                        }
                        if (e.clickCount == 2)
                            s.GoTo(hit.frame);
                        _drag = Drag.Move;
                        _dragStart = m;
                        _moveDelta = 0f;
                        _copyDrag = e.shift;
                    }
                    else
                    {
                        if (!e.shift && !e.control)
                        {
                            _selKeys.Clear();
                            _selLinks.Clear();
                        }
                        _drag = Drag.Box;
                        _dragStart = m;
                    }
                    GUIUtility.hotControl = id;
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseDrag:
                    if (_drag == Drag.None || GUIUtility.hotControl != id)
                        return;
                    if (_drag == Drag.Pan)
                    {
                        _tlFirst -= e.delta.x / _tlPpf;
                        _tlRowScroll -= e.delta.y;
                        SaveView();
                    }
                    else if (_drag == Drag.Scrub)
                    {
                        s.GoTo(Mathf.Round(XToFrame(ruler, m.x)));
                    }
                    else if (_drag == Drag.Move)
                    {
                        _moveDelta = Mathf.Round((m.x - _dragStart.x) / _tlPpf);
                    }
                    else if (_drag == Drag.Range)
                    {
                        UpdateRange(m.x);
                    }
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseUp:
                    if (_drag == Drag.Move && Mathf.Abs(_moveDelta) > 0f)
                        ApplyKeyDrag(0f, 1f, _moveDelta, _copyDrag);
                    else if (_drag == Drag.Box)
                        BoxSelect(keys, _dragStart, m);
                    else if (_drag == Drag.Range)
                        EndRange();
                    if (_drag != Drag.None)
                    {
                        _drag = Drag.None;
                        _moveDelta = 0f;
                        _copyDrag = false;
                        e.Use();
                        Repaint();
                    }
                    break;
                case EventType.ContextClick:
                    if (s_eatContext)
                    {
                        s_eatContext = false;
                        e.Use();
                    }
                    break;
            }
        }

        /// <summary>时间轴获得键盘焦点时处理 Delete、Ctrl+C/V/A、F，按键和编辑菜单命令两条路都接。</summary>
        private static bool HandleTimelineKeyboard(AsiKeySession s, Event e)
        {
            if (EditorGUIUtility.editingTextField)
                return false;
            if (e.type == EventType.KeyDown)
            {
                if (!HandleTimelineKeys(s, e))
                    return false;
                e.Use();
                Repaint();
                return true;
            }
            if (e.type != EventType.ValidateCommand && e.type != EventType.ExecuteCommand)
                return false;
            bool exec = e.type == EventType.ExecuteCommand;
            switch (e.commandName)
            {
                case "SoftDelete":
                case "Delete":
                    if (exec)
                    {
                        if (_selKeys.Count > 0) DeleteSelected();
                        else s.DeleteKeysAtFrame();
                    }
                    break;
                case "Copy":
                    if (exec) CopySelected();
                    break;
                case "Paste":
                    if (exec) PasteKeys(s);
                    break;
                case "SelectAll":
                    if (exec) SelectAllRows();
                    break;
                case "FrameSelected":
                    if (exec) FrameAll();
                    break;
                case "Duplicate":
                case "Cut":
                    break;
                default:
                    return false;
            }
            e.Use();
            Repaint();
            return true;
        }

        private static void BoxSelect(Rect keys, Vector2 a, Vector2 b)
        {
            Rect box = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            for (int r = 0; r < _rows.Count; r++)
            {
                float y = keys.y + (r + 1) * RowHeight - _tlRowScroll + RowHeight * 0.5f;
                if (y < box.yMin || y > box.yMax)
                    continue;
                int link = RowLink(_rows[r]);
                if (link >= 0)
                {
                    AsiKeyConstraint c = _project.constraints[link];
                    for (int i = 0; i < c.links.Count; i++)
                    {
                        float lx = FrameToX(keys, c.links[i].frame);
                        if (lx >= box.xMin && lx <= box.xMax)
                            _selLinks.Add(c.links[i]);
                    }
                }
                if (_rows[r].track == null)
                    continue;
                List<AsiKey> list = _rows[r].track.keys;
                for (int k = 0; k < list.Count; k++)
                {
                    float x = FrameToX(keys, list[k].frame);
                    if (x >= box.xMin && x <= box.xMax)
                        _selKeys.Add(list[k]);
                }
            }
            float sy = keys.y - _tlRowScroll + RowHeight * 0.5f;
            if (sy >= box.yMin && sy <= box.yMax)
            {
                for (int r = 0; r < _rows.Count; r++)
                {
                if (_rows[r].track == null)
                    continue;
                List<AsiKey> list = _rows[r].track.keys;
                for (int k = 0; k < list.Count; k++)
                {
                    float x = FrameToX(keys, list[k].frame);
                    if (x >= box.xMin && x <= box.xMax)
                        _selKeys.Add(list[k]);
                }
            }
        }
        }

        // ---------------- 关键帧编辑 ----------------

        private static void ForEachSelectedTrack(Action<AsiKeyTrack, int, List<AsiKey>> fn)
        {
            for (int l = 0; l < _project.layers.Count; l++)
            {
                List<AsiKeyTrack> tracks = _project.layers[l].tracks;
                for (int t = 0; t < tracks.Count; t++)
                {
                    List<AsiKey> picked = null;
                    List<AsiKey> keys = tracks[t].keys;
                    for (int k = 0; k < keys.Count; k++)
                    {
                        if (_selKeys.Contains(keys[k]))
                        {
                            if (picked == null)
                                picked = new List<AsiKey>();
                            picked.Add(keys[k]);
                        }
                    }
                    if (picked != null)
                        fn(tracks[t], l, picked);
                }
            }
        }

        /// <summary>松开拖动：选中关键帧映射到 pivot + (帧 - pivot) * factor + delta。复制时原关键帧不动，改为选中复制出的。</summary>
        private static void ApplyKeyDrag(float pivot, float factor, float delta, bool copy)
        {
            if (_selLinks.Count > 0)
            {
                AsiKeySession session = AsiKeySession.Current;
                if (session != null)
                {
                    List<AsiLinkEntry> moving = new List<AsiLinkEntry>(_selLinks);
                    List<AsiLinkEntry> copies = copy ? new List<AsiLinkEntry>() : null;
                    session.MoveLinkEntries(moving, pivot, factor, delta, copy, copies);
                    if (copy)
                    {
                        _selLinks.Clear();
                        for (int i = 0; i < copies.Count; i++)
                            _selLinks.Add(copies[i]);
                    }
                }
            }
            if (_selKeys.Count == 0)
                return;
            if (copy)
            {
                List<AsiKey> copies = new List<AsiKey>();
                Mutate("复制关键帧", () => ForEachSelectedTrack((tr, layer, keys) =>
                    copies.AddRange(AsiKeyEdit.CopyKeys(_project, tr, keys, pivot, factor, delta, layer))));
                _selKeys.Clear();
                _selKeys.UnionWith(copies);
            }
            else if (Mathf.Abs(factor - 1f) > 1e-5f)
            {
                Mutate("缩放关键帧", () => ForEachSelectedTrack((tr, layer, keys) => AsiKeyEdit.ScaleKeys(_project, tr, keys, pivot, factor, layer)));
            }
            else
            {
                Mutate("移动关键帧", () => ForEachSelectedTrack((tr, layer, keys) => AsiKeyEdit.MoveKeys(_project, tr, keys, delta, layer)));
            }
        }

        private static void ScaleSelected(AsiKeySession s, float factor)
        {
            if (factor <= 0f)
                return;
            float pivot = s.frame;
            Mutate("缩放关键帧", () => ForEachSelectedTrack((tr, layer, keys) => AsiKeyEdit.ScaleKeys(_project, tr, keys, pivot, factor, layer)));
        }

        private static void DeleteSelected()
        {
            if (_selLinks.Count > 0)
            {
                AsiKeySession session = AsiKeySession.Current;
                if (session != null)
                    session.DeleteLinkEntries(new List<AsiLinkEntry>(_selLinks));
                _selLinks.Clear();
            }
            if (_selKeys.Count == 0)
                return;
            Mutate("删除关键帧", () => ForEachSelectedTrack((tr, layer, keys) =>
            {
                for (int k = 0; k < keys.Count; k++)
                    tr.keys.Remove(keys[k]);
                AsiKeyEdit.AfterEdit(_project, tr, layer, null);
            }));
            _selKeys.Clear();
        }

        private static void CopySelected()
        {
            s_copied.Clear();
            s_copiedMin = float.MaxValue;
            ForEachSelectedTrack((tr, layer, keys) =>
            {
                for (int k = 0; k < keys.Count; k++)
                {
                    CopiedKey c;
                    c.track = tr;
                    c.layer = layer;
                    c.key = keys[k].Clone();
                    s_copied.Add(c);
                    s_copiedMin = Mathf.Min(s_copiedMin, keys[k].frame);
                }
            });
        }

        private static void PasteKeys(AsiKeySession s)
        {
            if (s_copied.Count == 0)
                return;
            float at = Mathf.Round(s.frame);
            Mutate("粘贴关键帧", () =>
            {
                _selKeys.Clear();
                List<AsiKeyTrack> touched = new List<AsiKeyTrack>();
                List<int> layers = new List<int>();
                for (int i = 0; i < s_copied.Count; i++)
                {
                    CopiedKey c = s_copied[i];
                    if (FindTrackOfKeyOwner(c.track) < 0)
                        continue;
                    AsiKey k = c.key.Clone();
                    k.frame = at + (c.key.frame - s_copiedMin);
                    int existing = AsiKeyTrackEval.FindKeyIndex(c.track.keys, k.frame);
                    if (existing >= 0)
                        c.track.keys.RemoveAt(existing);
                    c.track.keys.Add(k);
                    _selKeys.Add(k);
                    if (!touched.Contains(c.track))
                    {
                        touched.Add(c.track);
                        layers.Add(c.layer);
                    }
                }
                for (int t = 0; t < touched.Count; t++)
                    AsiKeyEdit.AfterEdit(_project, touched[t], layers[t], null);
            });
        }

        private static int FindTrackOfKeyOwner(AsiKeyTrack tr)
        {
            for (int l = 0; l < _project.layers.Count; l++)
            {
                if (_project.layers[l].tracks.Contains(tr))
                    return l;
            }
            return -1;
        }

        private static void ShowTimelineMenu(AsiKeySession s, float frame)
        {
            GenericMenu menu = new GenericMenu();
            bool any = _selKeys.Count > 0;
            AddMenu(menu, "跳到这里", true, () => s.GoTo(Mathf.Round(frame)));
            menu.AddSeparator("");
            AddMenu(menu, "复制 (Ctrl+C)", any, CopySelected);
            AddMenu(menu, "粘贴到当前帧 (Ctrl+V)", s_copied.Count > 0, () => PasteKeys(s));
            AddMenu(menu, "删除 (Delete)", any, DeleteSelected);
            menu.AddSeparator("");
            for (int t = 0; t < AsiKeyLabels.KeyType.Length; t++)
            {
                AsiKeyType type = (AsiKeyType)t;
                AddMenu(menu, "改为关键帧类型/" + AsiKeyLabels.KeyType[t], any, () => SetSelectedType(s, type));
            }
            for (int t = 0; t < AsiKeyLabels.Tangent.Length; t++)
            {
                AsiTangentType type = (AsiTangentType)t;
                if (type == AsiTangentType.Fixed)
                    continue;
                AddMenu(menu, "曲线切线/" + AsiKeyLabels.Tangent[t], any, () => SetSelectedTangent(type));
            }
            AddMenu(menu, "TCB/重置为默认", any, () => Mutate("TCB 默认", () =>
            {
                foreach (AsiKey k in _selKeys)
                    AsiKeyEdit.ApplyTCB(k, 25f, 25f, 25f, 0f, 0f);
            }));
            AddMenu(menu, "TCB/缓入缓出", any, () => Mutate("TCB 缓入缓出", () =>
            {
                foreach (AsiKey k in _selKeys)
                    AsiKeyEdit.ApplyTCB(k, k.tension, k.continuity, k.bias, 25f, 25f);
            }));
            menu.AddSeparator("");
            AddMenu(menu, "只显示选中控制器", true, () => SetOnlySelected(!_tlOnlySelected), _tlOnlySelected);
            AddMenu(menu, "分轨道视图", true, () => TimelineExpanded = !TimelineExpanded, TimelineExpanded);
            menu.ShowAsContext();
        }

        private static void SetSelectedType(AsiKeySession s, AsiKeyType type)
        {
            List<AsiKey> keys = new List<AsiKey>(_selKeys);
            keys.Sort((a, b) => a.frame.CompareTo(b.frame));
            for (int i = 0; i < keys.Count; i++)
            {
                AsiKeyTrack tr = FindTrackOfKey(keys[i]);
                if (tr == null)
                    continue;
                if (tr.kind == AsiTrackKind.Chain && FindTrackOfKeyOwner(tr) == 0)
                    s.SetKeyType(tr, keys[i], type);
            }
        }

        private static void SetSelectedTangent(AsiTangentType type)
        {
            Mutate("切线类型", () => ForEachSelectedTrack((tr, layer, keys) =>
            {
                for (int k = 0; k < keys.Count; k++)
                {
                    AsiKeyEdit.EnsureArrays(_project, tr, keys[k]);
                    AsiKeyEdit.SetTangentType(keys[k], -1, type != AsiTangentType.Stepped, true, type);
                }
                AsiKeyEdit.RecomputeTangents(_project, tr);
            }));
        }

        private static void SelectAllRows()
        {
            _selKeys.Clear();
            _selLinks.Clear();
            for (int r = 0; r < _rows.Count; r++)
            {
                int link = RowLink(_rows[r]);
                if (link >= 0)
                {
                    AsiKeyConstraint c = _project.constraints[link];
                    for (int i = 0; i < c.links.Count; i++)
                        _selLinks.Add(c.links[i]);
                }
                if (_rows[r].track == null)
                    continue;
                List<AsiKey> keys = _rows[r].track.keys;
                for (int k = 0; k < keys.Count; k++)
                    _selKeys.Add(keys[k]);
            }
        }

        private static bool HandleTimelineKeys(AsiKeySession s, Event e)
        {
            bool ctrl = e.control || e.command;
            switch (e.keyCode)
            {
                case KeyCode.Delete:
                case KeyCode.Backspace:
                    if (_selKeys.Count > 0 || _selLinks.Count > 0)
                        DeleteSelected();
                    else
                        s.DeleteKeysAtFrame();
                    return true;
                case KeyCode.C:
                    if (!ctrl)
                        return false;
                    CopySelected();
                    return true;
                case KeyCode.V:
                    if (!ctrl)
                        return false;
                    PasteKeys(s);
                    return true;
                case KeyCode.F:
                    if (ctrl || e.shift || e.alt || !TimelineExpanded)
                        return false;
                    FrameAll();
                    return true;
                case KeyCode.A:
                    if (!ctrl)
                        return false;
                    SelectAllRows();
                    return true;
            }
            return false;
        }
    }
}
