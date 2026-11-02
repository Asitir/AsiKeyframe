using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>主窗口「单位」页签：工程父级、添加单位、选择筛选、单位列表（可选 / 可显 / 改名 / 换目标 / 删除）。</summary>
    public partial class AsiKeyWindow
    {
        private int _renameUnit = -1;
        private string _renameText = "";

        private static readonly string[] KindNames = { "人形", "骨骼", "物体" };

        private void DrawUnitsTab(AsiKeySession s)
        {
            GUILayout.Label("工程父级", Header);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField(s.projectRoot, typeof(Transform), true);
            if (GUILayout.Button(new GUIContent("换成选中物体", "全部单位必须在新父级下面；物体单位的关键帧会换算，场景里的位置不变"), GUILayout.Width(90f)))
                SetRootToSelection();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6f);
            GUILayout.Label("添加单位", Header);
            EditorGUILayout.HelpBox("只有加进工程的单位才能 K 帧。单位必须在工程父级下面，且单位之间不能互相嵌套。\n" +
                "带人形 Avatar 的 Animator 按 Biped 生成控制器；其它 Animator 按蒙皮骨骼生成；镜头、灯光、道具等作为物体加入，只有一个控制器。", MessageType.None);
            List<Transform> picks = SelectedUnitCandidates(s);
            using (new EditorGUI.DisabledScope(picks.Count == 0))
            {
                EditorGUILayout.BeginHorizontal();
                string what = picks.Count == 1 ? "「" + picks[0].name + "」" : picks.Count + " 个选中物体";
                if (GUILayout.Button(new GUIContent("把 " + what + " 加为单位", "有 Animator 的自动识别为角色，否则作为物体"), GUILayout.Height(24f)))
                    AddUnits(s, picks, false);
                if (GUILayout.Button(new GUIContent("作为物体加入", "即使带 Animator 也只当成一个物体（只 K 它本身的位置旋转）"), GUILayout.Width(96f), GUILayout.Height(24f)))
                    AddUnits(s, picks, true);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(6f);
            GUILayout.Label("选择筛选", Header);
            DrawFilterControls(s);

            EditorGUILayout.Space(6f);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("单位（" + _project.units.Count + "）", Header);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(_project.units.Count == 0))
            {
                if (GUILayout.Button("全部可选", EditorStyles.miniButtonLeft, GUILayout.Width(64f)))
                    AsiKeyPanels.SetUnitFlags(s, -1, true, null);
                if (GUILayout.Button("全部可显", EditorStyles.miniButtonRight, GUILayout.Width(64f)))
                    AsiKeyPanels.SetUnitFlags(s, -1, null, true);
            }
            EditorGUILayout.EndHorizontal();
            if (_project.units.Count == 0)
                EditorGUILayout.HelpBox("还没有单位。在场景里选中工程父级下的角色或物体，点上面的按钮加入。", MessageType.Info);
            for (int u = 0; u < _project.units.Count; u++)
                DrawUnitRow(s, u);
        }

        /// <summary>选择里可以加为单位的物体：场景物体、不是 K帧控制器、还不是单位。</summary>
        private static List<Transform> SelectedUnitCandidates(AsiKeySession s)
        {
            List<Transform> list = new List<Transform>();
            Transform[] sel = Selection.transforms;
            for (int k = 0; k < sel.Length; k++)
            {
                Transform t = sel[k];
                if (t == null || EditorUtility.IsPersistent(t.gameObject) || s.ControlIndex(t) >= 0 || AsiKeyRigObjects.IsRigObject(t))
                    continue;
                bool already = false;
                for (int u = 0; u < s.UnitSlots; u++)
                    already |= s.UnitTarget(u) == t;
                if (!already)
                    list.Add(t);
            }
            return list;
        }

        private static void AddUnits(AsiKeySession s, List<Transform> picks, bool asObject)
        {
            List<string> errors = new List<string>();
            int added = 0;
            for (int k = 0; k < picks.Count; k++)
            {
                Transform t = picks[k];
                if (t == null)
                    continue;
                string err = s.AddUnit(t, asObject ? AsiUnitKind.Object : AsiKeyUnits.DetectKind(t));
                if (string.IsNullOrEmpty(err))
                    added++;
                else
                    errors.Add(t.name + "：" + err);
            }
            if (errors.Count > 0)
            {
                s.status = string.Join("；", errors);
                if (added == 0)
                    EditorUtility.DisplayDialog("K帧工具", string.Join("\n", errors), "好");
            }
            else
                s.status = "已添加 " + added + " 个单位";
            AsiKeySession.RaiseRepaint();
        }

        private void DrawFilterControls(AsiKeySession s)
        {
            AsiSelectFilter f = AsiKeyRigDrawer.Filter;
            string[] labels = new string[5];
            for (int i = 0; i < labels.Length; i++)
                labels[i] = AsiKeyRigDrawer.FilterLabel(null, (AsiSelectFilter)i);
            labels[(int)AsiSelectFilter.OneUnit] = "仅选择某单位的骨骼";
            EditorGUI.BeginChangeCheck();
            int pick = EditorGUILayout.Popup(new GUIContent("Scene 点选", "不筛选时用 Unity 默认点选；其它模式只点选/框选通过筛选的 K帧控制器"), (int)f, labels);
            if (EditorGUI.EndChangeCheck())
                AsiKeyRigDrawer.Filter = (AsiSelectFilter)pick;
            if (AsiKeyRigDrawer.Filter == AsiSelectFilter.OneUnit && _project.units.Count > 0)
            {
                string[] names = new string[_project.units.Count];
                for (int u = 0; u < names.Length; u++)
                    names[u] = (u + 1) + ". " + _project.UnitName(u);
                EditorGUI.BeginChangeCheck();
                int unit = EditorGUILayout.Popup("　只选单位", Mathf.Clamp(_project.selectFilterUnit, 0, names.Length - 1), names);
                if (EditorGUI.EndChangeCheck())
                    AsiKeyRigDrawer.FilterOneUnit(_project, unit);
            }
            if (AsiKeyRigDrawer.Filter == AsiSelectFilter.Custom)
                EditorGUILayout.HelpBox("在下面的单位列表里勾选「可选」决定哪些单位能在 Scene 里点选。", MessageType.None);
        }

        private void DrawUnitRow(AsiKeySession s, int u)
        {
            AsiKeyUnit unit = _project.units[u];
            Transform target = s.UnitTarget(u);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label((u + 1) + ".", GUILayout.Width(20f));
            if (_renameUnit == u)
            {
                GUI.SetNextControlName("AsiKeyUnitRename");
                _renameText = EditorGUILayout.TextField(_renameText);
                if (GUILayout.Button("确定", EditorStyles.miniButton, GUILayout.Width(40f)))
                {
                    s.RenameUnit(u, _renameText);
                    _renameUnit = -1;
                }
                if (GUILayout.Button("取消", EditorStyles.miniButton, GUILayout.Width(40f)))
                    _renameUnit = -1;
            }
            else
            {
                GUILayout.Label(new GUIContent(_project.UnitName(u), unit.targetPath), EditorStyles.boldLabel);
                GUILayout.Label(KindNames[Mathf.Clamp((int)unit.kind, 0, KindNames.Length - 1)], EditorStyles.miniLabel, GUILayout.Width(30f));
                GUILayout.FlexibleSpace();
                bool sel = GUILayout.Toggle(unit.selectable, new GUIContent("可选", "「自定义可选单位」筛选下能否在 Scene 里点选"), EditorStyles.miniButtonLeft, GUILayout.Width(40f));
                bool vis = GUILayout.Toggle(unit.visible, new GUIContent("可显", "显示该单位的控制器；不可显时也不能选"), EditorStyles.miniButtonRight, GUILayout.Width(40f));
                if (sel != unit.selectable)
                    AsiKeyPanels.SetUnitFlags(s, u, sel, null);
                if (vis != unit.visible)
                    AsiKeyPanels.SetUnitFlags(s, u, null, vis);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField(target, typeof(Transform), true);
            int bones = 0;
            for (int i = 0; i < _project.bones.Count; i++)
            {
                if (_project.bones[i].unit == u)
                    bones++;
            }
            GUILayout.Label(bones + " 根", EditorStyles.miniLabel, GUILayout.Width(44f));
            EditorGUILayout.EndHorizontal();
            if (target == null)
                EditorGUILayout.HelpBox("找不到目标物体：" + unit.targetPath + "。可以选中新物体后点「换成选中」。", MessageType.Warning);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("选控制器", "在 Scene 里选中该单位的全部控制器"), EditorStyles.miniButtonLeft))
                SelectUnitControls(s, u);
            using (new EditorGUI.DisabledScope(target == null))
            {
                if (GUILayout.Button("选目标", EditorStyles.miniButtonMid))
                    Selection.activeGameObject = target.gameObject;
            }
            if (GUILayout.Button("改名", EditorStyles.miniButtonMid))
            {
                _renameUnit = u;
                _renameText = _project.UnitName(u);
            }
            Transform pickT = SelectedSceneTransform();
            using (new EditorGUI.DisabledScope(pickT == null || pickT == target || s.ControlIndex(pickT) >= 0))
            {
                if (GUILayout.Button(new GUIContent("换成选中", "把单位的目标换成场景里选中的物体，种类不同时重建骨骼"), EditorStyles.miniButtonMid))
                {
                    string err = s.RetargetUnit(u, pickT);
                    if (!string.IsNullOrEmpty(err))
                        s.status = err;
                }
            }
            if (GUILayout.Button(new GUIContent("重建", "按目标重新生成该单位的控制器，清空它的动画"), EditorStyles.miniButtonMid)
                && EditorUtility.DisplayDialog("K帧工具", "重建「" + _project.UnitName(u) + "」的控制器会清空它的全部关键帧、IK 链和约束，确定？", "重建", "取消"))
            {
                string err = s.RebuildUnit(u);
                if (!string.IsNullOrEmpty(err))
                    s.status = err;
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("删除", EditorStyles.miniButtonRight)
                && EditorUtility.DisplayDialog("K帧工具", "删除单位「" + _project.UnitName(u) + "」及它的控制器、关键帧、IK 链和相关约束？", "删除", "取消"))
            {
                s.RemoveUnit(u);
                if (_unit >= _project.units.Count)
                    _unit = Mathf.Max(0, _project.units.Count - 1);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawConstraintsTab(AsiKeySession s)
        {
            EditorGUILayout.HelpBox("仿 3ds Max 的约束：位置、旋转、注视、链接。约束在求值时和 IK 链一起按依赖顺序计算，可以跨单位，烘焙时会写进动画。\n" +
                "选中要被约束的控制器（同时选中的其它控制器直接作为目标），点下面的按钮；没有其它选中时在 Scene 里点选目标。\n" +
                "影响度为 1 时拖动被约束的骨骼会改写约束偏移；影响度在 0 和 1 之间时不能拖动该通道。Scene 里也可以打开「K帧 约束」面板。", MessageType.None);
            AsiKeyPanels.DrawConstraintsPanel(s, true);
        }

        private static void SelectUnitControls(AsiKeySession s, int u)
        {
            List<int> list = new List<int>();
            for (int i = 0; i < s.project.bones.Count; i++)
            {
                if (s.project.bones[i].unit == u && AsiKeyRigDrawer.Pickable(s, i))
                    list.Add(i);
            }
            if (list.Count == 0)
                s.status = "该单位没有可见的控制器";
            s.SelectBones(list, false);
        }
    }
}
