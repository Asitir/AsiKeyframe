using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// Scene 视图里 K帧面板（时间轴、Motion、Key Info、层）的共享状态与绘制。
    /// 同一时间只有一个会话，也只有当前激活的 Scene 视图显示这些面板，所以状态是静态的。
    /// </summary>
    public static partial class AsiKeyPanels
    {
        public const string Timeline = "Timeline";
        public const string Motion = "Motion";
        public const string KeyInfo = "KeyInfo";
        public const string Layers = "Layers";
        public const string Shortcuts = "Shortcuts";
        public const string Units = "Units";
        public const string Constraints = "Constraints";

        public static readonly string[] All = { Timeline, Motion, KeyInfo, Layers, Shortcuts, Units, Constraints };
        public static readonly string[] Titles = { "时间轴", "Motion", "Key Info", "层", "快捷键", "单位", "约束" };

        private const string PrefPrefix = "AsiKey.Panel.";
        private static readonly Dictionary<string, bool> s_shown = new Dictionary<string, bool>();

        private static readonly HashSet<AsiKey> _selKeys = new HashSet<AsiKey>();
        private static readonly HashSet<AsiLinkEntry> _selLinks = new HashSet<AsiLinkEntry>();
        private static AsiKeyProject _project;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            Undo.undoRedoPerformed += ClearKeySelection;
        }

        /// <summary>有效的会话，没有时为 null。</summary>
        public static AsiKeySession Active
        {
            get
            {
                AsiKeySession s = AsiKeySession.Current;
                return s != null && s.Valid ? s : null;
            }
        }

        public static bool IsShown(string panel)
        {
            bool on;
            if (!s_shown.TryGetValue(panel, out on))
            {
                on = EditorPrefs.GetBool(PrefPrefix + panel, panel != Layers && panel != Shortcuts && panel != Units && panel != Constraints);
                s_shown[panel] = on;
            }
            return on;
        }

        public static void SetShown(string panel, bool on)
        {
            if (IsShown(panel) == on)
                return;
            s_shown[panel] = on;
            EditorPrefs.SetBool(PrefPrefix + panel, on);
            SceneView.RepaintAll();
            AsiKeySession.RaiseRepaint();
        }

        public static void Toggle(string panel)
        {
            SetShown(panel, !IsShown(panel));
        }

        public static void ClearKeySelection()
        {
            _selKeys.Clear();
            _selLinks.Clear();
            AsiKeySession.RaiseRepaint();
        }

        public static int SelectedKeyCount
        {
            get { return _selKeys.Count; }
        }

        private static bool Begin(AsiKeySession s)
        {
            _project = s != null && s.Valid ? s.project : null;
            return _project != null;
        }

        private static void Repaint()
        {
            AsiKeySession.RaiseRepaint();
        }

        private static void Mutate(string undo, Action fn)
        {
            Undo.RecordObject(_project, undo);
            fn();
            _project.Touch();
            EditorUtility.SetDirty(_project);
            AsiKeySession s = AsiKeySession.Current;
            if (s != null && s.project == _project)
                s.Refresh();
        }

        private static bool ColoredButton(string text, Color color, params GUILayoutOption[] options)
        {
            Color bg = GUI.backgroundColor;
            GUI.backgroundColor = color;
            bool r = GUILayout.Button(text, options);
            GUI.backgroundColor = bg;
            return r;
        }

        public static void AddMenu(GenericMenu menu, string text, bool enabled, GenericMenu.MenuFunction fn, bool on = false)
        {
            if (enabled)
                menu.AddItem(new GUIContent(text), on, fn);
            else
                menu.AddDisabledItem(new GUIContent(text), on);
        }

        // ---------------- 供菜单与快捷键调用 ----------------

        /// <summary>IK 轴心的选项名与对应的轴心下标。脚链最前面多一个「脚趾根」（<see cref="AsiKeyChain.ChildPivot"/>，默认）。</summary>
        public static string[] PivotNames(AsiKeyProject p, AsiKeyChain chain, out int[] values)
        {
            int n = Mathf.Max(1, chain.pivots != null ? chain.pivots.Length : 1);
            bool named = chain.pivotNames != null && chain.pivotNames.Length == n;
            int extra = p.UsesChildPivot(chain) ? 1 : 0;
            string[] names = new string[n + extra];
            values = new int[n + extra];
            if (extra > 0)
            {
                names[0] = "脚趾根（默认）";
                values[0] = AsiKeyChain.ChildPivot;
            }
            for (int i = 0; i < n; i++)
            {
                names[i + extra] = named ? chain.pivotNames[i] : i == 0 ? "末端原点" : "轴心 " + i;
                values[i + extra] = i;
            }
            return names;
        }

        /// <summary>轴心下标在 <see cref="PivotNames"/> 选项里的位置，找不到时为 0。</summary>
        public static int PivotOption(int[] values, int pivot)
        {
            int k = System.Array.IndexOf(values, pivot);
            return k >= 0 ? k : 0;
        }

        /// <summary>设定 IK 链的轴心；当前帧有该链的关键帧时一并改写。</summary>
        public static void SetPivot(AsiKeySession s, int c, int pivot)
        {
            if (!Begin(s))
                return;
            s.SetPivotChoice(c, pivot);
            AsiKeyTrack tr = _project.FindTrack(0, AsiTrackKind.Chain, c);
            int idx = tr != null ? AsiKeyTrackEval.FindKeyIndex(tr.keys, s.frame) : -1;
            if (idx >= 0)
            {
                AsiKey key = tr.keys[idx];
                Mutate("修改 IK 轴心", () =>
                {
                    key.pivot = pivot;
                    AsiKeyEdit.AfterEdit(_project, tr, 0, key);
                });
            }
            SceneView.RepaintAll();
            Repaint();
        }

        public static void SetDisplay(AsiKeySession s, bool? xray, bool? links, bool? trajectory, float? scale)
        {
            if (!Begin(s))
                return;
            Undo.RecordObject(_project, "K帧显示设置");
            if (xray.HasValue) _project.xray = xray.Value;
            if (links.HasValue) _project.showLinks = links.Value;
            if (trajectory.HasValue) _project.showTrajectory = trajectory.Value;
            if (scale.HasValue) _project.shapeScale = Mathf.Clamp(scale.Value, 0.2f, 3f);
            EditorUtility.SetDirty(_project);
            SceneView.RepaintAll();
            Repaint();
        }

        public static void SetBoneDisplay(AsiKeySession s, AsiBoneDisplay mode)
        {
            if (!Begin(s) || _project.boneDisplay == mode)
                return;
            Undo.RecordObject(_project, "K帧骨骼样式");
            _project.boneDisplay = mode;
            EditorUtility.SetDirty(_project);
            SceneView.RepaintAll();
            Repaint();
        }

        /// <summary>外形「显示」关掉的骨骼数，不含外形种类本身为「隐藏」的。</summary>
        public static int HiddenBoneCount(AsiKeyProject p)
        {
            int n = 0;
            for (int i = 0; i < p.bones.Count; i++)
            {
                if (!p.bones[i].shape.visible)
                    n++;
            }
            return n;
        }

        public static void HideSelected(AsiKeySession s)
        {
            if (!Begin(s))
                return;
            List<int> bones = s.SelectedBones();
            if (bones.Count == 0)
            {
                s.status = "先选择控制器";
                Repaint();
                return;
            }
            SetBonesVisible(s, bones, false, "隐藏选中骨骼");
        }

        public static void HideUnselected(AsiKeySession s)
        {
            if (!Begin(s))
                return;
            List<int> bones = new List<int>();
            for (int i = 0; i < _project.bones.Count; i++)
            {
                if (!s.IsSelected(i))
                    bones.Add(i);
            }
            SetBonesVisible(s, bones, false, "隐藏未选中骨骼");
        }

        public static void ShowAllBones(AsiKeySession s)
        {
            if (!Begin(s))
                return;
            List<int> bones = new List<int>();
            for (int i = 0; i < _project.bones.Count; i++)
                bones.Add(i);
            SetBonesVisible(s, bones, true, "全部显示骨骼");
        }

        public static void HideAllBones(AsiKeySession s)
        {
            if (!Begin(s))
                return;
            List<int> bones = new List<int>();
            for (int i = 0; i < _project.bones.Count; i++)
                bones.Add(i);
            SetBonesVisible(s, bones, false, "隐藏所有骨骼");
        }

        // ---------------- 单位的可选 / 可显 ----------------

        /// <summary>改单位的可选、可显开关（null 表示不改）；unit 为 -1 时改全部单位。不可显的单位的控制器同时从选择里去掉。</summary>
        public static void SetUnitFlags(AsiKeySession s, int unit, bool? selectable, bool? visible)
        {
            if (!Begin(s))
                return;
            Undo.RecordObject(_project, "单位可选/可显");
            for (int u = 0; u < _project.units.Count; u++)
            {
                if (unit >= 0 && u != unit)
                    continue;
                if (selectable.HasValue) _project.units[u].selectable = selectable.Value;
                if (visible.HasValue) _project.units[u].visible = visible.Value;
            }
            EditorUtility.SetDirty(_project);
            DropUnselectable(s);
            SceneView.RepaintAll();
            Repaint();
        }

        /// <summary>选择里去掉看不见或不能选的控制器。</summary>
        public static void DropUnselectable(AsiKeySession s)
        {
            List<UnityEngine.Object> keep = new List<UnityEngine.Object>();
            UnityEngine.Object[] sel = Selection.objects;
            for (int k = 0; k < sel.Length; k++)
            {
                GameObject go = sel[k] as GameObject;
                int i = go != null ? s.ControlIndex(go.transform) : -1;
                if (i < 0 || AsiKeyRigDrawer.Pickable(s, i) && (AsiKeyRigDrawer.Filter == AsiSelectFilter.None || AsiKeyRigDrawer.PassesFilter(s.project, i)))
                    keep.Add(sel[k]);
            }
            if (keep.Count != sel.Length)
                Selection.objects = keep.ToArray();
        }

        /// <summary>部位（<see cref="AsiKeyBipedMap.PartNames"/> 下标）里的骨骼。</summary>
        public static List<int> PartBones(AsiKeyProject p, int part)
        {
            List<int> list = new List<int>();
            for (int i = 0; i < p.bones.Count; i++)
            {
                if (AsiKeyBipedMap.PartOf(p.bones[i]) == part)
                    list.Add(i);
            }
            return list;
        }

        public static bool PartShown(AsiKeyProject p, List<int> bones)
        {
            for (int k = 0; k < bones.Count; k++)
            {
                if (!p.bones[bones[k]].shape.visible)
                    return false;
            }
            return true;
        }

        public static void SetPartVisible(AsiKeySession s, int part, bool visible)
        {
            if (!Begin(s))
                return;
            SetBonesVisible(s, PartBones(_project, part), visible,
                (visible ? "显示" : "隐藏") + AsiKeyBipedMap.PartNames[part]);
        }

        /// <summary>改外形的「显示」开关（跟工程保存）。隐藏的骨骼同时从选择里去掉。</summary>
        private static void SetBonesVisible(AsiKeySession s, List<int> bones, bool visible, string undo)
        {
            int changed = 0;
            for (int k = 0; k < bones.Count; k++)
            {
                if (_project.bones[bones[k]].shape.visible != visible)
                    changed++;
            }
            if (changed == 0)
            {
                s.status = visible ? "没有隐藏的骨骼" : "这些骨骼已经隐藏";
                Repaint();
                return;
            }
            Undo.RecordObject(_project, undo);
            for (int k = 0; k < bones.Count; k++)
                _project.bones[bones[k]].shape.visible = visible;
            EditorUtility.SetDirty(_project);
            if (!visible)
            {
                List<UnityEngine.Object> keep = new List<UnityEngine.Object>();
                UnityEngine.Object[] sel = Selection.objects;
                for (int k = 0; k < sel.Length; k++)
                {
                    GameObject go = sel[k] as GameObject;
                    int i = go != null ? s.ControlIndex(go.transform) : -1;
                    if (i < 0 || _project.bones[i].shape.visible)
                        keep.Add(sel[k]);
                }
                if (keep.Count != sel.Length)
                    Selection.objects = keep.ToArray();
            }
            s.status = (visible ? "显示了 " : "隐藏了 ") + changed + " 根骨骼";
            SceneView.RepaintAll();
            Repaint();
        }

        public static void SetAutoKey(AsiKeySession s, bool on)
        {
            if (s == null || s.autoKey == on)
                return;
            s.autoKey = on;
            SceneView.RepaintAll();
            Repaint();
        }
    }
}
