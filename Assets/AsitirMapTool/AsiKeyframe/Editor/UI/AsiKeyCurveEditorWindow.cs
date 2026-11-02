using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// Maya Graph Editor 式曲线编辑器。曲线模式轨道可拖动关键帧与切线；TCB 轨道只读显示（换算成欧拉角）。
    /// 一个关键帧带着轨道的全部通道，所以移动时间会影响该轨道所有通道。
    /// </summary>
    public class AsiKeyCurveEditorWindow : EditorWindow
    {
        private struct Channel
        {
            public AsiKeyTrack track;
            public int ch;
            public int rotCh;
            public string name;
            public Color color;
            public bool editable;
            public int id;
        }

        private struct SelKey
        {
            public AsiKeyTrack track;
            public AsiKey key;
            public int ch;
        }

        private struct DragOrig
        {
            public float frame;
            public float value;
        }

        private enum Drag { None, Pan, Box, Keys, TanIn, TanOut, Scrub }

        private readonly List<Channel> _channels = new List<Channel>();
        private readonly List<AsiKeyTrack> _tracks = new List<AsiKeyTrack>();
        private readonly HashSet<int> _hidden = new HashSet<int>();
        private readonly List<SelKey> _sel = new List<SelKey>();
        private readonly Dictionary<AsiKey, float> _origFrame = new Dictionary<AsiKey, float>();
        private readonly List<DragOrig> _origValue = new List<DragOrig>();

        private float _x0 = -5f;
        private float _ppf = 8f;
        private float _y0 = -100f;
        private float _ppv = 1.5f;
        private bool _needsFrame = true;
        private bool _onlySelected = true;
        private Vector2 _listScroll;
        private Drag _drag;
        private Vector2 _dragStart;
        private Vector2 _dragLast;
        private int _dragAxis;
        private SelKey _tanKey;
        private bool _dragged;

        private const float ListWidth = 210f;
        private const float RulerH = 18f;
        private const float HandleLen = 45f;
        private static readonly int GraphHint = "AsiKeyCurveGraph".GetHashCode();

        public static void Open()
        {
            AsiKeyCurveEditorWindow w = GetWindow<AsiKeyCurveEditorWindow>();
            w.titleContent = new GUIContent("K帧曲线");
            w.minSize = new Vector2(600f, 300f);
            w.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("K帧曲线");
            AsiKeySession.Repaint += Repaint;
            Undo.undoRedoPerformed += OnUndo;
        }

        private void OnDisable()
        {
            AsiKeySession.Repaint -= Repaint;
            Undo.undoRedoPerformed -= OnUndo;
        }

        private void OnUndo()
        {
            _sel.Clear();
            Repaint();
        }

        private void OnSelectionChange()
        {
            if (_onlySelected)
                _needsFrame = true;
            Repaint();
        }

        // ---------------- 通道 ----------------

        private void BuildChannels(AsiKeySession s)
        {
            AsiKeyProject p = s.project;
            _channels.Clear();
            _tracks.Clear();
            int layer = p.activeLayer;
            List<int> sel = s.SelectedBones();
            if (_onlySelected && sel.Count > 0)
            {
                s.TracksFor(sel, layer, _tracks, false);
            }
            else
            {
                List<AsiKeyTrack> all = p.layers[layer].tracks;
                for (int t = 0; t < all.Count; t++)
                {
                    if (all[t].keys.Count > 0)
                        _tracks.Add(all[t]);
                }
            }
            string[] axis = { "X", "Y", "Z" };
            Color[] cols = { new Color(1f, 0.3f, 0.3f), new Color(0.35f, 1f, 0.35f), new Color(0.4f, 0.6f, 1f) };
            for (int t = 0; t < _tracks.Count; t++)
            {
                AsiKeyTrack tr = _tracks[t];
                if (tr.keys.Count == 0)
                    continue;
                int m = p.MemberCount(tr);
                int rotCh = m * 3;
                string tname = p.TrackName(tr);
                for (int j = 0; j < m; j++)
                {
                    int bone = p.TrackBone(tr, j);
                    string member = tr.kind == AsiTrackKind.Chain && bone >= 0 ? p.bones[bone].name : tname;
                    float shade = m > 1 ? Mathf.Lerp(1f, 0.55f, j / (float)(m - 1)) : 1f;
                    for (int a = 0; a < 3; a++)
                        AddChannel(tr, j * 3 + a, rotCh, member + " 旋转" + axis[a], cols[a] * shade, t);
                }
                int mask = p.PosMask(tr);
                if (tr.kind == AsiTrackKind.Constraint || tr.kind == AsiTrackKind.LayerWeight)
                {
                    AddChannel(tr, rotCh, rotCh, tname + (tr.kind == AsiTrackKind.LayerWeight ? " 权重" : " 影响度"),
                        tr.kind == AsiTrackKind.LayerWeight ? AsiKeyLabels.LayerWeightColor : AsiKeyLabels.ConstraintColor, t);
                    continue;
                }
                for (int a = 0; a < 3; a++)
                {
                    if ((mask & (1 << a)) != 0)
                        AddChannel(tr, rotCh + a, rotCh, tname + " 位移" + axis[a], Color.Lerp(cols[a], Color.white, 0.4f), t);
                }
            }
        }

        private void AddChannel(AsiKeyTrack tr, int ch, int rotCh, string name, Color color, int trackIndex)
        {
            Channel c;
            c.track = tr;
            c.ch = ch;
            c.rotCh = rotCh;
            c.name = name;
            c.color = new Color(color.r, color.g, color.b, 1f);
            c.editable = tr.mode == AsiInterpMode.Curve;
            c.id = tr.kind.GetHashCode() * 7919 + tr.target * 131 + ch;
            _channels.Add(c);
        }

        private bool Visible(Channel c)
        {
            return !_hidden.Contains(c.id);
        }

        // ---------------- 坐标 ----------------

        private float FrameToX(float f) { return (f - _x0) * _ppf; }
        private float XToFrame(float x) { return _x0 + x / _ppf; }
        private float ValueToY(Rect g, float v) { return g.height - (v - _y0) * _ppv; }
        private float YToValue(Rect g, float y) { return _y0 + (g.height - y) / _ppv; }

        private float KeyValue(Channel c, AsiKey k, ref Vector3 hint)
        {
            if (c.editable)
                return AsiKeyInterpCurve.GetValue(k, c.ch, c.rotCh);
            return AsiKeyTrackEval.EvalChannel(c.track, c.ch, c.rotCh, k.frame, ref hint);
        }

        private Vector3 StartHint(Channel c)
        {
            if (c.ch >= c.rotCh || c.track.keys.Count == 0)
                return Vector3.zero;
            int m = c.ch / 3;
            AsiKey k = c.track.keys[0];
            return m < k.euler.Length ? k.euler[m] : Vector3.zero;
        }

        // ---------------- 绘制 ----------------

        private void OnGUI()
        {
            AsiKeySession s = AsiKeySession.Current;
            if (s == null || !s.Valid)
            {
                EditorGUILayout.HelpBox("先在「K帧工具」窗口打开并绑定一个工程。", MessageType.Info);
                return;
            }
            BuildChannels(s);
            PruneSelection();
            DrawToolbar(s);
            float top = EditorStyles.toolbar.fixedHeight > 0 ? EditorStyles.toolbar.fixedHeight : 21f;
            Rect list = new Rect(0f, top, ListWidth, position.height - top);
            Rect graph = new Rect(ListWidth + 1f, top, position.width - ListWidth - 1f, position.height - top);
            DrawChannelList(list);
            EditorGUI.DrawRect(new Rect(ListWidth, top, 1f, position.height - top), new Color(0f, 0f, 0f, 0.4f));
            if (_needsFrame && Event.current.type == EventType.Layout)
            {
                FrameView(graph, false);
                _needsFrame = false;
            }
            DrawGraph(s, graph);
            HandleKeys(s, graph);
        }

        private void PruneSelection()
        {
            for (int i = _sel.Count - 1; i >= 0; i--)
            {
                if (!_sel[i].track.keys.Contains(_sel[i].key) || ChannelIndex(_sel[i].track, _sel[i].ch) < 0)
                    _sel.RemoveAt(i);
            }
        }

        private int ChannelIndex(AsiKeyTrack tr, int ch)
        {
            for (int i = 0; i < _channels.Count; i++)
            {
                if (_channels[i].track == tr && _channels[i].ch == ch)
                    return i;
            }
            return -1;
        }

        private void DrawToolbar(AsiKeySession s)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            _onlySelected = GUILayout.Toggle(_onlySelected, new GUIContent("仅选中", "只显示选中控制器的曲线"), EditorStyles.toolbarButton, GUILayout.Width(44f));
            GUILayout.Space(6f);
            bool any = _sel.Count > 0;
            using (new EditorGUI.DisabledScope(!any))
            {
                TanButton("自动", AsiTangentType.Auto);
                TanButton("样条", AsiTangentType.Spline);
                TanButton("钳制", AsiTangentType.Clamped);
                TanButton("线性", AsiTangentType.Linear);
                TanButton("平直", AsiTangentType.Flat);
                TanButton("阶梯", AsiTangentType.Stepped);
                TanButton("高原", AsiTangentType.Plateau);
                GUILayout.Space(6f);
                if (GUILayout.Button(new GUIContent("断开", "入/出切线分开调整"), EditorStyles.toolbarButton, GUILayout.Width(36f)))
                    EditTangents(s, "断开切线", (ref AsiTangent t) => t.broken = true);
                if (GUILayout.Button(new GUIContent("统一", "入/出切线保持共线"), EditorStyles.toolbarButton, GUILayout.Width(36f)))
                    EditTangents(s, "统一切线", (ref AsiTangent t) =>
                    {
                        t.broken = false;
                        if (t.inType == AsiTangentType.Fixed || t.outType == AsiTangentType.Fixed)
                        {
                            t.inType = AsiTangentType.Fixed;
                            t.outType = AsiTangentType.Fixed;
                            t.outSlope = t.inSlope;
                        }
                    });
                if (GUILayout.Button(new GUIContent("权重", "切换权重切线"), EditorStyles.toolbarButton, GUILayout.Width(36f)))
                {
                    bool on = !AsiKeyInterpCurve.GetTan(_sel[0].key, _sel[0].ch).weighted;
                    EditTangents(s, "权重切线", (ref AsiTangent t) =>
                    {
                        t.weighted = on;
                        if (on && t.inWeight <= 0f) t.inWeight = 1f / 3f;
                        if (on && t.outWeight <= 0f) t.outWeight = 1f / 3f;
                    });
                }
            }
            GUILayout.Space(6f);
            if (GUILayout.Button(new GUIContent("欧拉过滤", "消除可见轨道的 ±180° 跳变"), EditorStyles.toolbarButton, GUILayout.Width(56f)))
            {
                Undo.RecordObject(s.project, "欧拉过滤");
                for (int t = 0; t < _tracks.Count; t++)
                    AsiKeyEdit.EulerFilter(s.project, _tracks[t]);
                Changed(s);
            }
            if (GUILayout.Button(new GUIContent("转为曲线", "把可见的 TCB 轨道转换为曲线插值，才能在这里编辑"), EditorStyles.toolbarButton, GUILayout.Width(56f)))
                ConvertVisible(s);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("全部 (A)"), EditorStyles.toolbarButton, GUILayout.Width(52f)))
                _needsFrame = true;
            if (GUILayout.Button(new GUIContent("选中 (F)"), EditorStyles.toolbarButton, GUILayout.Width(52f)))
                FrameView(GraphRect(), true);
            EditorGUILayout.EndHorizontal();
        }

        private Rect GraphRect()
        {
            float top = EditorStyles.toolbar.fixedHeight > 0 ? EditorStyles.toolbar.fixedHeight : 21f;
            return new Rect(ListWidth + 1f, top, position.width - ListWidth - 1f, position.height - top);
        }

        private void TanButton(string label, AsiTangentType type)
        {
            if (GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(34f)))
                SetTangentType(AsiKeySession.Current, type);
        }

        private void ConvertVisible(AsiKeySession s)
        {
            List<AsiKeyTrack> tcb = new List<AsiKeyTrack>();
            for (int t = 0; t < _tracks.Count; t++)
            {
                if (_tracks[t].mode == AsiInterpMode.TCB && _tracks[t].keys.Count > 0)
                    tcb.Add(_tracks[t]);
            }
            if (tcb.Count == 0)
                return;
            if (!EditorUtility.DisplayDialog("K帧曲线", "把 " + tcb.Count + " 条 TCB 轨道转换为曲线插值？关键帧之间的运动可能略有变化。", "转换", "取消"))
                return;
            Undo.RecordObject(s.project, "转为曲线");
            for (int t = 0; t < tcb.Count; t++)
                AsiKeyEdit.ConvertMode(s.project, tcb[t], AsiInterpMode.Curve);
            Changed(s);
        }

        private void DrawChannelList(Rect r)
        {
            GUILayout.BeginArea(r);
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
            AsiKeyTrack last = null;
            for (int i = 0; i < _channels.Count; i++)
            {
                Channel c = _channels[i];
                if (c.track != last)
                {
                    last = c.track;
                    string mode = c.track.mode == AsiInterpMode.TCB ? "  (TCB 只读)" : "";
                    GUILayout.Label(AsiKeySession.Current.project.TrackName(c.track) + mode, EditorStyles.miniBoldLabel);
                }
                Rect row = GUILayoutUtility.GetRect(10f, 16f, GUILayout.ExpandWidth(true));
                bool vis = Visible(c);
                Rect tog = new Rect(row.x + 6f, row.y + 1f, 14f, 14f);
                bool nv = GUI.Toggle(tog, vis, GUIContent.none);
                if (nv != vis)
                {
                    if (nv) _hidden.Remove(c.id); else _hidden.Add(c.id);
                }
                EditorGUI.DrawRect(new Rect(row.x + 24f, row.y + 5f, 10f, 6f), c.color);
                Rect label = new Rect(row.x + 38f, row.y, row.width - 38f, row.height);
                GUI.Label(label, c.name, EditorStyles.miniLabel);
                Event e = Event.current;
                if (e.type == EventType.MouseDown && e.button == 0 && label.Contains(e.mousePosition))
                {
                    if (e.control || e.command)
                    {
                        if (vis) _hidden.Add(c.id); else _hidden.Remove(c.id);
                    }
                    else
                    {
                        for (int k = 0; k < _channels.Count; k++)
                        {
                            if (k == i) _hidden.Remove(_channels[k].id);
                            else _hidden.Add(_channels[k].id);
                        }
                    }
                    _needsFrame = true;
                    e.Use();
                }
            }
            if (_channels.Count == 0)
                EditorGUILayout.HelpBox("当前层没有可显示的轨道。选中有关键帧的控制器，或关闭「仅选中」。", MessageType.None);
            else if (GUILayout.Button("显示全部通道", EditorStyles.miniButton))
            {
                _hidden.Clear();
                _needsFrame = true;
            }
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawGraph(AsiKeySession s, Rect graph)
        {
            EditorGUI.DrawRect(graph, new Color(0.16f, 0.16f, 0.16f));
            GUI.BeginClip(graph);
            Rect g = new Rect(0f, 0f, graph.width, graph.height);
            Rect area = new Rect(0f, RulerH, g.width, g.height - RulerH);
            if (Event.current.type == EventType.Repaint)
            {
                DrawGrid(s, g);
                for (int i = 0; i < _channels.Count; i++)
                {
                    if (Visible(_channels[i]))
                        DrawCurve(g, _channels[i]);
                }
                for (int i = 0; i < _channels.Count; i++)
                {
                    if (Visible(_channels[i]))
                        DrawKeys(g, _channels[i]);
                }
                float cx = FrameToX(s.frame);
                EditorGUI.DrawRect(new Rect(cx, 0f, 1f, g.height), new Color(1f, 0.25f, 0.25f, 0.9f));
                if (_drag == Drag.Box)
                    EditorGUI.DrawRect(BoxRect(_dragStart, Event.current.mousePosition), new Color(0.4f, 0.6f, 1f, 0.15f));
            }
            HandleGraphInput(s, g, area);
            GUI.EndClip();
        }

        private static Rect BoxRect(Vector2 a, Vector2 b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        private static float NiceStep(float pixelsPerUnit, float minPixels)
        {
            float raw = minPixels / Mathf.Max(1e-6f, pixelsPerUnit);
            float pow = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(raw)));
            float[] mul = { 1f, 2f, 5f, 10f };
            for (int i = 0; i < mul.Length; i++)
            {
                if (pow * mul[i] >= raw)
                    return pow * mul[i];
            }
            return pow * 10f;
        }

        private void DrawGrid(AsiKeySession s, Rect g)
        {
            AsiKeyProject p = s.project;
            float a = FrameToX(p.startFrame);
            float b = FrameToX(p.endFrame);
            if (a > 0f)
                EditorGUI.DrawRect(new Rect(0f, 0f, a, g.height), new Color(0f, 0f, 0f, 0.2f));
            if (b < g.width)
                EditorGUI.DrawRect(new Rect(b, 0f, g.width - b, g.height), new Color(0f, 0f, 0f, 0.2f));

            float fs = Mathf.Max(1f, NiceStep(_ppf, 50f));
            for (float f = Mathf.Floor(XToFrame(0f) / fs) * fs; FrameToX(f) < g.width; f += fs)
            {
                float x = FrameToX(f);
                EditorGUI.DrawRect(new Rect(x, RulerH, 1f, g.height - RulerH), new Color(1f, 1f, 1f, 0.06f));
                GUI.Label(new Rect(x + 2f, 1f, 50f, RulerH), f.ToString("0"), EditorStyles.miniLabel);
            }
            float vs = NiceStep(_ppv, 30f);
            for (float v = Mathf.Floor(YToValue(g, g.height) / vs) * vs; ValueToY(g, v) > RulerH; v += vs)
            {
                float y = ValueToY(g, v);
                bool zero = Mathf.Abs(v) < vs * 0.01f;
                EditorGUI.DrawRect(new Rect(0f, y, g.width, 1f), new Color(1f, 1f, 1f, zero ? 0.18f : 0.06f));
                GUI.Label(new Rect(3f, y - 14f, 60f, 14f), v.ToString(vs < 0.1f ? "0.###" : "0.#"), EditorStyles.miniLabel);
            }
            EditorGUI.DrawRect(new Rect(0f, 0f, g.width, RulerH), new Color(0.12f, 0.12f, 0.12f, 0.9f));
            for (float f = Mathf.Floor(XToFrame(0f) / fs) * fs; FrameToX(f) < g.width; f += fs)
                GUI.Label(new Rect(FrameToX(f) + 2f, 1f, 50f, RulerH), f.ToString("0"), EditorStyles.miniLabel);
        }

        private readonly List<Vector3> _pts = new List<Vector3>();

        private void DrawCurve(Rect g, Channel c)
        {
            List<AsiKey> keys = c.track.keys;
            if (keys.Count == 0)
                return;
            _pts.Clear();
            Vector3 hint = StartHint(c);
            float f0 = XToFrame(0f);
            float f1 = XToFrame(g.width);
            float first = keys[0].frame;
            float step = Mathf.Max(0.05f, 3f / _ppf);
            float startF = Mathf.Max(f0, first);
            if (first > f0)
            {
                float v0 = AsiKeyTrackEval.EvalChannel(c.track, c.ch, c.rotCh, first, ref hint);
                _pts.Add(new Vector3(0f, ValueToY(g, v0)));
            }
            else
            {
                for (int k = 0; k < keys.Count && keys[k].frame < f0; k++)
                    AsiKeyTrackEval.EvalChannel(c.track, c.ch, c.rotCh, keys[k].frame, ref hint);
            }
            for (float f = startF; f <= f1 + step; f += step)
            {
                float v = AsiKeyTrackEval.EvalChannel(c.track, c.ch, c.rotCh, f, ref hint);
                _pts.Add(new Vector3(FrameToX(f), ValueToY(g, v)));
            }
            if (_pts.Count < 2)
                return;
            Color col = c.editable ? c.color : Color.Lerp(c.color, Color.gray, 0.5f);
            Handles.color = col;
            Handles.DrawAAPolyLine(c.editable ? 2f : 1.5f, _pts.ToArray());
        }

        private bool IsSelected(AsiKeyTrack tr, AsiKey k, int ch)
        {
            for (int i = 0; i < _sel.Count; i++)
            {
                if (_sel[i].key == k && _sel[i].ch == ch && _sel[i].track == tr)
                    return true;
            }
            return false;
        }

        private void DrawKeys(Rect g, Channel c)
        {
            List<AsiKey> keys = c.track.keys;
            Vector3 hint = StartHint(c);
            for (int k = 0; k < keys.Count; k++)
            {
                AsiKey key = keys[k];
                float v = KeyValue(c, key, ref hint);
                Vector2 pt = new Vector2(FrameToX(key.frame), ValueToY(g, v));
                if (pt.x < -10f || pt.x > g.width + 10f)
                    continue;
                bool sel = IsSelected(c.track, key, c.ch);
                if (sel && c.editable)
                    DrawTangentHandles(g, c, k, pt);
                float size = c.editable ? 3.5f : 2.5f;
                Color col = sel ? Color.yellow : (c.editable ? Color.black : new Color(0.25f, 0.25f, 0.25f));
                EditorGUI.DrawRect(new Rect(pt.x - size - 1f, pt.y - size - 1f, size * 2f + 2f, size * 2f + 2f), c.editable ? c.color : Color.gray);
                EditorGUI.DrawRect(new Rect(pt.x - size, pt.y - size, size * 2f, size * 2f), col);
            }
        }

        private Vector2 TangentPoint(Rect g, Channel c, int k, bool isIn, Vector2 keyPt)
        {
            List<AsiKey> keys = c.track.keys;
            AsiKey key = keys[k];
            AsiTangent t = AsiKeyInterpCurve.GetTan(key, c.ch);
            float slope = isIn ? t.inSlope : t.outSlope;
            if (t.weighted)
            {
                int nb = isIn ? k - 1 : k + 1;
                float dt = nb >= 0 && nb < keys.Count ? Mathf.Abs(keys[nb].frame - key.frame) : 10f;
                float w = isIn ? t.inWeight : t.outWeight;
                float df = w * dt * (isIn ? -1f : 1f);
                float v = AsiKeyInterpCurve.GetValue(key, c.ch, c.rotCh) + slope * df;
                return new Vector2(FrameToX(key.frame + df), ValueToY(g, v));
            }
            Vector2 dir = new Vector2(_ppf, -slope * _ppv);
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector2.right;
            dir.Normalize();
            return keyPt + dir * (isIn ? -HandleLen : HandleLen);
        }

        private void DrawTangentHandles(Rect g, Channel c, int k, Vector2 pt)
        {
            AsiKey key = c.track.keys[k];
            AsiTangent t = AsiKeyInterpCurve.GetTan(key, c.ch);
            Handles.color = new Color(0.85f, 0.75f, 0.3f);
            if (k > 0)
            {
                Vector2 a = TangentPoint(g, c, k, true, pt);
                Handles.DrawAAPolyLine(1.5f, pt, a);
                EditorGUI.DrawRect(new Rect(a.x - 3f, a.y - 3f, 6f, 6f), t.inType == AsiTangentType.Fixed ? new Color(1f, 0.6f, 0.2f) : new Color(0.85f, 0.75f, 0.3f));
            }
            if (k < c.track.keys.Count - 1 && t.outType != AsiTangentType.Stepped)
            {
                Vector2 b = TangentPoint(g, c, k, false, pt);
                Handles.DrawAAPolyLine(1.5f, pt, b);
                EditorGUI.DrawRect(new Rect(b.x - 3f, b.y - 3f, 6f, 6f), t.outType == AsiTangentType.Fixed ? new Color(1f, 0.6f, 0.2f) : new Color(0.85f, 0.75f, 0.3f));
            }
        }

        // ---------------- 视图 ----------------

        private void FrameView(Rect graph, bool selectedOnly)
        {
            float fMin = float.MaxValue, fMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            for (int i = 0; i < _channels.Count; i++)
            {
                Channel c = _channels[i];
                if (!Visible(c))
                    continue;
                Vector3 hint = StartHint(c);
                List<AsiKey> keys = c.track.keys;
                for (int k = 0; k < keys.Count; k++)
                {
                    float v = KeyValue(c, keys[k], ref hint);
                    if (selectedOnly && !IsSelected(c.track, keys[k], c.ch))
                        continue;
                    fMin = Mathf.Min(fMin, keys[k].frame);
                    fMax = Mathf.Max(fMax, keys[k].frame);
                    vMin = Mathf.Min(vMin, v);
                    vMax = Mathf.Max(vMax, v);
                }
                if (!selectedOnly && keys.Count > 1)
                {
                    Vector3 sh = StartHint(c);
                    float a = keys[0].frame, b = keys[keys.Count - 1].frame;
                    for (int n = 0; n <= 40; n++)
                    {
                        float v = AsiKeyTrackEval.EvalChannel(c.track, c.ch, c.rotCh, Mathf.Lerp(a, b, n / 40f), ref sh);
                        vMin = Mathf.Min(vMin, v);
                        vMax = Mathf.Max(vMax, v);
                    }
                }
            }
            if (fMin == float.MaxValue)
            {
                AsiKeySession s = AsiKeySession.Current;
                fMin = s != null ? s.project.startFrame : 0f;
                fMax = s != null ? s.project.endFrame : 60f;
                vMin = -90f;
                vMax = 90f;
            }
            if (fMax - fMin < 1f) { fMin -= 5f; fMax += 5f; }
            if (vMax - vMin < 1e-3f) { vMin -= 1f; vMax += 1f; }
            float w = Mathf.Max(50f, graph.width - 40f);
            float h = Mathf.Max(50f, graph.height - RulerH - 30f);
            _ppf = w / (fMax - fMin);
            _x0 = fMin - 20f / _ppf;
            _ppv = h / (vMax - vMin);
            _y0 = vMin - 15f / _ppv;
            Repaint();
        }

        // ---------------- 输入 ----------------

        private bool HitKey(Rect g, Vector2 m, out SelKey hit)
        {
            hit = default(SelKey);
            float best = 7f;
            bool found = false;
            for (int i = 0; i < _channels.Count; i++)
            {
                Channel c = _channels[i];
                if (!Visible(c))
                    continue;
                Vector3 hint = StartHint(c);
                List<AsiKey> keys = c.track.keys;
                for (int k = 0; k < keys.Count; k++)
                {
                    float v = KeyValue(c, keys[k], ref hint);
                    Vector2 pt = new Vector2(FrameToX(keys[k].frame), ValueToY(g, v));
                    float d = Vector2.Distance(pt, m);
                    if (d < best)
                    {
                        best = d;
                        hit.track = c.track;
                        hit.key = keys[k];
                        hit.ch = c.ch;
                        found = true;
                    }
                }
            }
            return found;
        }

        private bool HitTangent(Rect g, Vector2 m, out SelKey hit, out bool isIn)
        {
            hit = default(SelKey);
            isIn = false;
            for (int i = 0; i < _sel.Count; i++)
            {
                SelKey sk = _sel[i];
                int ci = ChannelIndex(sk.track, sk.ch);
                if (ci < 0 || !_channels[ci].editable || !Visible(_channels[ci]))
                    continue;
                Channel c = _channels[ci];
                int k = sk.track.keys.IndexOf(sk.key);
                if (k < 0)
                    continue;
                Vector2 pt = new Vector2(FrameToX(sk.key.frame), ValueToY(g, AsiKeyInterpCurve.GetValue(sk.key, c.ch, c.rotCh)));
                if (k > 0 && Vector2.Distance(TangentPoint(g, c, k, true, pt), m) < 6f)
                {
                    hit = sk;
                    isIn = true;
                    return true;
                }
                AsiTangent t = AsiKeyInterpCurve.GetTan(sk.key, c.ch);
                if (k < sk.track.keys.Count - 1 && t.outType != AsiTangentType.Stepped && Vector2.Distance(TangentPoint(g, c, k, false, pt), m) < 6f)
                {
                    hit = sk;
                    isIn = false;
                    return true;
                }
            }
            return false;
        }

        private void HandleGraphInput(AsiKeySession s, Rect g, Rect area)
        {
            Event e = Event.current;
            Vector2 m = e.mousePosition;
            int id = GUIUtility.GetControlID(GraphHint, FocusType.Keyboard);
            EventType type = e.GetTypeForControl(id);
            if (e.rawType == EventType.MouseUp && _drag != Drag.None)
                type = EventType.MouseUp;
            switch (type)
            {
                case EventType.ScrollWheel:
                    if (!g.Contains(m))
                        return;
                    {
                        float zoom = e.delta.y > 0 ? 0.88f : 1.13f;
                        float af = XToFrame(m.x);
                        float av = YToValue(g, m.y);
                        if (!e.control)
                        {
                            _ppf = Mathf.Clamp(_ppf * zoom, 0.05f, 400f);
                            _x0 = af - m.x / _ppf;
                        }
                        if (!e.shift)
                        {
                            _ppv = Mathf.Clamp(_ppv * zoom, 1e-4f, 1e5f);
                            _y0 = av - (g.height - m.y) / _ppv;
                        }
                    }
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseDown:
                    if (!g.Contains(m))
                        return;
                    GUIUtility.keyboardControl = id;
                    _dragStart = m;
                    _dragLast = m;
                    _dragAxis = 0;
                    _dragged = false;
                    if (e.button == 2 || e.button == 0 && e.alt)
                    {
                        _drag = Drag.Pan;
                    }
                    else if (e.button == 0 && m.y < RulerH)
                    {
                        _drag = Drag.Scrub;
                        s.GoTo(Mathf.Round(XToFrame(m.x)));
                    }
                    else if (e.button == 1)
                    {
                        SelKey hk;
                        if (HitKey(g, m, out hk) && !IsSelected(hk.track, hk.key, hk.ch))
                        {
                            _sel.Clear();
                            _sel.Add(hk);
                        }
                        ShowMenu(s);
                        e.Use();
                        return;
                    }
                    else if (e.button == 0)
                    {
                        SelKey th;
                        bool isIn;
                        SelKey hk;
                        if (HitTangent(g, m, out th, out isIn))
                        {
                            Undo.RecordObject(s.project, "调整切线");
                            _tanKey = th;
                            _drag = isIn ? Drag.TanIn : Drag.TanOut;
                        }
                        else if (HitKey(g, m, out hk))
                        {
                            bool add = e.shift;
                            bool toggle = e.control || e.command;
                            bool was = IsSelected(hk.track, hk.key, hk.ch);
                            if (toggle)
                            {
                                if (was) RemoveSel(hk); else _sel.Add(hk);
                            }
                            else if (!was)
                            {
                                if (!add) _sel.Clear();
                                _sel.Add(hk);
                            }
                            if (e.clickCount == 2)
                                s.GoTo(hk.key.frame);
                            BeginKeyDrag(s);
                            _drag = Drag.Keys;
                        }
                        else
                        {
                            if (!e.shift && !e.control && !e.command)
                                _sel.Clear();
                            _drag = Drag.Box;
                        }
                    }
                    if (_drag != Drag.None)
                        GUIUtility.hotControl = id;
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseDrag:
                    if (_drag == Drag.None || GUIUtility.hotControl != id)
                        return;
                    switch (_drag)
                    {
                        case Drag.Pan:
                            _x0 -= e.delta.x / _ppf;
                            _y0 += e.delta.y / _ppv;
                            break;
                        case Drag.Scrub:
                            s.GoTo(Mathf.Round(XToFrame(m.x)));
                            break;
                        case Drag.Keys:
                            DragKeys(s, g, m, e.shift);
                            break;
                        case Drag.TanIn:
                        case Drag.TanOut:
                            DragTangent(s, g, m, _drag == Drag.TanIn);
                            break;
                    }
                    _dragLast = m;
                    _dragged = true;
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseUp:
                    if (_drag == Drag.None)
                        return;
                    if (GUIUtility.hotControl == id)
                        GUIUtility.hotControl = 0;
                    if (_drag == Drag.Box)
                        BoxSelect(g, BoxRect(_dragStart, m));
                    else if (_drag == Drag.Keys && _dragged)
                        EndKeyDrag(s);
                    else if ((_drag == Drag.TanIn || _drag == Drag.TanOut) && _dragged)
                        Changed(s);
                    _drag = Drag.None;
                    e.Use();
                    Repaint();
                    break;
            }
        }

        private void RemoveSel(SelKey k)
        {
            for (int i = _sel.Count - 1; i >= 0; i--)
            {
                if (_sel[i].key == k.key && _sel[i].ch == k.ch && _sel[i].track == k.track)
                    _sel.RemoveAt(i);
            }
        }

        private void BoxSelect(Rect g, Rect box)
        {
            for (int i = 0; i < _channels.Count; i++)
            {
                Channel c = _channels[i];
                if (!Visible(c))
                    continue;
                Vector3 hint = StartHint(c);
                List<AsiKey> keys = c.track.keys;
                for (int k = 0; k < keys.Count; k++)
                {
                    float v = KeyValue(c, keys[k], ref hint);
                    Vector2 pt = new Vector2(FrameToX(keys[k].frame), ValueToY(g, v));
                    if (box.Contains(pt) && !IsSelected(c.track, keys[k], c.ch))
                    {
                        SelKey sk;
                        sk.track = c.track;
                        sk.key = keys[k];
                        sk.ch = c.ch;
                        _sel.Add(sk);
                    }
                }
            }
        }

        private void BeginKeyDrag(AsiKeySession s)
        {
            Undo.RecordObject(s.project, "移动关键帧");
            _origFrame.Clear();
            _origValue.Clear();
            for (int i = 0; i < _sel.Count; i++)
            {
                SelKey sk = _sel[i];
                if (!_origFrame.ContainsKey(sk.key))
                    _origFrame[sk.key] = sk.key.frame;
                DragOrig o;
                o.frame = sk.key.frame;
                int ci = ChannelIndex(sk.track, sk.ch);
                o.value = ci >= 0 ? AsiKeyInterpCurve.GetValue(sk.key, sk.ch, _channels[ci].rotCh) : 0f;
                _origValue.Add(o);
            }
        }

        private void DragKeys(AsiKeySession s, Rect g, Vector2 m, bool constrain)
        {
            Vector2 total = m - _dragStart;
            if (constrain && _dragAxis == 0 && total.magnitude > 4f)
                _dragAxis = Mathf.Abs(total.x) > Mathf.Abs(total.y) ? 1 : 2;
            if (!constrain)
                _dragAxis = 0;
            float df = _dragAxis == 2 ? 0f : Mathf.Round(total.x / _ppf);
            float dv = _dragAxis == 1 ? 0f : -total.y / _ppv;
            HashSet<AsiKeyTrack> touched = new HashSet<AsiKeyTrack>();
            foreach (KeyValuePair<AsiKey, float> pair in _origFrame)
            {
                AsiKeyTrack tr = TrackOfSel(pair.Key);
                if (tr == null || tr.mode != AsiInterpMode.Curve)
                    continue;
                pair.Key.frame = pair.Value + df;
                touched.Add(tr);
            }
            for (int i = 0; i < _sel.Count && i < _origValue.Count; i++)
            {
                SelKey sk = _sel[i];
                int ci = ChannelIndex(sk.track, sk.ch);
                if (ci < 0 || !_channels[ci].editable)
                    continue;
                AsiKeyInterpCurve.SetValue(sk.key, sk.ch, _channels[ci].rotCh, _origValue[i].value + dv);
                if (sk.ch < _channels[ci].rotCh)
                    AsiKeyEdit.SyncRotFromEuler(sk.track, sk.key);
                touched.Add(sk.track);
            }
            foreach (AsiKeyTrack tr in touched)
            {
                AsiKeyEdit.SortKeys(tr);
                AsiKeyEdit.RecomputeTangents(s.project, tr);
            }
            Changed(s, false);
        }

        private AsiKeyTrack TrackOfSel(AsiKey k)
        {
            for (int i = 0; i < _sel.Count; i++)
            {
                if (_sel[i].key == k)
                    return _sel[i].track;
            }
            return null;
        }

        private void EndKeyDrag(AsiKeySession s)
        {
            HashSet<AsiKeyTrack> touched = new HashSet<AsiKeyTrack>();
            for (int i = 0; i < _sel.Count; i++)
                touched.Add(_sel[i].track);
            foreach (AsiKeyTrack tr in touched)
            {
                AsiKeyEdit.SortKeys(tr);
                for (int k = tr.keys.Count - 1; k >= 1; k--)
                {
                    if (Mathf.Abs(tr.keys[k].frame - tr.keys[k - 1].frame) >= 0.01f)
                        continue;
                    bool movedK = _origFrame.ContainsKey(tr.keys[k]);
                    tr.keys.RemoveAt(movedK ? k - 1 : k);
                }
                AsiKeyEdit.AfterEdit(s.project, tr, LayerOf(s.project, tr), null);
            }
            _origFrame.Clear();
            Changed(s);
        }

        private static int LayerOf(AsiKeyProject p, AsiKeyTrack tr)
        {
            for (int l = 0; l < p.layers.Count; l++)
            {
                if (p.layers[l].tracks.Contains(tr))
                    return l;
            }
            return 0;
        }

        private void DragTangent(AsiKeySession s, Rect g, Vector2 m, bool isIn)
        {
            SelKey sk = _tanKey;
            int ci = ChannelIndex(sk.track, sk.ch);
            if (ci < 0 || sk.key.tan == null || sk.ch >= sk.key.tan.Length)
                return;
            Channel c = _channels[ci];
            float kf = sk.key.frame;
            float kv = AsiKeyInterpCurve.GetValue(sk.key, sk.ch, c.rotCh);
            float mf = XToFrame(m.x);
            float mv = YToValue(g, m.y);
            float df = mf - kf;
            if (isIn) df = Mathf.Min(df, -1e-3f); else df = Mathf.Max(df, 1e-3f);
            float slope = (mv - kv) / df;
            AsiTangent t = sk.key.tan[sk.ch];
            if (isIn)
            {
                t.inSlope = slope;
                t.inType = AsiTangentType.Fixed;
            }
            else
            {
                t.outSlope = slope;
                t.outType = AsiTangentType.Fixed;
            }
            if (!t.broken)
            {
                if (isIn) { t.outSlope = slope; if (t.outType != AsiTangentType.Stepped) t.outType = AsiTangentType.Fixed; }
                else { t.inSlope = slope; t.inType = AsiTangentType.Fixed; }
            }
            if (t.weighted)
            {
                List<AsiKey> keys = sk.track.keys;
                int k = keys.IndexOf(sk.key);
                int nb = isIn ? k - 1 : k + 1;
                if (nb >= 0 && nb < keys.Count)
                {
                    float dt = Mathf.Abs(keys[nb].frame - kf);
                    float w = Mathf.Clamp(Mathf.Abs(df) / Mathf.Max(1e-3f, dt), 0.01f, 1f);
                    if (isIn) t.inWeight = w; else t.outWeight = w;
                }
            }
            sk.key.tan[sk.ch] = t;
            AsiKeyEdit.RecomputeTangents(s.project, sk.track);
            Changed(s, false);
        }

        private delegate void TanEdit(ref AsiTangent t);

        private void EditTangents(AsiKeySession s, string undo, TanEdit fn)
        {
            if (s == null)
                return;
            Undo.RecordObject(s.project, undo);
            HashSet<AsiKeyTrack> touched = new HashSet<AsiKeyTrack>();
            for (int i = 0; i < _sel.Count; i++)
            {
                SelKey sk = _sel[i];
                if (sk.track.mode != AsiInterpMode.Curve || sk.key.tan == null || sk.ch >= sk.key.tan.Length)
                    continue;
                AsiTangent t = sk.key.tan[sk.ch];
                fn(ref t);
                sk.key.tan[sk.ch] = t;
                touched.Add(sk.track);
            }
            foreach (AsiKeyTrack tr in touched)
                AsiKeyEdit.RecomputeTangents(s.project, tr);
            Changed(s);
        }

        private void SetTangentType(AsiKeySession s, AsiTangentType type)
        {
            EditTangents(s, "切线类型", (ref AsiTangent t) =>
            {
                if (type != AsiTangentType.Stepped)
                    t.inType = type;
                t.outType = type;
            });
        }

        private void DeleteSelected(AsiKeySession s)
        {
            if (_sel.Count == 0)
                return;
            Undo.RecordObject(s.project, "删除关键帧");
            HashSet<AsiKeyTrack> touched = new HashSet<AsiKeyTrack>();
            for (int i = 0; i < _sel.Count; i++)
            {
                if (_sel[i].track.keys.Remove(_sel[i].key))
                    touched.Add(_sel[i].track);
            }
            foreach (AsiKeyTrack tr in touched)
                AsiKeyEdit.AfterEdit(s.project, tr, LayerOf(s.project, tr), null);
            _sel.Clear();
            Changed(s);
        }

        private void ShowMenu(AsiKeySession s)
        {
            GenericMenu menu = new GenericMenu();
            bool any = _sel.Count > 0;
            string[] names = { "自动", "样条", "钳制", "线性", "平直", "阶梯", "高原" };
            AsiTangentType[] types =
            {
                AsiTangentType.Auto, AsiTangentType.Spline, AsiTangentType.Clamped, AsiTangentType.Linear,
                AsiTangentType.Flat, AsiTangentType.Stepped, AsiTangentType.Plateau
            };
            for (int i = 0; i < names.Length; i++)
            {
                AsiTangentType t = types[i];
                if (any) menu.AddItem(new GUIContent("切线/" + names[i]), false, () => SetTangentType(s, t));
                else menu.AddDisabledItem(new GUIContent("切线/" + names[i]));
            }
            if (any)
            {
                menu.AddItem(new GUIContent("切线/断开"), false, () => EditTangents(s, "断开切线", (ref AsiTangent t) => t.broken = true));
                menu.AddItem(new GUIContent("切线/统一"), false, () => EditTangents(s, "统一切线", (ref AsiTangent t) => t.broken = false));
                menu.AddItem(new GUIContent("切线/权重切线 开"), false, () => EditTangents(s, "权重切线", (ref AsiTangent t) => t.weighted = true));
                menu.AddItem(new GUIContent("切线/权重切线 关"), false, () => EditTangents(s, "权重切线", (ref AsiTangent t) => t.weighted = false));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("删除关键帧（轨道上该帧的所有通道）"), false, () => DeleteSelected(s));
                menu.AddItem(new GUIContent("跳到该帧"), false, () => s.GoTo(_sel[0].key.frame));
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("显示全部 (A)"), false, () => _needsFrame = true);
            menu.AddItem(new GUIContent("显示选中 (F)"), false, () => FrameView(GraphRect(), true));
            menu.ShowAsContext();
        }

        private void HandleKeys(AsiKeySession s, Rect graph)
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || EditorGUIUtility.editingTextField)
                return;
            switch (e.keyCode)
            {
                case KeyCode.A:
                    if (e.control || e.command)
                    {
                        _sel.Clear();
                        BoxSelect(new Rect(0f, 0f, graph.width, graph.height), new Rect(-1e6f, -1e6f, 2e6f, 2e6f));
                    }
                    else
                    {
                        _needsFrame = true;
                    }
                    break;
                case KeyCode.F:
                    FrameView(graph, _sel.Count > 0);
                    break;
                case KeyCode.Delete:
                case KeyCode.Backspace:
                    DeleteSelected(s);
                    break;
                case KeyCode.Comma:
                    s.GoTo(Mathf.Round(s.frame) - 1f);
                    break;
                case KeyCode.Period:
                    s.GoTo(Mathf.Round(s.frame) + 1f);
                    break;
                default:
                    return;
            }
            e.Use();
            Repaint();
        }

        private void Changed(AsiKeySession s, bool dirty = true)
        {
            s.project.Touch();
            if (dirty)
                EditorUtility.SetDirty(s.project);
            s.Refresh();
        }
    }
}
