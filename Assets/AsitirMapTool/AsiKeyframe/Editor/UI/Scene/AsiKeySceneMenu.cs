using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// Scene 视图里选中 K帧控制器后的右键菜单（右键单击 / Alt+右键单击），以及删除、复制、粘贴命令的接管。
    /// </summary>
    public static class AsiKeySceneMenu
    {
        private const float ClickTolerance = 4f;
        private const double ClickTime = 0.6;
        private static readonly float[] ShapeScales = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f };

        private static bool s_armed;
        private static bool s_alt;
        private static Vector2 s_downPos;
        private static double s_downTime;
        private static Vector3 s_pivot;
        private static Quaternion s_rot;
        private static float s_size;

        /// <summary>
        /// 右键按下到松开之间鼠标和相机都没动才算单击。不吃掉事件：右键漫游与 Alt+右键缩放在松开时照常结束。
        /// </summary>
        public static void HandleContextClick(AsiKeySession s, SceneView view, Event e)
        {
            if (e.button != 1)
                return;
            if (e.rawType == EventType.MouseDown)
            {
                s_armed = AnyControlSelected(s) && !AsiKeyGizmo.Holding;
                s_alt = e.alt;
                s_downPos = e.mousePosition;
                s_downTime = EditorApplication.timeSinceStartup;
                s_pivot = view.pivot;
                s_rot = view.rotation;
                s_size = view.size;
                return;
            }
            if (e.rawType != EventType.MouseUp || !s_armed)
                return;
            s_armed = false;
            bool still = (e.mousePosition - s_downPos).sqrMagnitude <= ClickTolerance * ClickTolerance
                && EditorApplication.timeSinceStartup - s_downTime <= ClickTime
                && (view.pivot - s_pivot).sqrMagnitude < 1e-8f
                && Quaternion.Angle(view.rotation, s_rot) < 0.01f
                && Mathf.Abs(view.size - s_size) <= Mathf.Abs(s_size) * 1e-4f;
            if (!still || !AnyControlSelected(s))
                return;
            GenericMenu menu = s_alt ? AltMenu(s) : MainMenu(s);
            menu.ShowAsContext();
        }

        /// <summary>只选中了控制器时，删除 = 删当前帧关键帧，复制/粘贴 = 姿势，复制物体/剪切被拦下，避免误删或复制出控制器。</summary>
        public static void HandleCommands(AsiKeySession s, Event e)
        {
            if (e.type != EventType.ValidateCommand && e.type != EventType.ExecuteCommand)
                return;
            if (!OnlyControlsSelected(s))
                return;
            bool exec = e.type == EventType.ExecuteCommand;
            switch (e.commandName)
            {
                case "SoftDelete":
                case "Delete":
                    if (exec) s.DeleteKeysAtFrame();
                    break;
                case "Copy":
                    if (exec) s.CopyPose();
                    break;
                case "Paste":
                    if (exec && AsiKeySession.Clipboard != null) s.PastePose(false);
                    break;
                case "Duplicate":
                case "Cut":
                    if (exec) s.status = "K帧控制器不能复制或剪切";
                    break;
                default:
                    return;
            }
            e.Use();
            if (exec)
                AsiKeySession.RaiseRepaint();
        }

        private static bool AnyControlSelected(AsiKeySession s)
        {
            Transform[] sel = Selection.GetTransforms(SelectionMode.Unfiltered);
            for (int k = 0; k < sel.Length; k++)
            {
                if (s.ControlIndex(sel[k]) >= 0)
                    return true;
            }
            return false;
        }

        private static bool OnlyControlsSelected(AsiKeySession s)
        {
            GameObject[] objs = Selection.gameObjects;
            if (objs.Length == 0)
                return false;
            for (int k = 0; k < objs.Length; k++)
            {
                if (s.ControlIndex(objs[k].transform) < 0)
                    return false;
            }
            return true;
        }

        private static string WithKey(string text, string shortcutId)
        {
            return text + "    " + AsiKeyShortcuts.MenuBinding(shortcutId);
        }

        /// <summary>菜单回调执行时会话可能已经换了，只对仍然有效的同一会话生效。</summary>
        private static GenericMenu.MenuFunction Run(AsiKeySession s, GenericMenu.MenuFunction fn)
        {
            return () =>
            {
                if (AsiKeySession.Current == s && s.Valid)
                    fn();
            };
        }

        private static void Add(GenericMenu menu, string text, bool enabled, AsiKeySession s, GenericMenu.MenuFunction fn, bool on = false)
        {
            AsiKeyPanels.AddMenu(menu, text, enabled, Run(s, fn), on);
        }

        // ---------------- 右键菜单 ----------------

        private static GenericMenu MainMenu(AsiKeySession s)
        {
            GenericMenu m = new GenericMenu();
            AsiKeyProject p = s.project;
            List<int> sel = s.SelectedBones();
            List<int> chains = s.SelectedChains();

            Add(m, WithKey("注册关键帧", AsiKeyShortcuts.SetKey), true, s, () => s.SetKey(AsiKeyType.Set));
            Add(m, "固定关键帧", true, s, () => s.SetKey(AsiKeyType.Planted));
            Add(m, "滑动关键帧", true, s, () => s.SetKey(AsiKeyType.Sliding));
            Add(m, "自由关键帧", true, s, () => s.SetKey(AsiKeyType.Free));
            Add(m, "删除当前帧关键帧    Delete", true, s, s.DeleteKeysAtFrame);
            m.AddSeparator("");
            for (int i = 0; i < AsiKeySession.ConstraintKindNames.Length; i++)
            {
                AsiConstraintKind kind = (AsiConstraintKind)i;
                Add(m, "添加约束/" + AsiKeySession.ConstraintKindNames[i], sel.Count > 0, s, () => AsiKeyPanels.AddConstraintFromSelection(s, kind));
            }
            m.AddSeparator("添加约束/");
            Add(m, "添加约束/打开约束面板", true, s, () => AsiKeyPanels.SetShown(AsiKeyPanels.Constraints, true));
            Add(m, WithKey("自动关键点", AsiKeyShortcuts.AutoKey), true, s, () => AsiKeyPanels.SetAutoKey(s, !s.autoKey), s.autoKey);

            bool hasCom = false;
            for (int k = 0; k < sel.Count; k++)
                hasCom |= p.bones[sel[k]].isCom;
            if (hasCom)
            {
                Add(m, "质心轨道/水平", true, s, () => s.comKeyH = !s.comKeyH, s.comKeyH);
                Add(m, "质心轨道/垂直", true, s, () => s.comKeyV = !s.comKeyV, s.comKeyV);
                Add(m, "质心轨道/旋转", true, s, () => s.comKeyR = !s.comKeyR, s.comKeyR);
            }
            if (chains.Count > 0)
            {
                int c = chains[0];
                int[] values;
                string[] pivots = AsiKeyPanels.PivotNames(p, p.chains[c], out values);
                int cur = s.PivotChoice(c);
                for (int i = 0; i < pivots.Length; i++)
                {
                    int pivot = values[i];
                    Add(m, "IK 轴心（" + p.chains[c].name.Replace('/', '∕') + "）/" + pivots[i].Replace('/', '∕'), true, s, () => AsiKeyPanels.SetPivot(s, c, pivot), cur == pivot);
                }
            }
            m.AddSeparator("");
            AddPanelItems(m, "面板/");
            return m;
        }

        private static GenericMenu AltMenu(AsiKeySession s)
        {
            GenericMenu m = new GenericMenu();
            Add(m, "变换中心：轴心 Pivot", true, s, () => SetPivotMode(PivotMode.Pivot), Tools.pivotMode == PivotMode.Pivot);
            Add(m, "变换中心：中心 Center", true, s, () => SetPivotMode(PivotMode.Center), Tools.pivotMode == PivotMode.Center);
            Add(m, "K帧变换轴（Max 风格）", true, s, () => AsiKeyGizmo.Enabled = !AsiKeyGizmo.Enabled, AsiKeyGizmo.Enabled);
            m.AddSeparator("");
            bool clip = AsiKeySession.Clipboard != null;
            Add(m, "复制姿势    Ctrl+C", true, s, s.CopyPose);
            Add(m, "粘贴姿势（选中骨骼）    Ctrl+V", clip, s, () => s.PastePose(false));
            Add(m, "镜像粘贴", clip, s, () => s.PastePose(true));
            Add(m, "镜像当前姿势", true, s, s.MirrorCurrentPose);
            m.AddSeparator("");
            Add(m, "重置选中骨骼（复原到原始姿势）", true, s, s.ResetSelected);
            Add(m, "设为原始姿势（选中骨骼的当前姿态）", true, s, s.SetOriginalPose);
            m.AddSeparator("");
            Add(m, "姿势库窗口…", true, s, AsiKeyPoseWindow.Open);
            Add(m, "复制全身姿势到姿势库", true, s, () => AsiKeyPoseWindow.CaptureToLibrary(s, true));
            Add(m, "复制选中骨骼（局部姿势）到姿势库", true, s, () => AsiKeyPoseWindow.CaptureToLibrary(s, false));
            m.AddSeparator("");
            AddDisplayItems(m, s, "显示/");
            AddFilterItems(m, s, "选择筛选/");
            return m;
        }

        private static void SetPivotMode(PivotMode mode)
        {
            Tools.pivotMode = mode;
            SceneView.RepaintAll();
        }

        // ---------------- 共用菜单项（也给 Scene 工具栏下拉用） ----------------

        public static void AddDisplayItems(GenericMenu m, AsiKeySession s, string prefix)
        {
            AsiKeyProject p = s.project;
            Add(m, prefix + "透视（外形不被模型遮挡）", true, s, () => AsiKeyPanels.SetDisplay(s, !p.xray, null, null, null), p.xray);
            Add(m, prefix + "父子连线", true, s, () => AsiKeyPanels.SetDisplay(s, null, !p.showLinks, null, null), p.showLinks);
            Add(m, prefix + "运动轨迹", true, s, () => AsiKeyPanels.SetDisplay(s, null, null, !p.showTrajectory, null), p.showTrajectory);
            for (int i = 0; i < ShapeScales.Length; i++)
            {
                float v = ShapeScales[i];
                Add(m, prefix + "外形大小/× " + v.ToString("0.##"), true, s, () => AsiKeyPanels.SetDisplay(s, null, null, null, v), Mathf.Abs(p.shapeScale - v) < 0.01f);
            }
            for (int i = 0; i < AsiKeyLabels.BoneDisplay.Length; i++)
            {
                AsiBoneDisplay mode = (AsiBoneDisplay)i;
                Add(m, prefix + "骨骼样式/" + AsiKeyLabels.BoneDisplay[i], true, s, () => AsiKeyPanels.SetBoneDisplay(s, mode), p.boneDisplay == mode);
            }
            m.AddSeparator(prefix);
            AddHideItems(m, s, prefix);
            if (!p.AnyHumanoid)
                return;
            for (int part = 0; part < AsiKeyBipedMap.PartNames.Length; part++)
            {
                List<int> bones = AsiKeyPanels.PartBones(p, part);
                if (bones.Count == 0)
                    continue;
                int pt = part;
                bool shown = AsiKeyPanels.PartShown(p, bones);
                Add(m, prefix + "按部位显示/" + AsiKeyBipedMap.PartNames[part], true, s, () => AsiKeyPanels.SetPartVisible(s, pt, !shown), shown);
            }
        }

        private static void AddHideItems(GenericMenu m, AsiKeySession s, string prefix)
        {
            bool any = s.SelectedBones().Count > 0;
            int hidden = AsiKeyPanels.HiddenBoneCount(s.project);
            Add(m, prefix + "隐藏选中骨骼", any, s, () => AsiKeyPanels.HideSelected(s));
            Add(m, prefix + "隐藏未选中骨骼", any, s, () => AsiKeyPanels.HideUnselected(s));
            Add(m, prefix + "隐藏所有骨骼", hidden < s.project.bones.Count, s, () => AsiKeyPanels.HideAllBones(s));
            Add(m, prefix + (hidden > 0 ? "全部显示（" + hidden + " 根已隐藏）" : "全部显示"), hidden > 0, s, () => AsiKeyPanels.ShowAllBones(s));
        }

        /// <summary>选择筛选的几种模式，以及各单位的可选 / 可显开关。</summary>
        public static void AddFilterItems(GenericMenu m, AsiKeySession s, string prefix)
        {
            AsiKeyProject p = s.project;
            AsiSelectFilter f = AsiKeyRigDrawer.Filter;
            Add(m, prefix + "不筛选（Unity 默认点选）", true, s, () => AsiKeyRigDrawer.Filter = AsiSelectFilter.None, f == AsiSelectFilter.None);
            Add(m, prefix + "仅选骨骼（角色单位的控制器）", true, s, () => AsiKeyRigDrawer.Filter = AsiSelectFilter.Bones, f == AsiSelectFilter.Bones);
            Add(m, prefix + "仅选工程内单位（含镜头、灯光、物件）", true, s, () => AsiKeyRigDrawer.Filter = AsiSelectFilter.Units, f == AsiSelectFilter.Units);
            bool anyUnit = false;
            for (int u = 0; u < p.units.Count; u++)
            {
                int unit = u;
                anyUnit = true;
                Add(m, prefix + "仅选择某单位的骨骼/" + UnitMenuName(p, u), true, s, () => AsiKeyRigDrawer.FilterOneUnit(p, unit),
                    f == AsiSelectFilter.OneUnit && p.selectFilterUnit == u);
            }
            if (!anyUnit)
                Add(m, prefix + "仅选择某单位的骨骼/（没有单位）", false, s, null);
            Add(m, prefix + "自定义可选单位", true, s, () => AsiKeyRigDrawer.Filter = AsiSelectFilter.Custom, f == AsiSelectFilter.Custom);
            m.AddSeparator(prefix);
            Add(m, prefix + "全部可选", p.units.Count > 0, s, () => AsiKeyPanels.SetUnitFlags(s, -1, true, null));
            Add(m, prefix + "全部可显", p.units.Count > 0, s, () => AsiKeyPanels.SetUnitFlags(s, -1, null, true));
            for (int u = 0; u < p.units.Count; u++)
            {
                int unit = u;
                AsiKeyUnit un = p.units[u];
                Add(m, prefix + "单位可选/" + UnitMenuName(p, u), true, s, () => AsiKeyPanels.SetUnitFlags(s, unit, !un.selectable, null), un.selectable);
                Add(m, prefix + "单位可显/" + UnitMenuName(p, u), true, s, () => AsiKeyPanels.SetUnitFlags(s, unit, null, !un.visible), un.visible);
            }
        }

        private static string UnitMenuName(AsiKeyProject p, int u)
        {
            AsiKeyUnit un = p.units[u];
            string kind = un.IsCharacter ? "角色" : "物体";
            return (u + 1) + ". " + (string.IsNullOrEmpty(un.name) ? "单位" : un.name).Replace('/', '∕') + "（" + kind + "）";
        }

        public static void AddPanelItems(GenericMenu m, string prefix)
        {
            for (int i = 0; i < AsiKeyPanels.All.Length; i++)
            {
                string panel = AsiKeyPanels.All[i];
                string text = AsiKeyPanels.Titles[i];
                if (panel == AsiKeyPanels.Shortcuts)
                    text += "    " + AsiKeyShortcuts.MenuBinding(AsiKeyShortcuts.Help);
                m.AddItem(new GUIContent(prefix + text), AsiKeyPanels.IsShown(panel), () => AsiKeyPanels.Toggle(panel));
            }
            m.AddSeparator(prefix);
            if (AsiKeyTimelineOverlay.IsInToolbar(SceneView.lastActiveSceneView))
            {
                m.AddDisabledItem(new GUIContent(prefix + "时间轴贴底铺满（已停靠在工具栏区）"));
                return;
            }
            m.AddItem(new GUIContent(prefix + "时间轴贴底铺满"), false, () =>
            {
                AsiKeyPanels.SetShown(AsiKeyPanels.Timeline, true);
                AsiKeyPanels.TimelineDocked = true;
                AsiKeyPanels.RequestTimelineFit();
            });
        }

    }
}
