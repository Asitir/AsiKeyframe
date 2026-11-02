using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AsitirTool.Editor
{
    /// <summary>Scene 视图里绘制控制器外形、父子连线、IK 目标、末端轨迹，并提供点选与「仅选骨骼」选择模式。</summary>
    public static class AsiKeyRigDrawer
    {
        private const string FilterKey = "AsiKey.SelectFilter";
        private const float LineTol = 6f;
        private const float HoverDistance = 3f;
        // 等于 Unity 默认框选控件的距离；后注册者在相同距离下胜出。
        private const float EmptyDistance = 5f;
        private const float DragThreshold = 6f;
        private const float StickJoint = 0.035f;
        private static readonly int SelectHint = "AsiKeyRigSelect".GetHashCode();
        private static readonly int PickHint = "AsiKeyConstraintPick".GetHashCode();

        /// <summary>实际绘制和点选用的几何：骨骼样式不是「按外形设定」时覆盖外形种类（质心除外）。</summary>
        private enum Look { Box, Ring, Rect, Mesh, Octa, Stick }
        private static int s_filter = -1;
        private static int s_selectId;
        private static int s_hover = -1;
        private static int s_firstClick = -1;
        private static bool s_marquee;
        private static Vector2 s_pressPos;
        private static Vector2 s_dragPos;

        private static readonly Vector3[] s_local = new Vector3[8];
        private static readonly Vector3[] s_world = new Vector3[8];
        private static readonly Vector3[] s_quad = new Vector3[4];
        private static readonly Vector3[] s_tri = new Vector3[3];
        private static readonly Vector3[] s_seg = new Vector3[2];
        private static readonly Vector3[] s_ring = new Vector3[AsiKeyShapes.RingSegments + 1];
        private static readonly Vector3[] s_rect = new Vector3[5];
        private static readonly Vector2[] s_gui = new Vector2[AsiKeyShapes.RingSegments + 1];
        private static readonly Vector2[] s_face = new Vector2[4];
        private static Vector3[] s_traj = new Vector3[256];
        private static readonly List<int> s_trajBones = new List<int>();
        private static readonly List<AsiKeyTrack> s_tracks = new List<AsiKeyTrack>();
        private static readonly List<int> s_one = new List<int>(1);
        private static Material s_mat;
        private static GUIStyle s_overlay;

        /// <summary>
        /// Scene 里的选择筛选（见 <see cref="AsiSelectFilter"/>）。不是 None 时点选和框选只作用于通过筛选的控制器，点空白处清空选择。
        /// </summary>
        public static AsiSelectFilter Filter
        {
            get
            {
                if (s_filter < 0)
                    s_filter = Mathf.Clamp(EditorPrefs.GetInt(FilterKey, 0), 0, (int)AsiSelectFilter.Custom);
                return (AsiSelectFilter)s_filter;
            }
            set
            {
                if (value == Filter)
                    return;
                s_filter = (int)value;
                EditorPrefs.SetInt(FilterKey, s_filter);
                s_hover = -1;
                s_marquee = false;
                SceneView.RepaintAll();
                AsiKeySession.RaiseRepaint();
            }
        }

        public static string FilterLabel(AsiKeyProject p, AsiSelectFilter f)
        {
            switch (f)
            {
                case AsiSelectFilter.Bones: return "仅选骨骼";
                case AsiSelectFilter.Units: return "仅选工程内单位";
                case AsiSelectFilter.OneUnit: return "仅选择 " + (p != null && p.ValidUnit(p.selectFilterUnit) ? p.units[p.selectFilterUnit].name : "(角色)") + " 的骨骼";
                case AsiSelectFilter.Custom: return "自定义可选单位";
                default: return "不筛选";
            }
        }

        /// <summary>只选到某个单位：切到「仅选择该角色骨骼」筛选。</summary>
        public static void FilterOneUnit(AsiKeyProject p, int unit)
        {
            if (p == null || !p.ValidUnit(unit))
                return;
            if (p.selectFilterUnit != unit)
            {
                Undo.RecordObject(p, "选择筛选");
                p.selectFilterUnit = unit;
                EditorUtility.SetDirty(p);
            }
            Filter = AsiSelectFilter.OneUnit;
            SceneView.RepaintAll();
            AsiKeySession.RaiseRepaint();
        }

        /// <summary>骨骼是否通过当前选择筛选（不看可见性）。</summary>
        public static bool PassesFilter(AsiKeyProject p, int i)
        {
            int u = p.bones[i].unit;
            switch (Filter)
            {
                case AsiSelectFilter.Bones: return p.IsCharacter(u);
                case AsiSelectFilter.OneUnit: return u == p.selectFilterUnit;
                case AsiSelectFilter.Custom: return !p.ValidUnit(u) || p.units[u].selectable;
                default: return true;
            }
        }

        /// <summary>可以在 Scene 里点选 / 框选：可见且通过选择筛选。</summary>
        public static bool Selectable(AsiKeySession s, int i)
        {
            return Pickable(s, i) && PassesFilter(s.project, i);
        }

        public static void OnSceneGUI(SceneView view)
        {
            AsiKeySession s = AsiKeySession.Current;
            if (s == null || !s.Valid)
            {
                AsiKeyGizmo.Release();
                return;
            }
            Event e = Event.current;
            bool picking = s.ScenePicking && view.camera != null;
            AsiKeySceneMenu.HandleCommands(s, e);
            if (picking)
                HandlePick(s, view, e);
            else
            {
                AsiKeySceneMenu.HandleContextClick(s, view, e);
                HandleDoubleClick(s, view, e);
            }
            if (!AsiKeyFingerHandle.Input(s, view, e))
                AsiKeyGizmo.Input(s, view, e);
            if (!picking && Filter != AsiSelectFilter.None && view.camera != null)
                HandleSelection(s, view, e);
            else if (!picking)
                s_hover = -1;
            CompareFunction prevZ = Handles.zTest;
            Color prevColor = Handles.color;
            if (e.type == EventType.Repaint)
                DrawRig(s, view.camera);
            if (s.project.showTrajectory)
                DrawTrajectories(s, e);
            Handles.zTest = prevZ;
            Handles.color = prevColor;
            AsiKeyGizmo.Draw(s, view);
            AsiKeyFingerHandle.Draw(s, view);
            DrawOverlay(s, view);
        }

        /// <summary>
        /// 双击控制器选中它和全部子级（层级选择）。要在变换轴之前处理：第一下选中后出现的变换轴会挡住第二下。
        /// </summary>
        private static void HandleDoubleClick(AsiKeySession s, SceneView view, Event e)
        {
            if (e.type != EventType.MouseDown || e.button != 0 || e.alt || view.camera == null || GUIUtility.hotControl != 0)
                return;
            int hit = PickIndex(s, view.camera, e.mousePosition);
            if (e.clickCount == 2 && hit >= 0 && hit == s_firstClick && !e.shift && !EditorGUI.actionKey)
            {
                s_firstClick = -1;
                s.SelectHierarchy(hit);
                e.Use();
                return;
            }
            s_firstClick = e.clickCount == 1 ? hit : -1;
        }

        private static CompareFunction ZTest(AsiKeyProject p)
        {
            return p.xray ? CompareFunction.Always : CompareFunction.LessEqual;
        }

        private static Look LookOf(AsiKeyProject p, AsiKeyBone b)
        {
            if (b.isCom || p.boneDisplay == AsiBoneDisplay.Shape)
            {
                switch (b.shape.kind)
                {
                    case AsiShapeKind.Ring: return Look.Ring;
                    case AsiShapeKind.Rect: return Look.Rect;
                    case AsiShapeKind.Mesh: return Look.Mesh;
                    default: return Look.Box;
                }
            }
            switch (p.boneDisplay)
            {
                case AsiBoneDisplay.Ring: return Look.Ring;
                case AsiBoneDisplay.Rect: return Look.Rect;
                case AsiBoneDisplay.Octahedron: return Look.Octa;
                case AsiBoneDisplay.Stick: return Look.Stick;
                default: return Look.Box;
            }
        }

        private static void DrawRig(AsiKeySession s, Camera cam)
        {
            AsiKeyProject p = s.project;
            bool marquee = MarqueeActive && cam != null;
            Rect marqueeRect = MarqueeRect();
            Handles.zTest = ZTest(p);
            Vector3 view = cam != null ? cam.transform.forward : Vector3.forward;
            if (p.showLinks)
            {
                Handles.color = new Color(0.8f, 0.8f, 0.8f, 0.45f);
                for (int i = 0; i < p.bones.Count; i++)
                {
                    int parent = p.LinkParent(i);
                    if (parent < 0 || !Pickable(s, i) || !Pickable(s, parent))
                        continue;
                    Handles.DrawDottedLine(s.controls[parent].position, s.controls[i].position, 3f);
                }
            }
            for (int i = 0; i < p.bones.Count; i++)
            {
                Transform c = s.controls[i];
                if (c == null)
                    continue;
                AsiKeyBone b = p.bones[i];
                AsiKeyShape sh = b.shape;
                bool sel = s.IsSelected(i);
                if (!Pickable(s, i))
                {
                    if (sel)
                    {
                        Handles.color = Color.white;
                        Handles.DrawWireDisc(c.position, view, HandleUtility.GetHandleSize(c.position) * 0.05f);
                    }
                    continue;
                }
                bool canSelect = s.ScenePicking || PassesFilter(p, i);
                bool hot = !sel && canSelect && (i == s_hover || marquee && InRect(s, i, cam, marqueeRect));
                Color col = sh.color;
                if (sel)
                    col = Color.Lerp(col, Color.white, 0.55f);
                else if (hot)
                    col = Color.Lerp(col, Color.white, 0.3f);
                else if (!canSelect)
                    col = new Color(col.r * 0.6f, col.g * 0.6f, col.b * 0.6f, col.a * 0.45f);
                Matrix4x4 m = AsiKeyShapes.ShapeMatrix(b, c, p.shapeScale);
                float width = sel ? 3.5f : hot ? 2.75f : 2f;
                switch (LookOf(p, b))
                {
                    case Look.Box:
                        AsiKeyShapes.BoxCorners(sh, s_local);
                        DrawBox(m, col, sel, width);
                        break;
                    case Look.Ring:
                        AsiKeyShapes.RingPoints(sh, s_ring);
                        for (int k = 0; k < s_ring.Length; k++)
                            s_ring[k] = m.MultiplyPoint3x4(s_ring[k]);
                        Handles.color = col;
                        Handles.DrawAAPolyLine(width + 0.5f, s_ring);
                        if (b.isCom)
                            DrawComCross(m, sh, col);
                        break;
                    case Look.Rect:
                        AsiKeyShapes.RectPoints(sh, s_rect);
                        for (int k = 0; k < s_rect.Length; k++)
                            s_rect[k] = m.MultiplyPoint3x4(s_rect[k]);
                        Handles.color = col;
                        Handles.DrawAAPolyLine(width, s_rect);
                        break;
                    case Look.Mesh:
                        DrawMesh(m, sh, col, sel, p);
                        break;
                    case Look.Octa:
                        DrawOcta(AsiKeyShapes.BoneMatrix(b, c), b, p.shapeScale, col, sel, width);
                        break;
                    case Look.Stick:
                        DrawStick(AsiKeyShapes.BoneMatrix(b, c), b, c.position, view, col, width);
                        break;
                }
            }
            DrawIKTargets(s);
            DrawConstraints(s);
        }

        /// <summary>约束连线：被约束骨骼到各目标（链接约束到当前父级），线宽随影响度变化。</summary>
        private static void DrawConstraints(AsiKeySession s)
        {
            AsiKeyProject p = s.project;
            for (int k = 0; k < p.constraints.Count; k++)
            {
                AsiKeyConstraint c = p.constraints[k];
                if (!c.enabled || c.bone < 0 || c.bone >= p.bones.Count || !Pickable(s, c.bone))
                    continue;
                float inf = AsiKeyConstraints.Influence(p, s.pose, k);
                Color col = AsiKeyLabels.ConstraintColor;
                col.a = Mathf.Lerp(0.25f, 0.9f, inf);
                Handles.color = col;
                Vector3 from = s.controls[c.bone].position;
                if (c.kind == AsiConstraintKind.Link)
                {
                    int e = AsiKeyConstraints.ActiveLink(c, s.frame);
                    if (e >= 0)
                        DrawConstraintLine(s, from, c.links[e].bone, c.links[e].obj, true);
                    continue;
                }
                for (int t = 0; t < c.targets.Count; t++)
                    DrawConstraintLine(s, from, c.targets[t].bone, c.targets[t].obj, false);
            }
        }

        private static void DrawConstraintLine(AsiKeySession s, Vector3 from, int bone, string obj, bool worldIsRoot)
        {
            Vector3 to;
            Transform t = bone < 0 && !string.IsNullOrEmpty(obj) ? s.ctx.Resolve(obj) : null;
            if (bone >= 0 && bone < s.controls.Length && s.controls[bone] != null)
                to = s.controls[bone].position;
            else if (t != null)
                to = t.position;
            else if (worldIsRoot && bone < 0 && string.IsNullOrEmpty(obj))
                to = s.ctx.ToWorldPos(Vector3.zero);
            else
                return;
            Handles.DrawDottedLine(from, to, 4f);
            Handles.DrawWireDisc(to, Camera.current != null ? Camera.current.transform.forward : Vector3.forward, HandleUtility.GetHandleSize(to) * 0.04f);
        }

        private static void DrawOcta(Matrix4x4 m, AsiKeyBone b, float scale, Color col, bool sel, float width)
        {
            AsiKeyShapes.OctaCorners(b, scale, s_local);
            for (int k = 0; k < 6; k++)
                s_world[k] = m.MultiplyPoint3x4(s_local[k]);
            Handles.color = new Color(col.r, col.g, col.b, sel ? 0.4f : 0.22f);
            for (int f = 0; f < AsiKeyShapes.OctaFaces.Length; f++)
            {
                int[] face = AsiKeyShapes.OctaFaces[f];
                for (int k = 0; k < 3; k++)
                    s_tri[k] = s_world[face[k]];
                Handles.DrawAAConvexPolygon(s_tri);
            }
            Handles.color = col;
            for (int e = 0; e < AsiKeyShapes.OctaEdges.Length; e += 2)
            {
                s_seg[0] = s_world[AsiKeyShapes.OctaEdges[e]];
                s_seg[1] = s_world[AsiKeyShapes.OctaEdges[e + 1]];
                Handles.DrawAAPolyLine(width * 0.75f, s_seg);
            }
        }

        private static void DrawStick(Matrix4x4 m, AsiKeyBone b, Vector3 joint, Vector3 view, Color col, float width)
        {
            Handles.color = col;
            s_seg[0] = joint;
            s_seg[1] = m.MultiplyPoint3x4(AsiKeyShapes.BoneTip(b));
            Handles.DrawAAPolyLine(width + 1f, s_seg);
            Handles.DrawSolidDisc(joint, view, HandleUtility.GetHandleSize(joint) * StickJoint);
        }

        private static void DrawBox(Matrix4x4 m, Color col, bool sel, float width)
        {
            for (int k = 0; k < 8; k++)
                s_world[k] = m.MultiplyPoint3x4(s_local[k]);
            Handles.color = new Color(col.r, col.g, col.b, sel ? 0.32f : 0.16f);
            for (int f = 0; f < AsiKeyShapes.BoxFaces.Length; f++)
            {
                int[] face = AsiKeyShapes.BoxFaces[f];
                for (int k = 0; k < 4; k++)
                    s_quad[k] = s_world[face[k]];
                Handles.DrawAAConvexPolygon(s_quad);
            }
            Handles.color = col;
            for (int e = 0; e < AsiKeyShapes.BoxEdges.Length; e += 2)
            {
                s_seg[0] = s_world[AsiKeyShapes.BoxEdges[e]];
                s_seg[1] = s_world[AsiKeyShapes.BoxEdges[e + 1]];
                Handles.DrawAAPolyLine(width, s_seg);
            }
        }

        private static void DrawComCross(Matrix4x4 m, AsiKeyShape sh, Color col)
        {
            Handles.color = new Color(col.r, col.g, col.b, 0.8f);
            float r = Mathf.Min(sh.radiusX, sh.radiusY) * 0.25f;
            s_seg[0] = m.MultiplyPoint3x4(sh.offset + new Vector3(-r, 0f, 0f));
            s_seg[1] = m.MultiplyPoint3x4(sh.offset + new Vector3(r, 0f, 0f));
            Handles.DrawAAPolyLine(2f, s_seg);
            s_seg[0] = m.MultiplyPoint3x4(sh.offset + new Vector3(0f, -r, 0f));
            s_seg[1] = m.MultiplyPoint3x4(sh.offset + new Vector3(0f, r, 0f));
            Handles.DrawAAPolyLine(2f, s_seg);
            s_seg[0] = m.MultiplyPoint3x4(sh.offset + new Vector3(0f, 0f, -r));
            s_seg[1] = m.MultiplyPoint3x4(sh.offset + new Vector3(0f, 0f, r));
            Handles.DrawAAPolyLine(2f, s_seg);
        }

        private static void DrawMesh(Matrix4x4 m, AsiKeyShape sh, Color col, bool sel, AsiKeyProject p)
        {
            if (sh.mesh == null)
            {
                AsiKeyShapes.MeshBoxCorners(sh, s_local);
                DrawBox(m, col, sel, 1f);
                return;
            }
            if (s_mat == null)
            {
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                s_mat = new Material(shader);
                s_mat.hideFlags = HideFlags.HideAndDontSave;
                s_mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                s_mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                s_mat.SetInt("_Cull", (int)CullMode.Off);
                s_mat.SetInt("_ZWrite", 0);
            }
            s_mat.SetColor("_Color", new Color(col.r, col.g, col.b, sel ? 0.55f : 0.35f));
            s_mat.SetInt("_ZTest", (int)ZTest(p));
            s_mat.SetPass(0);
            Matrix4x4 mm = m * Matrix4x4.TRS(sh.offset, Quaternion.identity, sh.meshScale);
            Graphics.DrawMeshNow(sh.mesh, mm);
        }

        private static void DrawIKTargets(AsiKeySession s)
        {
            AsiKeyProject p = s.project;
            for (int c = 0; c < p.chains.Count && c < s.pose.chainCount; c++)
            {
                AsiChainState st = s.pose.chains[c];
                if (!st.valid || st.blend < 0.01f)
                    continue;
                AsiKeyChain chain = p.chains[c];
                if (!Pickable(s, chain.Effector))
                    continue;
                Vector3 tp;
                Quaternion tr;
                AsiKeyEvaluator.ResolveState(p, chain, s.pose, s.ctx, st, out tp, out tr);
                Vector3 w = s.ctx.ToWorldPos(tp);
                float size = HandleUtility.GetHandleSize(w) * 0.06f;
                Color col = AsiKeyLabels.KeyColor(st.join ? AsiKeyType.Planted : AsiKeyType.Sliding);
                col.a = Mathf.Lerp(0.3f, 1f, st.blend);
                Handles.color = col;
                Handles.RectangleHandleCap(0, w, Camera.current != null ? Camera.current.transform.rotation : Quaternion.identity, size, EventType.Repaint);
                Vector3 piv = p.PivotOf(chain, st.pivot);
                if (piv.sqrMagnitude > 1e-12f)
                {
                    Vector3 pw = s.ctx.ToWorldPos(tp + tr * piv);
                    Handles.DrawWireDisc(pw, s.ctx.ToWorldRot(tr) * Vector3.up, size * 0.8f);
                    s_seg[0] = w;
                    s_seg[1] = pw;
                    Handles.DrawAAPolyLine(1.5f, s_seg);
                }
            }
        }

        private static void DrawTrajectories(AsiKeySession s, Event e)
        {
            AsiKeyProject p = s.project;
            s_trajBones.Clear();
            for (int c = 0; c < p.chains.Count; c++)
            {
                int eff = p.chains[c].Effector;
                if (eff >= 0 && Pickable(s, eff) && !s_trajBones.Contains(eff))
                    s_trajBones.Add(eff);
            }
            for (int i = 0; i < p.bones.Count; i++)
            {
                if (p.bones[i].isCom && Pickable(s, i) && !s_trajBones.Contains(i))
                    s_trajBones.Add(i);
            }
            List<int> sel = s.SelectedBones();
            for (int k = 0; k < sel.Count; k++)
            {
                if (!s_trajBones.Contains(sel[k]))
                    s_trajBones.Add(sel[k]);
            }
            s.UpdateTrajectories(s_trajBones);
            Handles.zTest = ZTest(p);
            int start = s.TrajectoryStart;
            for (int k = 0; k < s_trajBones.Count; k++)
            {
                int b = s_trajBones[k];
                Vector3[] path = s.Trajectory(b);
                if (path == null || path.Length < 2)
                    continue;
                if (s_traj.Length != path.Length)
                    s_traj = new Vector3[path.Length];
                for (int f = 0; f < path.Length; f++)
                    s_traj[f] = s.ctx.ToWorldPos(path[f]);
                Color col = p.bones[b].isCom ? AsiKeyLabels.ComColor : AsiKeyLabels.SideColor(p.bones[b].side);
                bool selected = s.IsSelected(b);
                if (e.type == EventType.Repaint)
                {
                    Handles.color = new Color(col.r, col.g, col.b, selected ? 0.95f : 0.55f);
                    Handles.DrawAAPolyLine(selected ? 2.5f : 1.5f, s_traj);
                    int cur = Mathf.RoundToInt(s.frame) - start;
                    if (cur >= 0 && cur < path.Length)
                    {
                        Handles.color = Color.white;
                        Handles.DotHandleCap(0, s_traj[cur], Quaternion.identity, HandleUtility.GetHandleSize(s_traj[cur]) * 0.025f, EventType.Repaint);
                    }
                }
                s_one.Clear();
                s_one.Add(b);
                s.TracksFor(s_one, p.activeLayer, s_tracks, false);
                for (int t = 0; t < s_tracks.Count; t++)
                {
                    List<AsiKey> keys = s_tracks[t].keys;
                    for (int ki = 0; ki < keys.Count; ki++)
                    {
                        int fi = Mathf.RoundToInt(keys[ki].frame) - start;
                        if (fi < 0 || fi >= path.Length)
                            continue;
                        Vector3 w = s_traj[fi];
                        float size = HandleUtility.GetHandleSize(w) * 0.035f;
                        Handles.color = AsiKeyLabels.KeyColor(keys[ki].type);
                        if (Handles.Button(w, Quaternion.identity, size, size * 1.5f, Handles.SphereHandleCap))
                        {
                            s.GoTo(keys[ki].frame);
                            e.Use();
                            return;
                        }
                    }
                }
            }
        }

        private static void DrawOverlay(AsiKeySession s, SceneView view)
        {
            if (s_overlay == null)
            {
                s_overlay = new GUIStyle(EditorStyles.boldLabel);
                s_overlay.normal.textColor = new Color(1f, 0.85f, 0.35f);
            }
            Handles.BeginGUI();
            if (s.autoKey && view.camera != null)
            {
                Color red = new Color(0.85f, 0.1f, 0.1f, 0.9f);
                float ppp = EditorGUIUtility.pixelsPerPoint;
                float w = view.camera.pixelWidth / ppp;
                float h = view.camera.pixelHeight / ppp;
                EditorGUI.DrawRect(new Rect(0, 0, w, 3), red);
                EditorGUI.DrawRect(new Rect(0, h - 3, w, 3), red);
                EditorGUI.DrawRect(new Rect(0, 0, 3, h), red);
                EditorGUI.DrawRect(new Rect(w - 3, 0, 3, h), red);
            }
            string layer = s.project.ActiveLayer.name;
            string text = "K帧  第 " + s.frame.ToString("0.##") + " 帧   层：" + layer + (s.autoKey ? "   ● 自动关键点" : "") + (s.PoseDirty && !s.autoKey ? "   （姿势未记录关键帧）" : "");
            int hidden = AsiKeyPanels.HiddenBoneCount(s.project);
            if (hidden > 0)
                text += "   隐藏 " + hidden + " 根骨骼";
            int root = s.HierarchyRoot;
            if (root >= 0)
                text += "   层级选择：" + s.project.bones[root].name;
            if (s.PickingBind)
                text += "   ◎ 点选绑定对象（Esc / 右键取消）";
            else if (s.Picking)
                text += "   ◎ 拾取约束目标（Shift 连续，Esc / 右键退出）";
            else if (Filter != AsiSelectFilter.None)
                text += "   筛选：" + FilterLabel(s.project, Filter);
            GUI.Label(new Rect(10, 6, 900, 20), text, s_overlay);
            float infoY = 24f;
            List<int> only = s.SelectedBones();
            if (only.Count == 1)
            {
                string caption = AsiKeyPanels.SingleBoneCaption(s, only[0]);
                if (!string.IsNullOrEmpty(caption))
                {
                    GUI.Label(new Rect(10, infoY, 900, 18), caption, EditorStyles.whiteMiniLabel);
                    infoY += 16f;
                }
            }
            if (!string.IsNullOrEmpty(s.status))
                GUI.Label(new Rect(10, infoY, 900, 20), s.status, EditorStyles.whiteMiniLabel);
            if (Event.current.type == EventType.Repaint && MarqueeActive)
                DrawMarquee(MarqueeRect());
            Handles.EndGUI();
        }

        private static void DrawMarquee(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.45f, 0.65f, 1f, 0.12f));
            Color edge = new Color(0.55f, 0.75f, 1f, 0.9f);
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, r.width, 1f), edge);
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMax - 1f, r.width, 1f), edge);
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, 1f, r.height), edge);
            EditorGUI.DrawRect(new Rect(r.xMax - 1f, r.yMin, 1f, r.height), edge);
        }

        // ---------------- 仅选骨骼 ----------------

        private static bool MarqueeActive
        {
            get { return s_marquee && GUIUtility.hotControl != 0 && GUIUtility.hotControl == s_selectId; }
        }

        private static Rect MarqueeRect()
        {
            return Rect.MinMaxRect(Mathf.Min(s_pressPos.x, s_dragPos.x), Mathf.Min(s_pressPos.y, s_dragPos.y),
                Mathf.Max(s_pressPos.x, s_dragPos.x), Mathf.Max(s_pressPos.y, s_dragPos.y));
        }

        private static void HandleSelection(AsiKeySession s, SceneView view, Event e)
        {
            int id = GUIUtility.GetControlID(SelectHint, FocusType.Passive);
            s_selectId = id;
            Camera cam = view.camera;
            switch (e.GetTypeForControl(id))
            {
                case EventType.Layout:
                case EventType.MouseMove:
                {
                    if (GUIUtility.hotControl != 0)
                        break;
                    bool blocked = Tools.viewToolActive;
                    int hover = blocked ? -1 : PickIndex(s, cam, e.mousePosition);
                    if (hover != s_hover)
                    {
                        s_hover = hover;
                        view.Repaint();
                    }
                    if (e.type == EventType.Layout && !blocked)
                        HandleUtility.AddControl(id, hover >= 0 ? HoverDistance : EmptyDistance);
                    break;
                }
                case EventType.MouseDown:
                    if (e.button != 0 || e.alt || GUIUtility.hotControl != 0 || HandleUtility.nearestControl != id)
                        break;
                    GUIUtility.hotControl = id;
                    s_pressPos = e.mousePosition;
                    s_dragPos = s_pressPos;
                    s_marquee = false;
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id)
                        break;
                    s_dragPos = e.mousePosition;
                    if (!s_marquee && (s_dragPos - s_pressPos).sqrMagnitude > DragThreshold * DragThreshold)
                        s_marquee = true;
                    e.Use();
                    view.Repaint();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id || e.button != 0)
                        break;
                    GUIUtility.hotControl = 0;
                    if (s_marquee)
                        RectSelect(s, cam, MarqueeRect(), e.shift, EditorGUI.actionKey);
                    else
                        ClickSelect(s, PickIndex(s, cam, s_pressPos), e.shift || EditorGUI.actionKey);
                    s_marquee = false;
                    e.Use();
                    view.Repaint();
                    break;
                case EventType.MouseLeaveWindow:
                    if (s_hover >= 0)
                    {
                        s_hover = -1;
                        view.Repaint();
                    }
                    break;
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && GUIUtility.hotControl == id)
            {
                GUIUtility.hotControl = 0;
                s_marquee = false;
                e.Use();
                view.Repaint();
            }
        }

        // ---------------- 约束目标拾取 ----------------

        /// <summary>拾取模式：左键点控制器或场景物体作为约束目标（Shift 连续拾取），右键 / Esc 退出。不受选择筛选限制。</summary>
        private static void HandlePick(AsiKeySession s, SceneView view, Event e)
        {
            int id = GUIUtility.GetControlID(PickHint, FocusType.Passive);
            Camera cam = view.camera;
            switch (e.GetTypeForControl(id))
            {
                case EventType.Layout:
                    if (!Tools.viewToolActive)
                        HandleUtility.AddDefaultControl(id);
                    break;
                case EventType.MouseMove:
                {
                    int hover = PickAny(s, cam, e.mousePosition);
                    if (hover != s_hover)
                    {
                        s_hover = hover;
                        view.Repaint();
                    }
                    break;
                }
                case EventType.MouseDown:
                {
                    if (e.alt)
                        break;
                    if (e.button == 1)
                    {
                        s.EndPick();
                        e.Use();
                        break;
                    }
                    if (e.button != 0 || HandleUtility.nearestControl != id)
                        break;
                    int bone = PickAny(s, cam, e.mousePosition);
                    Transform t = bone >= 0 ? s.controls[bone] : null;
                    if (t == null)
                    {
                        GameObject go = HandleUtility.PickGameObject(e.mousePosition, false);
                        t = go != null ? go.transform : null;
                    }
                    e.Use();
                    if (t == null)
                        break;
                    if (s.PickingBind)
                    {
                        string bindErr = s.AssignBindTarget(t);
                        if (!string.IsNullOrEmpty(bindErr))
                            s.status = bindErr;
                        else
                            s.EndPick();
                        AsiKeySession.RaiseRepaint();
                        break;
                    }
                    int k = s.pickConstraint;
                    string err = s.pickRole == AsiKeySession.PickRole.UpObject ? s.SetUpObject(k, t) : s.AddConstraintTarget(k, t);
                    if (!string.IsNullOrEmpty(err))
                        s.status = err;
                    else if (!e.shift)
                        s.EndPick();
                    AsiKeySession.RaiseRepaint();
                    break;
                }
                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.Escape)
                    {
                        s.EndPick();
                        e.Use();
                    }
                    break;
            }
        }

        /// <summary>拾取用：任何可见的控制器（不看选择筛选），被约束的骨骼自身除外。</summary>
        private static int PickAny(AsiKeySession s, Camera cam, Vector2 mouse)
        {
            float best = float.MaxValue;
            int pick = -1;
            int self = -1;
            if (s.Picking)
                self = s.project.constraints[s.pickConstraint].bone;
            int skipChain = -1;
            if (s.PickingBind && s.pickBindTrack != null && s.pickBindTrack.kind == AsiTrackKind.Chain)
                skipChain = s.pickBindTrack.target;
            for (int i = 0; i < s.project.bones.Count; i++)
            {
                if (i == self || !Pickable(s, i))
                    continue;
                if (skipChain >= 0 && s.project.ChainOf(i) == skipChain)
                    continue;
                float score = HitScore(s, i, cam, mouse, false);
                if (score < best)
                {
                    best = score;
                    pick = i;
                }
            }
            return pick;
        }

        /// <summary>控制器存在、外形可见、所属单位可显。</summary>
        public static bool Pickable(AsiKeySession s, int i)
        {
            if (i < 0 || i >= s.controls.Length || s.controls[i] == null)
                return false;
            AsiKeyProject p = s.project;
            AsiKeyBone b = p.bones[i];
            if (p.ValidUnit(b.unit) && !p.units[b.unit].visible)
                return false;
            AsiKeyShape sh = b.shape;
            return sh.visible && sh.kind != AsiShapeKind.Hidden;
        }

        private static int PickIndex(AsiKeySession s, Camera cam, Vector2 mouse)
        {
            float best = float.MaxValue;
            int pick = -1;
            for (int i = 0; i < s.project.bones.Count; i++)
            {
                if (!Selectable(s, i))
                    continue;
                float score = HitScore(s, i, cam, mouse, false);
                if (score < best)
                {
                    best = score;
                    pick = i;
                }
            }
            return pick;
        }

        /// <summary>
        /// 自由旋转区里没拖动的单击。和普通点选同一套规则：筛开着只点控制器，点空清空（Shift/Ctrl 除外）；
        /// 筛关掉时先点控制器，没点中再点场景里的物体。
        /// </summary>
        public static void ClickThrough(AsiKeySession s, Camera cam, Vector2 mouse, bool toggle)
        {
            if (s == null || cam == null)
                return;
            int i = PickIndex(s, cam, mouse);
            if (i >= 0 || Filter != AsiSelectFilter.None)
            {
                ClickSelect(s, i, toggle);
                return;
            }
            GameObject go = HandleUtility.PickGameObject(mouse, false);
            if (go == null)
            {
                if (!toggle)
                    Selection.objects = new UnityEngine.Object[0];
                return;
            }
            if (!toggle)
            {
                Selection.activeGameObject = go;
                return;
            }
            List<UnityEngine.Object> objs = new List<UnityEngine.Object>(Selection.objects);
            if (!objs.Remove(go))
                objs.Add(go);
            Selection.objects = objs.ToArray();
        }

        private static void ClickSelect(AsiKeySession s, int i, bool toggle)
        {
            if (i < 0)
            {
                if (!toggle)
                    Selection.objects = new UnityEngine.Object[0];
                return;
            }
            GameObject go = s.controls[i].gameObject;
            if (!toggle)
            {
                Selection.activeGameObject = go;
                return;
            }
            List<UnityEngine.Object> objs = new List<UnityEngine.Object>(Selection.objects);
            if (!objs.Remove(go))
                objs.Add(go);
            Selection.objects = objs.ToArray();
        }

        private static void RectSelect(AsiKeySession s, Camera cam, Rect r, bool add, bool sub)
        {
            List<UnityEngine.Object> objs = new List<UnityEngine.Object>();
            if (add || sub)
                objs.AddRange(Selection.objects);
            for (int i = 0; i < s.project.bones.Count; i++)
            {
                if (!Selectable(s, i) || !InRect(s, i, cam, r))
                    continue;
                GameObject go = s.controls[i].gameObject;
                if (sub)
                    objs.Remove(go);
                else if (!objs.Contains(go))
                    objs.Add(go);
            }
            Selection.objects = objs.ToArray();
        }

        /// <summary>控制器原点或外形轮廓任一点落在框内即算框中。</summary>
        private static bool InRect(AsiKeySession s, int i, Camera cam, Rect r)
        {
            Vector2 g;
            if (ToGUI(cam, s.controls[i].position, out g) && r.Contains(g))
                return true;
            Vector3 center;
            int n = ProjectShape(s, i, cam, false, out center);
            for (int k = 0; k < n; k++)
            {
                if (r.Contains(s_gui[k]))
                    return true;
            }
            return false;
        }

        // ---------------- 拾取 ----------------

        private static bool ToGUI(Camera cam, Vector3 world, out Vector2 gp)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f)
            {
                gp = Vector2.zero;
                return false;
            }
            if (Camera.current == cam)
            {
                gp = HandleUtility.WorldToGUIPoint(world);
                return true;
            }
            float ppp = EditorGUIUtility.pixelsPerPoint;
            gp = new Vector2(sp.x / ppp, (cam.pixelHeight - sp.y) / ppp);
            return true;
        }

        /// <summary>pixels 为 true 时投影到相机像素坐标（y 向上，与自定义拾取回调的 position 一致），否则投影到 GUI 坐标。</summary>
        private static bool Project(Camera cam, Vector3 world, bool pixels, out Vector2 p)
        {
            if (!pixels)
                return ToGUI(cam, world, out p);
            Vector3 sp = cam.WorldToScreenPoint(world);
            p = sp;
            return sp.z > 0f;
        }

        /// <summary>
        /// 外形轮廓投影写入 s_gui：方块/网格写 8 个角点，圆环/方框写折线点，八面体写 6 个顶点，棍状写关节与尖端。有点在相机后方时返回 0。
        /// </summary>
        private static int ProjectShape(AsiKeySession s, int i, Camera cam, bool pixels, out Vector3 center)
        {
            AsiKeyProject p = s.project;
            AsiKeyBone b = p.bones[i];
            AsiKeyShape sh = b.shape;
            Matrix4x4 m = AsiKeyShapes.ShapeMatrix(b, s.controls[i], p.shapeScale);
            center = Vector3.zero;
            int n;
            Look look = LookOf(p, b);
            switch (look)
            {
                case Look.Box:
                case Look.Mesh:
                    if (look == Look.Mesh)
                        AsiKeyShapes.MeshBoxCorners(sh, s_local);
                    else
                        AsiKeyShapes.BoxCorners(sh, s_local);
                    n = 8;
                    for (int k = 0; k < n; k++)
                        s_ring[k] = m.MultiplyPoint3x4(s_local[k]);
                    break;
                case Look.Ring:
                    AsiKeyShapes.RingPoints(sh, s_ring);
                    n = s_ring.Length;
                    for (int k = 0; k < n; k++)
                        s_ring[k] = m.MultiplyPoint3x4(s_ring[k]);
                    break;
                case Look.Rect:
                    AsiKeyShapes.RectPoints(sh, s_rect);
                    n = s_rect.Length;
                    for (int k = 0; k < n; k++)
                        s_ring[k] = m.MultiplyPoint3x4(s_rect[k]);
                    break;
                case Look.Octa:
                    m = AsiKeyShapes.BoneMatrix(b, s.controls[i]);
                    AsiKeyShapes.OctaCorners(b, p.shapeScale, s_local);
                    n = 6;
                    for (int k = 0; k < n; k++)
                        s_ring[k] = m.MultiplyPoint3x4(s_local[k]);
                    break;
                case Look.Stick:
                    m = AsiKeyShapes.BoneMatrix(b, s.controls[i]);
                    n = 2;
                    s_ring[0] = s.controls[i].position;
                    s_ring[1] = m.MultiplyPoint3x4(AsiKeyShapes.BoneTip(b));
                    break;
                default:
                    return 0;
            }
            for (int k = 0; k < n; k++)
            {
                center += s_ring[k];
                if (!Project(cam, s_ring[k], pixels, out s_gui[k]))
                    return 0;
            }
            center /= n;
            return n;
        }

        private static bool Contains(GameObject[] list, GameObject go)
        {
            if (list == null)
                return false;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] == go)
                    return true;
            }
            return false;
        }

        public static GameObject Pick(Camera cam, int layers, Vector2 position, GameObject[] ignore, GameObject[] filter, out int materialIndex)
        {
            materialIndex = -1;
            AsiKeySession s = AsiKeySession.Current;
            if (s == null || !s.Valid || cam == null)
                return null;
            AsiKeyProject p = s.project;
            float best = float.MaxValue;
            GameObject pick = null;
            for (int i = 0; i < p.bones.Count; i++)
            {
                if (!Selectable(s, i))
                    continue;
                GameObject go = s.controls[i].gameObject;
                if (Contains(ignore, go) || filter != null && !Contains(filter, go))
                    continue;
                float score = HitScore(s, i, cam, position, true);
                if (score < best)
                {
                    best = score;
                    pick = go;
                }
            }
            return pick;
        }

        /// <summary>命中返回到相机的距离（越小越优先），未命中返回 MaxValue。线框类外形按线距离判定。</summary>
        private static float HitScore(AsiKeySession s, int i, Camera cam, Vector2 mouse, bool pixels)
        {
            Look look = LookOf(s.project, s.project.bones[i]);
            Vector3 center;
            int n = ProjectShape(s, i, cam, pixels, out center);
            if (n == 0)
                return float.MaxValue;
            float tol = pixels ? LineTol * EditorGUIUtility.pixelsPerPoint : LineTol;
            Vector3 eye = cam.transform.position;
            Vector2[] g = s_gui;
            if (look == Look.Octa)
            {
                float d = (eye - center).magnitude;
                for (int f = 0; f < AsiKeyShapes.OctaFaces.Length; f++)
                {
                    int[] face = AsiKeyShapes.OctaFaces[f];
                    for (int k = 0; k < 3; k++)
                        s_face[k] = g[face[k]];
                    if (AsiKeyShapes.InsideConvex(mouse, s_face, 3))
                        return d;
                }
                for (int e = 0; e < AsiKeyShapes.OctaEdges.Length; e += 2)
                {
                    if (AsiKeyShapes.DistanceToSegment(mouse, g[AsiKeyShapes.OctaEdges[e]], g[AsiKeyShapes.OctaEdges[e + 1]]) < tol)
                        return d;
                }
                return float.MaxValue;
            }
            if (look == Look.Stick)
            {
                if ((mouse - g[0]).magnitude < tol * 1.5f || AsiKeyShapes.DistanceToSegment(mouse, g[0], g[1]) < tol)
                    return (eye - s.controls[i].position).magnitude * 0.98f;
                return float.MaxValue;
            }
            if (look == Look.Box || look == Look.Mesh)
            {
                float d = (eye - center).magnitude;
                for (int f = 0; f < AsiKeyShapes.BoxFaces.Length; f++)
                {
                    int[] face = AsiKeyShapes.BoxFaces[f];
                    for (int k = 0; k < 4; k++)
                        s_face[k] = g[face[k]];
                    if (AsiKeyShapes.InsideConvex(mouse, s_face, 4))
                        return d;
                }
                for (int e = 0; e < AsiKeyShapes.BoxEdges.Length; e += 2)
                {
                    if (AsiKeyShapes.DistanceToSegment(mouse, g[AsiKeyShapes.BoxEdges[e]], g[AsiKeyShapes.BoxEdges[e + 1]]) < tol)
                        return d;
                }
                return float.MaxValue;
            }
            for (int k = 0; k + 1 < n; k++)
            {
                if (AsiKeyShapes.DistanceToSegment(mouse, g[k], g[k + 1]) < tol)
                    return (eye - s.controls[i].position).magnitude * 0.98f;
            }
            return float.MaxValue;
        }
    }
}
