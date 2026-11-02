using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>选中手掌时，在手掌骨骼上画卷曲、张开、错落。拖动直接改各指节的旋转。</summary>
    public static class AsiKeyFingerHandle
    {
        private static readonly string[] Names = { "卷曲", "张开", "错落" };
        private static readonly Color[] Colors =
        {
            new Color(1f, 0.55f, 0.2f),
            new Color(0.35f, 0.8f, 1f),
            new Color(0.75f, 0.45f, 1f),
        };

        private static int s_id;
        private static int s_which = -1;
        private static int s_hand = -1;
        private static Vector2 s_press;
        private static readonly Vector3[] s_pos = new Vector3[3];

        /// <summary>处理拖动。返回 true 时这一帧不要再把事件交给变换轴。</summary>
        public static bool Input(AsiKeySession s, SceneView view, Event e)
        {
            s_id = GUIUtility.GetControlID("AsiKey.Finger".GetHashCode(), FocusType.Passive);
            if (s == null || !s.Valid || view == null || view.camera == null)
                return false;
            int hand = Hand(s);
            bool ours = GUIUtility.hotControl == s_id;
            if (hand < 0 && !ours)
                return false;
            if ((hand >= 0 ? hand : s_hand) >= 0)
                Place(s, hand >= 0 ? hand : s_hand);

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && ours)
            {
                s.CancelFingerPose();
                End();
                e.Use();
                return true;
            }
            if (e.type == EventType.MouseUp && e.button == 0 && ours)
            {
                s.EndFingerPose();
                End();
                e.Use();
                return true;
            }
            if (e.type == EventType.MouseDrag && ours)
            {
                float deg = (e.mousePosition.x - s_press.x) * 0.45f;
                float curl = s_which == 0 ? deg : 0f;
                float spread = s_which == 1 ? deg : 0f;
                float stagger = s_which == 2 ? deg : 0f;
                s.DragFingerPose(curl, spread, stagger);
                e.Use();
                return true;
            }
            if (e.type != EventType.MouseDown || e.button != 0 || e.alt || ours || hand < 0 || GUIUtility.hotControl != 0)
                return ours;
            int hit = Pick(e.mousePosition);
            if (hit < 0)
                return false;
            s_which = hit;
            s_hand = hand;
            s_press = e.mousePosition;
            GUIUtility.hotControl = s_id;
            s.BeginFingerPose(hand);
            e.Use();
            return true;
        }

        public static void Draw(AsiKeySession s, SceneView view)
        {
            if (s == null || !s.Valid || view == null || view.camera == null || Event.current.type != EventType.Repaint)
                return;
            int hand = Hand(s);
            if (hand < 0)
                return;
            Place(s, hand);
            UnityEngine.Rendering.CompareFunction prev = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            for (int i = 0; i < 3; i++)
            {
                float size = HandleUtility.GetHandleSize(s_pos[i]) * 0.07f;
                Handles.color = Colors[i];
                Handles.DrawWireDisc(s_pos[i], view.camera.transform.forward, size);
                Handles.DrawSolidDisc(s_pos[i], view.camera.transform.forward, size * 0.45f);
                Handles.Label(s_pos[i], " " + Names[i]);
            }
            Handles.zTest = prev;
        }

        private static int Hand(AsiKeySession s)
        {
            int bone = s.HierarchyRoot >= 0 ? s.HierarchyRoot : s.ControlIndex(Selection.activeTransform);
            int unit;
            int side;
            return AsiKeyFingers.TryHand(s.project, bone, out unit, out side) ? bone : -1;
        }

        private static void Place(AsiKeySession s, int hand)
        {
            Vector3 center = s.ctx.ToWorldPos(s.pose.wpos[hand]);
            float step = HandleUtility.GetHandleSize(center) * 0.28f;
            Vector3 across = Across(s, hand);
            if (across.sqrMagnitude < 1e-8f)
                across = s.ctx.ToWorldRot(s.pose.wrot[hand]) * Vector3.right;
            across.Normalize();
            Vector3 origin = center;
            s_pos[0] = origin - across * step;
            s_pos[1] = origin;
            s_pos[2] = origin + across * step;
        }

        /// <summary>从小指近节指向食指近节，用来把三个手柄排开。</summary>
        private static Vector3 Across(AsiKeySession s, int hand)
        {
            int unit;
            int side;
            if (!AsiKeyFingers.TryHand(s.project, hand, out unit, out side))
                return Vector3.zero;
            int little = -1;
            int index = -1;
            AsiFingerRef f;
            if (AsiKeyFingers.TryFinger(s.project, unit, side, 4, out f))
                little = AsiKeyFingers.SpreadJoint(f);
            if (AsiKeyFingers.TryFinger(s.project, unit, side, 1, out f))
                index = AsiKeyFingers.SpreadJoint(f);
            if (little < 0 || index < 0)
                return Vector3.zero;
            return s.ctx.ToWorldPos(s.pose.wpos[index]) - s.ctx.ToWorldPos(s.pose.wpos[little]);
        }

        private static int Pick(Vector2 mouse)
        {
            int best = -1;
            float bestD = 16f;
            for (int i = 0; i < 3; i++)
            {
                float d = Vector2.Distance(HandleUtility.WorldToGUIPoint(s_pos[i]), mouse);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }

        private static void End()
        {
            if (GUIUtility.hotControl == s_id)
                GUIUtility.hotControl = 0;
            s_which = -1;
            s_hand = -1;
        }
    }
}
