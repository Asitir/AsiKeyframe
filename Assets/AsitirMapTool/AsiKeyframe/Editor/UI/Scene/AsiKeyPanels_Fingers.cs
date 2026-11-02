using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>每指 IK 开关。卷曲、张开和错落在手掌骨骼上拖，不在这个面板里。Motion 里默认收起。</summary>
    public static partial class AsiKeyPanels
    {
        private const string FingerFoldPref = "AsiKey.Motion.Fingers";
        /// <summary>sliders 已不用，保留参数以免旧的调用点编译失败。默认收起，一般不看。</summary>
        public static void DrawFingers(AsiKeySession s, bool sliders)
        {
            if (!Begin(s) || !AsiKeyFingers.Any(_project))
                return;
            EditorGUILayout.Space(6f);
            bool open = EditorPrefs.GetBool(FingerFoldPref, false);
            bool next = EditorGUILayout.Foldout(open, "手指", true, EditorStyles.foldoutHeader);
            if (next != open)
                EditorPrefs.SetBool(FingerFoldPref, next);
            if (!next)
                return;
            EditorGUILayout.LabelField(sliders
                ? "选中手掌，在骨骼上拖卷曲、张开、错落，直接改各指节的姿势。正在 IK 的手指会跳过。"
                : "卷曲、张开和错落在手掌骨骼上，直接改指节关键帧。", EditorStyles.wordWrappedMiniLabel);

            for (int u = 0; u < _project.units.Count; u++)
            {
                if (!_project.UnitIsHumanoid(u))
                    continue;
                for (int side = 0; side < 2; side++)
                {
                    if (!AsiKeyFingers.HandHasFinger(_project, u, side))
                        continue;
                    DrawHand(s, u, side);
                }
            }
        }

        private static void DrawHand(AsiKeySession s, int unit, int side)
        {
            string title = (_project.units.Count > 1 ? _project.UnitName(unit) + " " : "") + (side == 0 ? "左手" : "右手");
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(title, EditorStyles.boldLabel);
            AsiFingerRef picked;
            bool pickedOn;
            bool toggle = DrawIkToggles(unit, side, out picked, out pickedOn);
            EditorGUILayout.EndVertical();
            if (!toggle)
                return;
            if (!pickedOn && !EditorUtility.DisplayDialog("K帧工具",
                    "关闭「" + AsiKeyFingers.Label(picked.side, picked.id) + "」的 IK？关键帧会拆回各节的旋转，IK 参数丢失。", "关闭", "取消"))
                GUIUtility.ExitGUI();
            string err = s.SetFingerIK(picked, pickedOn);
            if (!string.IsNullOrEmpty(err))
                s.status = err;
            GUIUtility.ExitGUI();
        }

        private static bool DrawIkToggles(int unit, int side, out AsiFingerRef picked, out bool pickedOn)
        {
            picked = new AsiFingerRef();
            pickedOn = false;
            bool have = false;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("IK", "为这一指建链：近节到远节。关掉会拆回逐节旋转，IK 参数丢失。"), GUILayout.Width(22f));
            for (int id = 0; id < AsiKeyFingers.Count; id++)
            {
                AsiFingerRef f;
                if (!AsiKeyFingers.TryFinger(_project, unit, side, id, out f))
                    continue;
                bool on = AsiKeyFingers.FindChain(_project, f) >= 0;
                bool next = GUILayout.Toggle(on, new GUIContent(AsiKeyFingers.Names[id], "用这几节已有的控制器做 IK"), EditorStyles.miniButton);
                if (next != on)
                {
                    picked = f;
                    pickedOn = next;
                    have = true;
                }
            }
            EditorGUILayout.EndHorizontal();
            return have;
        }
    }
}
