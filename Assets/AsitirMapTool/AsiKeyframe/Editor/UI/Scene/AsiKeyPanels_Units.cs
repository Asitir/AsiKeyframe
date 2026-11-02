using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>Scene「K帧 单位」面板：选择筛选、全部可选 / 全部可显、各单位的可选 / 可显开关。增删单位在主窗口的「单位」页签。</summary>
    public static partial class AsiKeyPanels
    {
        public static void DrawUnitsPanel(AsiKeySession s)
        {
            if (!Begin(s))
                return;
            AsiSelectFilter f = AsiKeyRigDrawer.Filter;
            if (GUILayout.Button(new GUIContent("选择筛选：" + AsiKeyRigDrawer.FilterLabel(_project, f) + " ▾"), EditorStyles.popup))
            {
                GenericMenu menu = new GenericMenu();
                AsiKeySceneMenu.AddFilterItems(menu, s, "");
                menu.ShowAsContext();
            }
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_project.units.Count == 0))
            {
                if (GUILayout.Button("全部可选", EditorStyles.miniButtonLeft))
                    SetUnitFlags(s, -1, true, null);
                if (GUILayout.Button("全部可显", EditorStyles.miniButtonRight))
                    SetUnitFlags(s, -1, null, true);
            }
            EditorGUILayout.EndHorizontal();
            if (_project.units.Count == 0)
            {
                EditorGUILayout.HelpBox("还没有单位。在 K帧工具窗口的「单位」页签添加。", MessageType.None);
                return;
            }
            for (int u = 0; u < _project.units.Count; u++)
            {
                AsiKeyUnit unit = _project.units[u];
                EditorGUILayout.BeginHorizontal();
                bool focus = f == AsiSelectFilter.OneUnit && _project.selectFilterUnit == u;
                string label = _project.UnitName(u) + (unit.IsCharacter ? "" : "（物体）");
                if (GUILayout.Button(new GUIContent(label, "单击选中该单位的全部控制器"), focus ? EditorStyles.boldLabel : EditorStyles.label))
                    SelectUnit(s, u);
                GUILayout.FlexibleSpace();
                bool sel = GUILayout.Toggle(unit.selectable, new GUIContent("可选", "「自定义可选单位」筛选下能否点选"), EditorStyles.miniButtonLeft, GUILayout.Width(38f));
                bool vis = GUILayout.Toggle(unit.visible, new GUIContent("可显", "显示该单位的控制器"), EditorStyles.miniButtonRight, GUILayout.Width(38f));
                EditorGUILayout.EndHorizontal();
                if (sel != unit.selectable)
                    SetUnitFlags(s, u, sel, null);
                if (vis != unit.visible)
                    SetUnitFlags(s, u, null, vis);
            }
        }

        private static void SelectUnit(AsiKeySession s, int u)
        {
            List<int> list = new List<int>();
            for (int i = 0; i < _project.bones.Count; i++)
            {
                if (_project.bones[i].unit == u && AsiKeyRigDrawer.Pickable(s, i))
                    list.Add(i);
            }
            s.SelectBones(list, false);
        }
    }
}
