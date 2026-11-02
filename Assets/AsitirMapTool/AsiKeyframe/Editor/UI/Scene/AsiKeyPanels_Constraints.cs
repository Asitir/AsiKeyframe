using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>约束面板（Scene「K帧 约束」与主窗口「约束」页签共用）：添加、目标、影响度、偏移、注视轴向、链接条目、塌陷。</summary>
    public static partial class AsiKeyPanels
    {
        private static readonly string[] AxisNames = { "+X", "+Y", "+Z", "-X", "-Y", "-Z" };
        private static readonly string[] UpModeNames = { "工程父级的 Y 轴", "物体的轴", "不指定（最小旋转）" };
        private static readonly HashSet<AsiKeyConstraint> s_closed = new HashSet<AsiKeyConstraint>();

        /// <summary>选中控制器里的「主」骨骼：Unity 的活动物体是控制器时用它，否则用第一个。</summary>
        public static int ActiveBone(AsiKeySession s, List<int> sel)
        {
            Transform t = Selection.activeTransform;
            int i = t != null ? s.ControlIndex(t) : -1;
            if (i >= 0)
                return i;
            return sel.Count > 0 ? sel[0] : -1;
        }

        /// <summary>
        /// 仿 Max：给活动控制器加约束；同时选中的其它控制器直接作为目标（链接约束取第一个作为父级），没有其它选中时进入拾取目标模式。
        /// </summary>
        public static void AddConstraintFromSelection(AsiKeySession s, AsiConstraintKind kind)
        {
            List<int> sel = s.SelectedBones();
            int bone = ActiveBone(s, sel);
            int k = s.AddConstraint(bone, kind);
            if (k < 0)
            {
                Repaint();
                return;
            }
            int added = 0;
            string error = "";
            for (int n = 0; n < sel.Count; n++)
            {
                if (sel[n] == bone)
                    continue;
                string err = s.AddConstraintTarget(k, sel[n], "");
                if (string.IsNullOrEmpty(err))
                    added++;
                else
                    error = err;
                if (kind == AsiConstraintKind.Link && added > 0)
                    break;
            }
            if (added == 0)
                s.BeginPick(k, AsiKeySession.PickRole.Target);
            else if (!string.IsNullOrEmpty(error))
                s.status = error;
            SetShown(Constraints, true);
            Repaint();
        }

        public static void DrawConstraintsPanel(AsiKeySession s, bool all)
        {
            if (!Begin(s))
                return;
            List<int> sel = s.SelectedBones();
            int active = ActiveBone(s, sel);
            EditorGUILayout.LabelField("被约束骨骼", active >= 0 ? s.TargetName(active, "") : "（先选中一个控制器）", EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(active < 0))
            {
                EditorGUILayout.BeginHorizontal();
                string tip = "给活动控制器加约束；同时选中的其它控制器直接作为目标，否则之后在 Scene 里点选目标";
                if (GUILayout.Button(new GUIContent("位置", tip), EditorStyles.miniButtonLeft))
                    AddConstraintFromSelection(s, AsiConstraintKind.Position);
                if (GUILayout.Button(new GUIContent("旋转", tip), EditorStyles.miniButtonMid))
                    AddConstraintFromSelection(s, AsiConstraintKind.Orientation);
                if (GUILayout.Button(new GUIContent("注视", tip), EditorStyles.miniButtonMid))
                    AddConstraintFromSelection(s, AsiConstraintKind.LookAt);
                if (GUILayout.Button(new GUIContent("链接", "从当前帧起把活动控制器挂到另一个控制器或物体下（可以多次切换父级）"), EditorStyles.miniButtonRight))
                    AddConstraintFromSelection(s, AsiConstraintKind.Link);
                EditorGUILayout.EndHorizontal();
            }
            if (s.Picking)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox("正在为「" + _project.constraints[s.pickConstraint].name + "」拾取" + (s.pickRole == AsiKeySession.PickRole.UpObject ? "上方向物体" : "目标") +
                    "：在 Scene 里点控制器或物体（Shift 连续拾取，Esc 取消）。", MessageType.Info);
                if (GUILayout.Button("结束", GUILayout.Width(40f), GUILayout.Height(36f)))
                    s.EndPick();
                EditorGUILayout.EndHorizontal();
            }
            int shown = 0;
            for (int k = 0; k < _project.constraints.Count; k++)
            {
                AsiKeyConstraint c = _project.constraints[k];
                if (!all && !sel.Contains(c.bone))
                    continue;
                shown++;
                if (DrawConstraint(s, k))
                    break;
            }
            if (shown == 0)
                EditorGUILayout.HelpBox(all ? "工程里还没有约束。" : "选中的控制器上没有约束。", MessageType.None);
        }

        /// <summary>返回 true 表示约束列表变了，本帧停止绘制。</summary>
        private static bool DrawConstraint(AsiKeySession s, int k)
        {
            AsiKeyConstraint c = _project.constraints[k];
            bool open = !s_closed.Contains(c);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            bool nowOpen = EditorGUILayout.Foldout(open, c.name, true);
            if (nowOpen != open)
            {
                if (nowOpen) s_closed.Remove(c);
                else s_closed.Add(c);
            }
            GUILayout.FlexibleSpace();
            EditorGUI.BeginChangeCheck();
            bool en = GUILayout.Toggle(c.enabled, new GUIContent("启用"), GUILayout.Width(44f));
            if (EditorGUI.EndChangeCheck())
                s.EditConstraint(k, "启用约束", x => x.enabled = en);
            if (GUILayout.Button(new GUIContent("×", "删除约束"), EditorStyles.miniButton, GUILayout.Width(20f)))
            {
                s.RemoveConstraint(k);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return true;
            }
            EditorGUILayout.EndHorizontal();
            if (!nowOpen)
            {
                EditorGUILayout.EndVertical();
                return false;
            }

            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.DelayedTextField("名称", c.name);
            if (EditorGUI.EndChangeCheck())
                s.EditConstraint(k, "约束名称", x => x.name = name);
            EditorGUILayout.LabelField("被约束", s.TargetName(c.bone, "") + "　" + AsiKeySession.ConstraintKindNames[(int)c.kind], EditorStyles.miniLabel);

            float inf = AsiKeyConstraints.Influence(_project, s.pose, k);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            float w = EditorGUILayout.Slider(new GUIContent("影响度", "0 = 不起作用，1 = 完全跟随目标。有关键帧或开着自动关键点时改动会打关键帧"), c.enabled ? inf : c.weight, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
                s.SetConstraintWeight(k, w);
            if (GUILayout.Button(new GUIContent("K", "在当前帧给影响度打关键帧"), EditorStyles.miniButton, GUILayout.Width(22f)))
                s.KeyConstraintWeight(k, c.enabled ? inf : c.weight);
            EditorGUILayout.EndHorizontal();
            if (c.enabled && !AsiKeyConstraints.Affects(_project, s.pose, k))
                EditorGUILayout.LabelField(InactiveReason(s, k), EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            bool keep = EditorGUILayout.ToggleLeft(new GUIContent("保持偏移", "添加目标时记住当前相对位姿，骨骼不跳到目标上"), c.keepOffset, GUILayout.Width(80f));
            if (EditorGUI.EndChangeCheck())
                s.SetKeepOffset(k, keep);
            if (GUILayout.Button(new GUIContent("重新抓取偏移", "按当前帧的姿势重新计算偏移"), EditorStyles.miniButtonLeft))
                s.RegrabOffset(k);
            if (GUILayout.Button("清除偏移", EditorStyles.miniButtonRight))
                s.EditConstraint(k, "清除偏移", x => AsiKeyConstraints.ClearOffset(x));
            EditorGUILayout.EndHorizontal();

            if (c.kind == AsiConstraintKind.Link)
            {
                if (DrawLinks(s, k, c))
                {
                    EditorGUILayout.EndVertical();
                    return true;
                }
            }
            else
            {
                if (DrawTargets(s, k, c))
                {
                    EditorGUILayout.EndVertical();
                    return true;
                }
                if (c.kind == AsiConstraintKind.LookAt)
                    DrawLookAt(s, k, c);
            }

            if (GUILayout.Button(new GUIContent("塌陷为关键帧", "把工程范围内被约束骨骼的结果写成基础层关键帧，然后关闭约束")))
            {
                string err = s.CollapseConstraint(k);
                if (!string.IsNullOrEmpty(err))
                    s.status = err;
            }
            EditorGUILayout.EndVertical();
            return false;
        }

        private static string InactiveReason(AsiKeySession s, int k)
        {
            AsiKeyConstraint c = _project.constraints[k];
            if (AsiKeyConstraints.Influence(_project, s.pose, k) <= AsiKeyConstraints.WeightEps)
                return "（当前帧影响度为 0）";
            if (c.kind == AsiConstraintKind.Position && !_project.Movable(c.bone))
                return "（骨骼不能移动，位置约束不起作用）";
            if (c.kind == AsiConstraintKind.Link)
                return "（当前帧早于第一个链接条目）";
            return "（还没有目标）";
        }

        private static bool DrawTargets(AsiKeySession s, int k, AsiKeyConstraint c)
        {
            GUILayout.Label("目标", EditorStyles.boldLabel);
            for (int t = 0; t < c.targets.Count; t++)
            {
                AsiConstraintTarget tg = c.targets[t];
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(s.TargetName(tg.bone, tg.obj), GUILayout.MinWidth(80f));
                EditorGUI.BeginChangeCheck();
                float w = EditorGUILayout.FloatField(tg.weight, GUILayout.Width(44f));
                if (EditorGUI.EndChangeCheck())
                {
                    int ti = t;
                    s.EditConstraint(k, "目标权重", x => x.targets[ti].weight = Mathf.Max(0f, w));
                }
                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20f)))
                {
                    s.RemoveConstraintTarget(k, t);
                    EditorGUILayout.EndHorizontal();
                    return true;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("拾取目标", "在 Scene 里点控制器或场景物体")))
                s.BeginPick(k, AsiKeySession.PickRole.Target);
            Transform drop = (Transform)EditorGUILayout.ObjectField(null, typeof(Transform), true);
            if (drop != null)
            {
                string err = s.AddConstraintTarget(k, drop);
                if (!string.IsNullOrEmpty(err))
                    s.status = err;
            }
            EditorGUILayout.EndHorizontal();
            return false;
        }

        private static void DrawLookAt(AsiKeySession s, int k, AsiKeyConstraint c)
        {
            GUILayout.Label("注视", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            int aim = EditorGUILayout.Popup(new GUIContent("瞄准轴", "骨骼的哪根轴指向目标"), c.aimAxis, AxisNames);
            int up = EditorGUILayout.Popup(new GUIContent("上方向轴", "骨骼的哪根轴尽量朝上方向"), c.upAxis, AxisNames);
            AsiLookUp mode = (AsiLookUp)EditorGUILayout.Popup("上方向", (int)c.upMode, UpModeNames);
            int upTarget = c.upTargetAxis;
            if (mode == AsiLookUp.Object)
                upTarget = EditorGUILayout.Popup("物体的轴", c.upTargetAxis, AxisNames);
            if (EditorGUI.EndChangeCheck())
            {
                s.EditConstraint(k, "注视设置", x =>
                {
                    x.aimAxis = aim;
                    x.upAxis = up;
                    x.upMode = mode;
                    x.upTargetAxis = upTarget;
                    if (x.keepOffset)
                        AsiKeyConstraints.GrabOffset(_project, s.ctx, s.frame, k);
                });
            }
            if (c.upMode != AsiLookUp.Object)
                return;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("上方向物体：" + (c.upBone >= 0 || !string.IsNullOrEmpty(c.upObj) ? s.TargetName(c.upBone, c.upObj) : "（未指定，用工程父级）"), EditorStyles.miniLabel);
            if (GUILayout.Button("拾取", EditorStyles.miniButton, GUILayout.Width(40f)))
                s.BeginPick(k, AsiKeySession.PickRole.UpObject);
            EditorGUILayout.EndHorizontal();
        }

        private static bool DrawLinks(AsiKeySession s, int k, AsiKeyConstraint c)
        {
            GUILayout.Label("链接关键帧（琥珀菱形，时间轴上可拖；只改帧号，相对父级的位置和角度不变）", EditorStyles.boldLabel);
            int activeLink = AsiKeyConstraints.ActiveLink(c, s.frame);
            for (int e = 0; e < c.links.Count; e++)
            {
                AsiLinkEntry le = c.links[e];
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label((e == activeLink ? "▶ " : "　") + "第 " + le.frame.ToString("0") + " 帧", GUILayout.Width(80f));
                GUILayout.Label(s.TargetName(le.bone, le.obj));
                if (GUILayout.Button(new GUIContent("跳到", "跳到这一帧"), EditorStyles.miniButtonLeft, GUILayout.Width(36f)))
                    s.GoTo(le.frame);
                if (GUILayout.Button("×", EditorStyles.miniButtonRight, GUILayout.Width(20f)))
                {
                    s.RemoveLinkEntry(k, e);
                    EditorGUILayout.EndHorizontal();
                    return true;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("当前帧链接到…", "在 Scene 里点新的父级（控制器或场景物体）")))
                s.BeginPick(k, AsiKeySession.PickRole.Target);
            if (GUILayout.Button(new GUIContent("当前帧链接到世界", "从当前帧起不跟随任何物体（相对工程父级）")))
            {
                string err = s.SetLinkAtFrame(k, -1, "");
                if (!string.IsNullOrEmpty(err))
                    s.status = err;
            }
            EditorGUILayout.EndHorizontal();
            Transform drop = (Transform)EditorGUILayout.ObjectField("拖入父级", null, typeof(Transform), true);
            if (drop != null)
            {
                string err = s.AddConstraintTarget(k, drop);
                if (!string.IsNullOrEmpty(err))
                    s.status = err;
            }
            return false;
        }
    }
}
