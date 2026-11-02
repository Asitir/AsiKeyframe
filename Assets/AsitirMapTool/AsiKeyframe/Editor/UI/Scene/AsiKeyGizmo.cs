using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AsitirTool.Editor
{
    /// <summary>K帧变换轴的参考坐标，跟随 Unity 的 Tools.pivotRotation（X 键切换）。局部 = Biped 轴向（X 沿骨骼）。</summary>
    public enum AsiGizmoSpace
    {
        Local = 0,
        World = 1
    }

    /// <summary>
    /// K帧变换轴（仿 3ds Max）：活动选择是控制器且工具为移动/旋转时代替 Unity 的变换手柄（Tools.hidden）。
    /// 旋转：X/Y/Z 环只画朝向相机的半圈，沿抓取点切线拖动按 0.5°/像素转；环内拖动是轨迹球自由旋转，单击穿透去选骨骼；最外圈是屏幕环，绕视线转；
    /// 拖动时显示扇形和角度，Ctrl 按 Unity 吸附增量取整。移动：轴、平面方块、中心方块（视图平面）。
    /// 编辑交给 AsiKeySession.Gizmo*，每次都按「按下时的姿势 + 总增量」计算；Esc 或右键取消。
    /// </summary>
    public static class AsiKeyGizmo
    {
        private const string EnabledKey = "AsiKey.Gizmo.Enabled";
        public const float DegreesPerPixel = 0.5f;
        private const float PickTolerance = 7f;
        private const float CenterTolerance = 8f;
        private const float ScreenRingScale = 1.2f;
        // 球内距离为 0，按下先被自由旋转接住，避免框选或点选在分出点击/拖动之前改掉选择。
        private const float TrackballDistance = 0f;
        // 与点选框选的拖动阈值相同：相对按下点的位移，不是按住时长。
        private const float FreeDragThreshold = 6f;
        private const int Segments = 64;

        private enum Part
        {
            None,
            RotX,
            RotY,
            RotZ,
            RotFree,
            RotScreen,
            MoveX,
            MoveY,
            MoveZ,
            PlaneX,
            PlaneY,
            PlaneZ,
            MoveView
        }

        private struct Geo
        {
            public bool rotate;
            public Vector3 center;
            public Vector3 rigCenter;
            public Quaternion frame;
            public float size;
            public Vector3 view;
            public Vector3 camUp;
            public Vector3 camRight;
            public int mask;
            public bool canMove;
        }

        private static readonly int Hint = "AsiKeyGizmo".GetHashCode();
        private static readonly Vector3[] s_arc = new Vector3[Segments / 2 + 1];
        private static readonly Vector3[] s_circle = new Vector3[Segments + 1];
        private static readonly Vector3[] s_seg = new Vector3[2];
        private static readonly Vector3[] s_quad = new Vector3[4];
        private static readonly Vector2[] s_quadGui = new Vector2[4];
        private static int s_enabled = -1;
        private static bool s_hiding;
        private static bool s_on;
        private static bool s_rotate;
        private static Tool s_lastTool = Tool.Move;
        private static int s_id;
        private static Part s_hover;
        private static Part s_drag;
        private static AsiKeySession s_dragSession;
        private static bool s_freePress;
        private static Vector2 s_freePressMouse;
        private static GUIStyle s_label;

        private static Vector2 s_downMouse;
        private static Quaternion s_frame0;
        private static Vector3 s_center0;
        private static Vector3 s_rigCenter0;
        private static Vector3 s_view0;
        private static Vector3 s_camUp0;
        private static Vector3 s_camRight0;
        private static Vector3 s_axis0;
        private static Vector3 s_grabDir;
        private static Vector2 s_tangent;
        private static Vector3 s_planeNormal;
        private static Vector3 s_hit0;
        private static Vector3 s_hitLast;
        private static bool s_local;
        private static int s_hierRoot = -1;
        private static float s_lastAtan;
        private static float s_accum;
        private static float s_angle;
        private static Vector3 s_move;

        // ---------------- 设置 ----------------

        /// <summary>K帧变换轴开关。关闭后用 Unity 自己的变换手柄（仍按 Biped 规则换算）。</summary>
        public static bool Enabled
        {
            get
            {
                if (s_enabled < 0)
                    s_enabled = EditorPrefs.GetBool(EnabledKey, true) ? 1 : 0;
                return s_enabled == 1;
            }
            set
            {
                if (value == Enabled)
                    return;
                s_enabled = value ? 1 : 0;
                EditorPrefs.SetBool(EnabledKey, value);
                if (!value)
                    Release();
                SceneView.RepaintAll();
                AsiKeySession.RaiseRepaint();
            }
        }

        /// <summary>移动和旋转共用 Unity 的参考坐标：Local = 局部，Global = 世界。</summary>
        public static AsiGizmoSpace Space
        {
            get { return Tools.pivotRotation == PivotRotation.Local ? AsiGizmoSpace.Local : AsiGizmoSpace.World; }
        }

        [InitializeOnLoadMethod]
        private static void HookPivotRotation()
        {
            Tools.pivotRotationChanged += SceneView.RepaintAll;
        }

        /// <summary>当前变换工具；右键/Alt 漫游时 Tools.current 是 View，这时取漫游前的工具。</summary>
        public static Tool CurrentTool
        {
            get { return Tools.viewToolActive ? s_lastTool : Tools.current; }
        }

        /// <summary>正在拖动变换轴。</summary>
        public static bool Dragging
        {
            get { return s_drag != Part.None; }
        }

        /// <summary>正在拖动，或按在自由旋转区里、还没分出单击和拖动。</summary>
        public static bool Holding
        {
            get { return s_freePress || s_drag != Part.None; }
        }

        /// <summary>变换轴当前代替了 Unity 的变换手柄。</summary>
        public static bool Shown
        {
            get { return s_on; }
        }

        /// <summary>交还 Unity 的变换手柄（会话结束、关闭开关、选择不是控制器时）。</summary>
        public static void Release()
        {
            if (s_drag != Part.None && s_dragSession != null)
                s_dragSession.GizmoCancel();
            if (GUIUtility.hotControl == s_id)
                GUIUtility.hotControl = 0;
            EndDrag();
            s_freePress = false;
            s_on = false;
            s_hover = Part.None;
            if (s_hiding)
            {
                s_hiding = false;
                Tools.hidden = false;
            }
        }

        // ---------------- 输入 ----------------

        public static void Input(AsiKeySession s, SceneView view, Event e)
        {
            int id = GUIUtility.GetControlID(Hint, FocusType.Passive);
            s_id = id;
            UpdateActive(s);
            if (!s_on)
                return;
            Camera cam = view.camera;
            if ((s_drag != Part.None || s_freePress) && (s_dragSession != s || GUIUtility.hotControl != id))
            {
                if (s_drag != Part.None && s_dragSession == s)
                    s_dragSession.GizmoEnd();
                if (GUIUtility.hotControl == id)
                    GUIUtility.hotControl = 0;
                EndDrag();
                s_freePress = false;
            }
            Geo g;
            switch (e.GetTypeForControl(id))
            {
                case EventType.Layout:
                {
                    if (GUIUtility.hotControl != 0 || Tools.viewToolActive || !Compute(s, cam, s_rotate, out g))
                        break;
                    float dist;
                    s_hover = Pick(g, e.mousePosition, out dist);
                    if (s_hover != Part.None)
                        HandleUtility.AddControl(id, dist);
                    break;
                }
                case EventType.MouseMove:
                {
                    if (GUIUtility.hotControl != 0 || !Compute(s, cam, s_rotate, out g))
                        break;
                    float dist;
                    Part hover = Pick(g, e.mousePosition, out dist);
                    if (hover != s_hover)
                    {
                        s_hover = hover;
                        view.Repaint();
                    }
                    break;
                }
                case EventType.MouseDown:
                    if (GUIUtility.hotControl == id && e.button != 0)
                    {
                        Cancel(s);
                        e.Use();
                        break;
                    }
                    if (e.button != 0 || e.alt || GUIUtility.hotControl != 0 || HandleUtility.nearestControl != id || s_hover == Part.None)
                        break;
                    if (!Compute(s, cam, s_rotate, out g))
                        break;
                    if (s_hover == Part.RotFree)
                    {
                        s_freePress = true;
                        s_freePressMouse = e.mousePosition;
                        s_dragSession = s;
                        GUIUtility.hotControl = id;
                        e.Use();
                        view.Repaint();
                        break;
                    }
                    if (!BeginDrag(s, g, s_hover, e.mousePosition))
                        break;
                    GUIUtility.hotControl = id;
                    e.Use();
                    view.Repaint();
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id)
                        break;
                    if (s_freePress)
                    {
                        if ((e.mousePosition - s_freePressMouse).sqrMagnitude <= FreeDragThreshold * FreeDragThreshold)
                        {
                            e.Use();
                            break;
                        }
                        if (!Compute(s, cam, s_rotate, out g) || !BeginDrag(s, g, Part.RotFree, s_freePressMouse))
                        {
                            GUIUtility.hotControl = 0;
                            s_freePress = false;
                            EndDrag();
                            e.Use();
                            break;
                        }
                        s_freePress = false;
                        Drag(s, e.mousePosition);
                        e.Use();
                        view.Repaint();
                        break;
                    }
                    if (s_drag == Part.None)
                        break;
                    if (!s.GizmoActive)
                    {
                        GUIUtility.hotControl = 0;
                        EndDrag();
                        e.Use();
                        break;
                    }
                    Drag(s, e.mousePosition);
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id || e.button != 0)
                        break;
                    if (s_freePress)
                    {
                        Vector2 press = s_freePressMouse;
                        bool toggle = e.shift || EditorGUI.actionKey;
                        GUIUtility.hotControl = 0;
                        s_freePress = false;
                        EndDrag();
                        AsiKeyRigDrawer.ClickThrough(s, cam, press, toggle);
                        e.Use();
                        view.Repaint();
                        break;
                    }
                    if (s_drag == Part.None)
                        break;
                    GUIUtility.hotControl = 0;
                    s.GizmoEnd();
                    EndDrag();
                    e.Use();
                    view.Repaint();
                    break;
                case EventType.KeyDown:
                    if (GUIUtility.hotControl == id && e.keyCode == KeyCode.Escape)
                    {
                        Cancel(s);
                        e.Use();
                    }
                    break;
            }
        }

        private static void UpdateActive(AsiKeySession s)
        {
            if (!Tools.viewToolActive)
                s_lastTool = Tools.current;
            Tool tool = s_lastTool;
            bool on = Enabled && !s.playing && ActiveBone(s) >= 0
                && (tool == Tool.Move || tool == Tool.Rotate);
            if (!on)
            {
                Release();
                return;
            }
            bool rotate = tool == Tool.Rotate;
            if (rotate != s_rotate && (s_drag != Part.None || s_freePress))
            {
                if (s_drag != Part.None)
                    s.GizmoCancel();
                if (GUIUtility.hotControl == s_id)
                    GUIUtility.hotControl = 0;
                EndDrag();
                s_freePress = false;
            }
            s_rotate = rotate;
            s_on = true;
            if (!Tools.hidden)
            {
                Tools.hidden = true;
                s_hiding = true;
            }
        }

        private static void Cancel(AsiKeySession s)
        {
            if (s_drag != Part.None)
                s.GizmoCancel();
            if (GUIUtility.hotControl == s_id)
                GUIUtility.hotControl = 0;
            EndDrag();
            s_freePress = false;
            SceneView.RepaintAll();
        }

        private static void EndDrag()
        {
            s_drag = Part.None;
            s_dragSession = null;
        }

        // ---------------- 几何 ----------------

        /// <summary>变换轴跟随的骨骼：层级选择时是层级根，否则是 Unity 的活动选择。</summary>
        private static int ActiveBone(AsiKeySession s)
        {
            int root = s.HierarchyRoot;
            return root >= 0 ? root : s.ControlIndex(Selection.activeTransform);
        }

        private static bool Compute(AsiKeySession s, Camera cam, bool rotate, out Geo g)
        {
            g = default(Geo);
            int active = ActiveBone(s);
            if (active < 0 || cam == null)
                return false;
            List<int> sel = s.SelectedBones();
            if (sel.Count == 0)
                return false;
            bool hier = s.HierarchyRoot >= 0;
            if (hier && !rotate)
                sel = new List<int> { active };
            g.rotate = rotate;
            Vector3 rc = s.GizmoPivot(active);
            if (Tools.pivotMode == PivotMode.Center && sel.Count > 1 && !hier)
            {
                rc = Vector3.zero;
                for (int k = 0; k < sel.Count; k++)
                    rc += s.GizmoPivot(sel[k]);
                rc /= sel.Count;
            }
            g.rigCenter = rc;
            g.center = s.ctx.ToWorldPos(rc);
            g.frame = Frame(s, active, Space);
            g.size = HandleUtility.GetHandleSize(g.center);
            Transform ct = cam.transform;
            g.view = cam.orthographic ? ct.forward : g.center - ct.position;
            if (g.view.sqrMagnitude < 1e-10f)
                g.view = ct.forward;
            g.view.Normalize();
            g.camUp = Vector3.ProjectOnPlane(ct.up, g.view);
            if (g.camUp.sqrMagnitude < 1e-8f)
                g.camUp = ct.up;
            g.camUp.Normalize();
            g.camRight = Vector3.Cross(g.camUp, g.view).normalized;
            g.mask = rotate && Space == AsiGizmoSpace.Local ? AsiKeyBipedFrame.AxisMask(s.project, active) : AsiKeyBipedFrame.AllAxes;
            for (int k = 0; k < sel.Count && !g.canMove; k++)
                g.canMove = s.GizmoCanMove(sel[k]);
            return true;
        }

        private static Quaternion Frame(AsiKeySession s, int i, AsiGizmoSpace space)
        {
            if (space == AsiGizmoSpace.Local)
                return s.ctx.ToWorldRot(s.pose.wrot[i] * AsiKeyBipedFrame.Local(s.project, i));
            return Quaternion.identity;
        }

        private static Vector3 Axis(Geo g, int k)
        {
            return (g.frame * AsiKeyBipedFrame.Unit(k)).normalized;
        }

        /// <summary>绕 axis 的环朝向相机的半圈；环正对相机时画整圈。</summary>
        private static Vector3[] FrontArc(Geo g, Vector3 axis, float radius)
        {
            Vector3 from = Vector3.Cross(axis, g.view);
            if (from.sqrMagnitude < 1e-4f)
            {
                Circle(g.center, axis, g.camRight, radius, s_circle);
                return s_circle;
            }
            from.Normalize();
            int n = s_arc.Length - 1;
            for (int k = 0; k <= n; k++)
                s_arc[k] = g.center + Quaternion.AngleAxis(180f * k / n, axis) * from * radius;
            return s_arc;
        }

        private static void Circle(Vector3 center, Vector3 normal, Vector3 from, float radius, Vector3[] pts)
        {
            from = Vector3.ProjectOnPlane(from, normal);
            if (from.sqrMagnitude < 1e-8f)
                from = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right);
            from.Normalize();
            int n = pts.Length - 1;
            for (int k = 0; k <= n; k++)
                pts[k] = center + Quaternion.AngleAxis(360f * k / n, normal) * from * radius;
        }

        private static float GuiRadius(Geo g, float radius)
        {
            return (HandleUtility.WorldToGUIPoint(g.center + g.camRight * radius) - HandleUtility.WorldToGUIPoint(g.center)).magnitude;
        }

        private static Vector3 Unit(Part p)
        {
            return AsiKeyBipedFrame.Unit(AxisIndex(p));
        }

        private static int AxisIndex(Part p)
        {
            switch (p)
            {
                case Part.RotX:
                case Part.MoveX:
                case Part.PlaneX:
                    return 0;
                case Part.RotY:
                case Part.MoveY:
                case Part.PlaneY:
                    return 1;
                default:
                    return 2;
            }
        }

        /// <summary>平面方块朝相机一侧摆放（与 Unity 一致）。</summary>
        private static void PlaneQuad(Geo g, int normal, Vector3[] quad)
        {
            int i = (normal + 1) % 3;
            int j = (normal + 2) % 3;
            Vector3 a = Axis(g, i);
            Vector3 b = Axis(g, j);
            if (Vector3.Dot(a, g.view) > 0f)
                a = -a;
            if (Vector3.Dot(b, g.view) > 0f)
                b = -b;
            float l = g.size;
            quad[0] = g.center + (a * 0.15f + b * 0.15f) * l;
            quad[1] = g.center + (a * 0.45f + b * 0.15f) * l;
            quad[2] = g.center + (a * 0.45f + b * 0.45f) * l;
            quad[3] = g.center + (a * 0.15f + b * 0.45f) * l;
        }

        private static bool AxisFacesCamera(Geo g, Vector3 axis)
        {
            return Mathf.Abs(Vector3.Dot(axis, g.view)) > 0.985f;
        }

        // ---------------- 拾取 ----------------

        private static Part Pick(Geo g, Vector2 mouse, out float dist)
        {
            dist = 0f;
            return g.rotate ? PickRotate(g, mouse, out dist) : PickMove(g, mouse);
        }

        private static Part PickRotate(Geo g, Vector2 mouse, out float dist)
        {
            dist = 0f;
            Part best = Part.None;
            float bestDist = PickTolerance;
            for (int k = 0; k < 3; k++)
            {
                if ((g.mask & (1 << k)) == 0)
                    continue;
                float d = HandleUtility.DistanceToPolyLine(FrontArc(g, Axis(g, k), g.size));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = Part.RotX + k;
                }
            }
            if (best != Part.None)
                return best;
            Circle(g.center, g.view, g.camRight, g.size * ScreenRingScale, s_circle);
            if (HandleUtility.DistanceToPolyLine(s_circle) < PickTolerance)
                return Part.RotScreen;
            Vector2 c = HandleUtility.WorldToGUIPoint(g.center);
            if ((mouse - c).magnitude < GuiRadius(g, g.size))
            {
                dist = TrackballDistance;
                return Part.RotFree;
            }
            return Part.None;
        }

        private static Part PickMove(Geo g, Vector2 mouse)
        {
            if (!g.canMove)
                return Part.None;
            Vector2 c = HandleUtility.WorldToGUIPoint(g.center);
            if ((mouse - c).magnitude < CenterTolerance)
                return Part.MoveView;
            Part best = Part.None;
            float bestDist = PickTolerance;
            for (int k = 0; k < 3; k++)
            {
                Vector3 a = Axis(g, k);
                if (AxisFacesCamera(g, a))
                    continue;
                float d = HandleUtility.DistanceToLine(g.center, g.center + a * g.size);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = Part.MoveX + k;
                }
            }
            if (best != Part.None)
                return best;
            for (int k = 0; k < 3; k++)
            {
                PlaneQuad(g, k, s_quad);
                for (int q = 0; q < 4; q++)
                    s_quadGui[q] = HandleUtility.WorldToGUIPoint(s_quad[q]);
                if (AsiKeyShapes.InsideConvex(mouse, s_quadGui, 4) || AsiKeyShapes.InsideConvex(mouse, Reverse(s_quadGui), 4))
                    return Part.PlaneX + k;
            }
            return Part.None;
        }

        private static readonly Vector2[] s_rev = new Vector2[4];

        private static Vector2[] Reverse(Vector2[] quad)
        {
            for (int k = 0; k < 4; k++)
                s_rev[k] = quad[3 - k];
            return s_rev;
        }

        // ---------------- 拖动 ----------------

        private static bool BeginDrag(AsiKeySession s, Geo g, Part part, Vector2 mouse)
        {
            s_hierRoot = s.HierarchyRoot;
            List<int> bones = s_hierRoot >= 0 && !g.rotate ? new List<int> { s_hierRoot } : s.SelectedBones();
            if (!s.GizmoBegin(bones))
                return false;
            s_drag = part;
            s_dragSession = s;
            s_downMouse = mouse;
            s_frame0 = g.frame;
            s_center0 = g.center;
            s_rigCenter0 = g.rigCenter;
            s_view0 = g.view;
            s_camUp0 = g.camUp;
            s_camRight0 = g.camRight;
            s_local = Space == AsiGizmoSpace.Local;
            s_angle = 0f;
            s_accum = 0f;
            s_move = Vector3.zero;
            Vector2 c = HandleUtility.WorldToGUIPoint(g.center);
            switch (part)
            {
                case Part.RotX:
                case Part.RotY:
                case Part.RotZ:
                {
                    s_axis0 = Axis(g, AxisIndex(part));
                    Vector3[] pts = FrontArc(g, s_axis0, g.size);
                    Vector3 grab = pts[0];
                    float best = float.MaxValue;
                    for (int k = 0; k < pts.Length; k++)
                    {
                        float d = (HandleUtility.WorldToGUIPoint(pts[k]) - mouse).sqrMagnitude;
                        if (d < best)
                        {
                            best = d;
                            grab = pts[k];
                        }
                    }
                    s_grabDir = (grab - g.center).normalized;
                    Vector3 tangent = Vector3.Cross(s_axis0, s_grabDir);
                    Vector2 gp = HandleUtility.WorldToGUIPoint(grab);
                    s_tangent = HandleUtility.WorldToGUIPoint(grab + tangent * g.size * 0.2f) - gp;
                    if (s_tangent.sqrMagnitude < 1e-6f)
                    {
                        Vector2 r = gp - c;
                        s_tangent = new Vector2(-r.y, r.x);
                    }
                    s_tangent = s_tangent.sqrMagnitude > 1e-10f ? s_tangent.normalized : Vector2.right;
                    break;
                }
                case Part.RotScreen:
                {
                    Vector2 r = mouse - c;
                    s_lastAtan = Mathf.Atan2(r.y, r.x) * Mathf.Rad2Deg;
                    Ray ray = HandleUtility.GUIPointToWorldRay(mouse);
                    Vector3 hit;
                    s_grabDir = RayPlane(ray, g.view, g.center, out hit) ? Vector3.ProjectOnPlane(hit - g.center, g.view).normalized : g.camRight;
                    break;
                }
                case Part.MoveX:
                case Part.MoveY:
                case Part.MoveZ:
                {
                    s_axis0 = Axis(g, AxisIndex(part));
                    Vector3 n = Vector3.Cross(s_axis0, Vector3.Cross(g.view, s_axis0));
                    if (n.sqrMagnitude < 1e-6f)
                        n = Vector3.Cross(s_axis0, g.camRight);
                    s_planeNormal = n.normalized;
                    break;
                }
                case Part.PlaneX:
                case Part.PlaneY:
                case Part.PlaneZ:
                    s_axis0 = Axis(g, AxisIndex(part));
                    s_planeNormal = s_axis0;
                    break;
                case Part.MoveView:
                    s_planeNormal = g.view;
                    break;
            }
            if (!g.rotate)
            {
                Vector3 hit;
                s_hit0 = RayPlane(HandleUtility.GUIPointToWorldRay(mouse), s_planeNormal, s_center0, out hit) ? hit : s_center0;
                s_hitLast = s_hit0;
            }
            return true;
        }

        private static bool RayPlane(Ray ray, Vector3 normal, Vector3 point, out Vector3 hit)
        {
            Plane plane = new Plane(normal, point);
            float t;
            if (plane.Raycast(ray, out t))
            {
                hit = ray.GetPoint(t);
                return true;
            }
            hit = point;
            return false;
        }

        private static float SnapAngle(float angle)
        {
            float step = EditorSnapSettings.rotate;
            if (!EditorGUI.actionKey || step <= 0f)
                return angle;
            return Mathf.Round(angle / step) * step;
        }

        private static Quaternion ToRig(AsiKeySession s, Quaternion world)
        {
            return Quaternion.Inverse(s.ctx.rigRot) * world * s.ctx.rigRot;
        }

        /// <summary>世界空间的旋转增量。层级选择时按层级根的相对轴逐级转，否则所有选中骨骼转同一个角色空间增量。</summary>
        private static void RotateRig(AsiKeySession s, Quaternion world, bool center)
        {
            if (s_hierRoot >= 0)
                s.GizmoRotateRelative(s_hierRoot, ToRig(s, world));
            else
                s.GizmoRotate(ToRig(s, world), center, s_rigCenter0);
        }

        private static void Drag(AsiKeySession s, Vector2 mouse)
        {
            bool center = Tools.pivotMode == PivotMode.Center;
            switch (s_drag)
            {
                case Part.RotX:
                case Part.RotY:
                case Part.RotZ:
                {
                    float angle = SnapAngle(Vector2.Dot(mouse - s_downMouse, s_tangent) * DegreesPerPixel);
                    s_angle = angle;
                    if (s_local)
                        s.GizmoRotateLocal(AxisIndex(s_drag), angle);
                    else
                        RotateRig(s, Quaternion.AngleAxis(angle, s_axis0), center);
                    break;
                }
                case Part.RotFree:
                {
                    Vector2 d = mouse - s_downMouse;
                    float angle = SnapAngle(d.magnitude * DegreesPerPixel);
                    s_angle = angle;
                    Vector3 axis = -(s_camUp0 * d.x + s_camRight0 * d.y);
                    Quaternion q = axis.sqrMagnitude > 1e-8f ? Quaternion.AngleAxis(angle, axis.normalized) : Quaternion.identity;
                    RotateRig(s, q, center);
                    break;
                }
                case Part.RotScreen:
                {
                    Vector2 r = mouse - (Vector2)HandleUtility.WorldToGUIPoint(s_center0);
                    if (r.sqrMagnitude > 1f)
                    {
                        float a = Mathf.Atan2(r.y, r.x) * Mathf.Rad2Deg;
                        s_accum += Mathf.DeltaAngle(s_lastAtan, a);
                        s_lastAtan = a;
                    }
                    float angle = SnapAngle(-s_accum);
                    s_angle = angle;
                    RotateRig(s, Quaternion.AngleAxis(angle, s_view0), center);
                    break;
                }
                default:
                {
                    Vector3 hit;
                    if (RayPlane(HandleUtility.GUIPointToWorldRay(mouse), s_planeNormal, s_center0, out hit))
                        s_hitLast = hit;
                    Vector3 d = s_hitLast - s_hit0;
                    if (s_drag == Part.MoveX || s_drag == Part.MoveY || s_drag == Part.MoveZ)
                        d = s_axis0 * Vector3.Dot(d, s_axis0);
                    else if (s_drag != Part.MoveView)
                        d = Vector3.ProjectOnPlane(d, s_planeNormal);
                    if (EditorGUI.actionKey)
                    {
                        Vector3 step = EditorSnapSettings.move;
                        Vector3 l = Quaternion.Inverse(s_frame0) * d;
                        l.x = step.x > 0f ? Mathf.Round(l.x / step.x) * step.x : l.x;
                        l.y = step.y > 0f ? Mathf.Round(l.y / step.y) * step.y : l.y;
                        l.z = step.z > 0f ? Mathf.Round(l.z / step.z) * step.z : l.z;
                        d = s_frame0 * l;
                    }
                    s_move = d;
                    s.GizmoMove(Quaternion.Inverse(s.ctx.rigRot) * d);
                    break;
                }
            }
        }

        // ---------------- 绘制 ----------------

        public static void Draw(AsiKeySession s, SceneView view)
        {
            if (Event.current.type != EventType.Repaint || !s_on)
                return;
            Geo g;
            if (!Compute(s, view.camera, s_rotate, out g))
                return;
            CompareFunction prevZ = Handles.zTest;
            Color prevColor = Handles.color;
            Handles.zTest = CompareFunction.Always;
            Part hot = s_drag != Part.None ? s_drag
                : s_freePress ? Part.RotFree
                : GUIUtility.hotControl == 0 && HandleUtility.nearestControl == s_id ? s_hover : Part.None;
            if (g.rotate)
                DrawRotate(g, hot);
            else
                DrawMove(g, hot);
            DrawLabel(g);
            Handles.zTest = prevZ;
            Handles.color = prevColor;
        }

        private static Color AxisColor(int k)
        {
            return k == 0 ? Handles.xAxisColor : k == 1 ? Handles.yAxisColor : Handles.zAxisColor;
        }

        private static void DrawRotate(Geo g, Part hot)
        {
            Color sel = Handles.selectedColor;
            if (hot == Part.RotFree)
            {
                Handles.color = new Color(1f, 1f, 1f, 0.07f);
                Handles.DrawSolidDisc(g.center, g.view, g.size);
            }
            Circle(g.center, g.view, g.camRight, g.size, s_circle);
            Handles.color = hot == Part.RotFree ? sel : new Color(0.65f, 0.65f, 0.65f, 0.55f);
            Handles.DrawAAPolyLine(hot == Part.RotFree ? 2.5f : 1.5f, s_circle);
            Circle(g.center, g.view, g.camRight, g.size * ScreenRingScale, s_circle);
            Handles.color = hot == Part.RotScreen ? sel : new Color(0.85f, 0.85f, 0.85f, 0.75f);
            Handles.DrawAAPolyLine(hot == Part.RotScreen ? 3.5f : 2f, s_circle);
            for (int k = 0; k < 3; k++)
            {
                if ((g.mask & (1 << k)) == 0)
                    continue;
                bool h = hot == Part.RotX + k;
                Handles.color = h ? sel : AxisColor(k);
                Handles.DrawAAPolyLine(h ? 4f : 2.75f, FrontArc(g, Axis(g, k), g.size));
            }
            if (s_drag == Part.RotX || s_drag == Part.RotY || s_drag == Part.RotZ || s_drag == Part.RotScreen)
            {
                bool screen = s_drag == Part.RotScreen;
                Vector3 axis = screen ? s_view0 : s_axis0;
                float radius = screen ? g.size * ScreenRingScale : g.size;
                Color c = screen ? Color.white : AxisColor(AxisIndex(s_drag));
                Handles.color = new Color(c.r, c.g, c.b, 0.22f);
                Handles.DrawSolidArc(g.center, axis, s_grabDir, s_angle, radius);
                Handles.color = new Color(c.r, c.g, c.b, 0.9f);
                s_seg[0] = g.center;
                s_seg[1] = g.center + s_grabDir * radius;
                Handles.DrawAAPolyLine(1.5f, s_seg);
                s_seg[1] = g.center + Quaternion.AngleAxis(s_angle, axis) * s_grabDir * radius;
                Handles.DrawAAPolyLine(1.5f, s_seg);
            }
        }

        private static void DrawMove(Geo g, Part hot)
        {
            Color sel = Handles.selectedColor;
            Color dim = new Color(0.6f, 0.6f, 0.6f, 0.4f);
            for (int k = 0; k < 3; k++)
            {
                PlaneQuad(g, k, s_quad);
                bool h = hot == Part.PlaneX + k;
                Color c = !g.canMove ? dim : h ? sel : AxisColor(k);
                Handles.DrawSolidRectangleWithOutline(s_quad, new Color(c.r, c.g, c.b, h ? 0.4f : 0.15f), new Color(c.r, c.g, c.b, 0.8f));
            }
            for (int k = 0; k < 3; k++)
            {
                Vector3 a = Axis(g, k);
                if (AxisFacesCamera(g, a))
                    continue;
                bool h = hot == Part.MoveX + k;
                Handles.color = !g.canMove ? dim : h ? sel : AxisColor(k);
                s_seg[0] = g.center;
                s_seg[1] = g.center + a * g.size;
                Handles.DrawAAPolyLine(h ? 4f : 2.75f, s_seg);
                Handles.ConeHandleCap(0, g.center + a * g.size, Quaternion.LookRotation(a), g.size * 0.15f, EventType.Repaint);
            }
            Handles.color = !g.canMove ? dim : hot == Part.MoveView ? sel : new Color(0.9f, 0.9f, 0.9f, 0.9f);
            Handles.RectangleHandleCap(0, g.center, Quaternion.LookRotation(g.view, g.camUp), g.size * 0.08f, EventType.Repaint);
            if (s_drag != Part.None && !g.rotate)
            {
                Handles.color = new Color(1f, 1f, 1f, 0.6f);
                Handles.DrawDottedLine(s_center0, g.center, 3f);
            }
        }

        private static void DrawLabel(Geo g)
        {
            if (s_drag == Part.None)
                return;
            if (s_label == null)
            {
                s_label = new GUIStyle(EditorStyles.boldLabel);
                s_label.normal.textColor = new Color(1f, 0.9f, 0.4f);
            }
            string text;
            switch (s_drag)
            {
                case Part.RotX:
                case Part.RotY:
                case Part.RotZ:
                    text = "XYZ"[AxisIndex(s_drag)] + "  " + s_angle.ToString("0.0") + "°";
                    break;
                case Part.RotFree:
                    text = "自由  " + s_angle.ToString("0.0") + "°";
                    break;
                case Part.RotScreen:
                    text = "屏幕  " + s_angle.ToString("0.0") + "°";
                    break;
                default:
                {
                    Vector3 l = Quaternion.Inverse(s_frame0) * s_move;
                    text = "Δ  " + l.x.ToString("0.000") + ", " + l.y.ToString("0.000") + ", " + l.z.ToString("0.000");
                    break;
                }
            }
            Handles.Label(g.center + g.camUp * g.size * (g.rotate ? ScreenRingScale + 0.15f : 1.15f), text, s_label);
        }
    }
}
