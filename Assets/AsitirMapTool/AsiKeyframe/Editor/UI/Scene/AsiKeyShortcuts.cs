using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// K帧快捷键：注册到快捷键管理器（Edit > Shortcuts…，分类「K帧工具」），可以改键。
    /// 只在 Scene 视图获得焦点且 K帧会话运行时生效（带 Tag，会话启动时注册 Tag），这时优先于同键位的其它快捷键。
    /// </summary>
    public static class AsiKeyShortcuts
    {
        public const string Tag = "AsiKey.Session";
        private const string Prefix = "K帧工具/";

        public const string SetKey = Prefix + "注册关键帧";
        public const string AutoKey = Prefix + "自动关键点";
        public const string PrevKey = Prefix + "上一关键帧";
        public const string NextKey = Prefix + "下一关键帧";
        public const string PrevFrame = Prefix + "上一帧";
        public const string NextFrame = Prefix + "下一帧";
        public const string Play = Prefix + "播放暂停";
        public const string FirstFrame = Prefix + "跳到起始帧";
        public const string LastFrame = Prefix + "跳到结束帧";
        public const string SelectParent = Prefix + "选择父级";
        public const string SelectChild = Prefix + "选择子级";
        public const string Help = Prefix + "快捷键面板";

        public static readonly string[] Ids =
        {
            SetKey, AutoKey, PrevKey, NextKey, PrevFrame, NextFrame, Play, FirstFrame, LastFrame, SelectParent, SelectChild, Help
        };

        public static readonly string[] Labels =
        {
            "注册关键帧", "自动关键点开关", "上一关键帧（选中骨骼的）", "下一关键帧（选中骨骼的）", "上一帧", "下一帧",
            "播放 / 暂停", "跳到起始帧", "跳到结束帧", "选择父级", "选择子级", "显示 / 隐藏本面板"
        };

        private static readonly string[] MouseHelp =
        {
            "右键单击", "选中控制器时：打关键帧、删除当前帧、添加约束、IK 轴心、面板开关",
            "Alt+右键单击", "变换中心、变换轴开关、复制粘贴姿势、重置选中骨骼 / 设为原始姿势、姿势库、显示（骨骼样式、显隐骨骼）、选择筛选",
            "Ctrl+Alt+中键拖动", "Scene 视图缩放（K帧自动把 Unity 的 Alt+右键缩放改到这里，菜单 AsitirTool 里可恢复）",
            "X", "参考坐标在局部和世界之间切换（Unity 的 Local / Global，移动和旋转共用）",
            "选择筛选", "工具栏下拉：仅选骨骼（角色单位）、仅选工程内单位（含镜头灯光物件）、仅选某单位、自定义可选单位；开启后点空白处清空选择，Shift 加选、Ctrl 减选",
            "拾取约束目标", "添加约束后左键点控制器或场景物体作为目标，Shift 连续拾取，Esc 或右键退出",
            "双击控制器", "选中它和全部子级（层级选择，之后 Shift+单击加选/减选）：K帧变换轴旋转时每根骨骼绕自己局部的同一根相对轴一起转，移动只移动双击的那根",
            "选择子级", "Shift+S，多选时选中每根已选骨骼的全部子级。指尖轴心不显示、点不中，从末节也能选到。只选中它时旋转让整根手指 IK 绕指尖转；末节也在选择里时按普通方式转",
            "旋转轴", "X/Y/Z 环沿切线拖动（0.5°/像素）；环内拖动自由旋转，单击穿透选中骨骼；最外圈绕视线转；Ctrl 吸附；Esc 或右键取消",
            "移动轴", "拖轴、平面方块或中心方块（视图平面）；Ctrl 吸附；Esc 或右键取消。移动大腿/小腿/脚趾等价于移动脚，大臂/小臂/手指等价于移动手（Unity 移动工具同样）",
            "小腿 / 小臂", "局部坐标只有 X 和 Z：X 是特殊旋转（末端不动，膝/肘绕「根→末端」转，IK 时改 swivel），Z 是弯曲",
            "固定 / 滑动的脚", "默认绕脚趾根转（变换轴仍在脚上，右键「IK 轴心」可改）；转脚时脚趾保持世界朝向，打脚的关键帧时脚趾一起打",
            "滑动段的肢体", "转大腿/小腿/大臂像 FK 一样带着末端走，IK 目标跟到新位置；转小臂时手掌位置跟着腕关节，朝向保持不变（和转小腿时的脚掌一样）。关键帧仍是滑动。固定段转它们只改膝/肘朝向，末端不动",
            "Delete", "删除选中骨骼当前帧的关键帧（Scene 里选中控制器时）",
            "Ctrl+C / Ctrl+V", "复制姿势 / 粘贴到选中骨骼（Scene 里选中控制器时）",
            "时间滑块", "拖动滑块或点击轨道跳帧，两端 < > 逐帧",
            "轨道条 左键", "点色块选中该帧的关键帧（Ctrl 切换、Shift 加选，双击跳到该帧），拖动移动，按住 Shift 拖动复制；空白处拖动框选",
            "选择范围条", "选中的关键帧跨多个时间时出现在轨道条下方：拖两端缩放关键帧间距（另一端不动），拖中间整体移动，按住 Shift 复制",
            "时间轴 Ctrl+Alt+拖动", "左键改起始帧、右键改结束帧、中键平移整个时间范围（长度不变）；像抓住时间轴拖，向右拖帧号变小。整次拖动一步撤销",
            "轨道条 色块颜色", "同一帧的通道从上到下叠放：品红 质心水平、青 质心垂直、绿 质心旋转；灰 注册、橙 固定、黄 滑动、蓝 自由；紫 约束影响度",
            "时间轴 右键", "关键帧菜单（类型、切线、TCB、分轨道视图）",
            "时间轴 Delete / Ctrl+C / Ctrl+V / Ctrl+A", "点过时间轴后：删除 / 复制 / 粘贴 / 全选关键帧",
            "轨道条左端 ▲", "展开分轨道视图：滚轮缩放、Shift+滚轮滚动轨道、中键或 Alt+左键平移、F 全部显示、顶边调高度"
        };

        private static bool s_tag;
        private static bool s_hooked;
        private static readonly Dictionary<string, string> s_bindings = new Dictionary<string, string>();
        private static GUIStyle s_key;

        /// <summary>会话启动/结束时切换 Tag。</summary>
        public static void SetActive(bool on)
        {
            if (s_tag == on)
                return;
            s_tag = on;
            if (on)
                ShortcutManager.RegisterTag(Tag);
            else
                ShortcutManager.UnregisterTag(Tag);
        }

        /// <summary>当前绑定的按键文字，没有绑定时返回「未绑定」。</summary>
        public static string Binding(string id)
        {
            if (!s_hooked)
            {
                s_hooked = true;
                ShortcutManager.instance.shortcutBindingChanged += e => InvalidateBindings();
                ShortcutManager.instance.activeProfileChanged += e => InvalidateBindings();
            }
            string b;
            if (s_bindings.TryGetValue(id, out b))
                return b;
            try
            {
                b = ShortcutManager.instance.GetShortcutBinding(id).ToString();
            }
            catch (ArgumentException)
            {
                b = "";
            }
            if (string.IsNullOrEmpty(b))
                b = "未绑定";
            s_bindings[id] = b;
            return b;
        }

        /// <summary>菜单文字里「/」会被当成子菜单分隔符，换成形近字符。</summary>
        public static string MenuBinding(string id)
        {
            return Binding(id).Replace('/', '∕');
        }

        private static void InvalidateBindings()
        {
            s_bindings.Clear();
            AsiKeySession.RaiseRepaint();
        }

        // ---------------- 快捷键 ----------------

        private static AsiKeySession Session
        {
            get { return AsiKeyPanels.Active; }
        }

        [Shortcut(SetKey, typeof(SceneView), Tag, KeyCode.K)]
        private static void DoSetKey()
        {
            AsiKeySession s = Session;
            if (s != null)
                s.SetKey(AsiKeyType.Set);
        }

        [Shortcut(AutoKey, typeof(SceneView), Tag, KeyCode.N)]
        private static void DoAutoKey()
        {
            AsiKeySession s = Session;
            if (s != null)
                AsiKeyPanels.SetAutoKey(s, !s.autoKey);
        }

        [Shortcut(PrevKey, typeof(SceneView), Tag, KeyCode.Z, ShortcutModifiers.Shift)]
        private static void DoPrevKey()
        {
            AsiKeySession s = Session;
            if (s != null)
                s.JumpKey(-1);
        }

        [Shortcut(NextKey, typeof(SceneView), Tag, KeyCode.X, ShortcutModifiers.Shift)]
        private static void DoNextKey()
        {
            AsiKeySession s = Session;
            if (s != null)
                s.JumpKey(1);
        }

        [Shortcut(PrevFrame, typeof(SceneView), Tag, KeyCode.Comma)]
        private static void DoPrevFrame()
        {
            AsiKeySession s = Session;
            if (s != null)
                s.GoTo(Mathf.Round(s.frame) - 1f);
        }

        [Shortcut(NextFrame, typeof(SceneView), Tag, KeyCode.Period)]
        private static void DoNextFrame()
        {
            AsiKeySession s = Session;
            if (s != null)
                s.GoTo(Mathf.Round(s.frame) + 1f);
        }

        [Shortcut(Play, typeof(SceneView), Tag, KeyCode.C, ShortcutModifiers.Shift)]
        private static void DoPlay()
        {
            AsiKeySession s = Session;
            if (s != null)
                s.TogglePlay();
        }

        [Shortcut(FirstFrame, typeof(SceneView), Tag, KeyCode.Home)]
        private static void DoFirstFrame()
        {
            AsiKeySession s = Session;
            if (s != null)
                s.GoTo(s.project.startFrame);
        }

        [Shortcut(LastFrame, typeof(SceneView), Tag, KeyCode.End)]
        private static void DoLastFrame()
        {
            AsiKeySession s = Session;
            if (s != null)
                s.GoTo(s.project.endFrame);
        }

        [Shortcut(SelectParent, typeof(SceneView), Tag, KeyCode.A, ShortcutModifiers.Shift)]
        private static void DoSelectParent()
        {
            AsiKeySession s = Session;
            if (s != null)
                SelectRelative(s, true);
        }

        [Shortcut(SelectChild, typeof(SceneView), Tag, KeyCode.S, ShortcutModifiers.Shift)]
        private static void DoSelectChild()
        {
            AsiKeySession s = Session;
            if (s != null)
                SelectRelative(s, false);
        }

        [Shortcut(Help, typeof(SceneView), Tag, KeyCode.Slash, ShortcutModifiers.Shift)]
        private static void DoHelp()
        {
            if (Session != null)
                AsiKeyPanels.Toggle(AsiKeyPanels.Shortcuts);
        }

        // ---------------- 父级 / 子级 ----------------

        /// <summary>把选择换成每根选中骨骼的父级，或全部子级（可显示的，以及指尖轴心）。</summary>
        public static void SelectRelative(AsiKeySession s, bool parent)
        {
            List<int> sel = s.SelectedBones();
            if (sel.Count == 0)
            {
                s.status = "先选择控制器";
                AsiKeySession.RaiseRepaint();
                return;
            }
            List<int> result = new List<int>();
            for (int k = 0; k < sel.Count; k++)
            {
                if (parent)
                {
                    int j = VisibleParent(s, sel[k]);
                    if (j >= 0 && !result.Contains(j))
                        result.Add(j);
                }
                else
                    CollectChildren(s, sel[k], result);
            }
            if (result.Count == 0)
            {
                s.status = parent ? "已经是最上层的控制器" : "没有子级控制器";
                AsiKeySession.RaiseRepaint();
                return;
            }
            s.SelectBones(result, false);
        }

        private static int VisibleParent(AsiKeySession s, int i)
        {
            int p = s.project.LinkParent(i);
            while (p >= 0 && !AsiKeyRigDrawer.Pickable(s, p))
                p = s.project.LinkParent(p);
            return p;
        }

        /// <summary>bone 的下一层可选择子级：直接子级里能点中的和指尖轴心都要；这一层没有时再往隐藏骨骼下面找。</summary>
        private static void CollectChildren(AsiKeySession s, int bone, List<int> result)
        {
            List<AsiKeyBone> bones = s.project.bones;
            List<int> layer = new List<int> { bone };
            while (layer.Count > 0)
            {
                List<int> found = new List<int>();
                List<int> deeper = new List<int>();
                for (int h = 0; h < layer.Count; h++)
                {
                    int head = layer[h];
                    for (int j = 0; j < bones.Count; j++)
                    {
                        if (s.project.LinkParent(j) != head)
                            continue;
                        if (AsiKeyRigDrawer.Pickable(s, j) || bones[j].fingerTip)
                            found.Add(j);
                        else
                            deeper.Add(j);
                    }
                }
                if (found.Count > 0)
                {
                    for (int k = 0; k < found.Count; k++)
                    {
                        if (!result.Contains(found[k]))
                            result.Add(found[k]);
                    }
                    return;
                }
                layer = deeper;
            }
        }

        // ---------------- 快捷键面板 ----------------

        public static void DrawHelp()
        {
            if (s_key == null)
            {
                s_key = new GUIStyle(EditorStyles.miniBoldLabel);
                s_key.alignment = TextAnchor.MiddleRight;
            }
            GUILayout.Label("快捷键（Scene 视图获得焦点时）", EditorStyles.miniBoldLabel);
            for (int i = 0; i < Ids.Length; i++)
                Row(Labels[i], Binding(Ids[i]));
            EditorGUILayout.Space(4f);
            GUILayout.Label("鼠标与编辑命令", EditorStyles.miniBoldLabel);
            for (int i = 0; i + 1 < MouseHelp.Length; i += 2)
            {
                GUILayout.Label(MouseHelp[i], EditorStyles.miniBoldLabel);
                GUILayout.Label(MouseHelp[i + 1], EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.Space(4f);
            if (GUILayout.Button("修改快捷键…（Edit > Shortcuts，分类「K帧工具」）", EditorStyles.miniButton))
                EditorApplication.ExecuteMenuItem("Edit/Shortcuts...");
        }

        private static void Row(string label, string key)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(key, s_key);
            EditorGUILayout.EndHorizontal();
        }
    }
}
