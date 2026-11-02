using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>左侧页签：骨骼层级与增删、IK 链、控制器外形与预设。</summary>
    public partial class AsiKeyWindow
    {
        private string _newChainName = "";
        private int _editChain = -1;
        [SerializeField] private AsiKeyShapePreset _preset;
        private readonly List<int> _dfs = new List<int>();
        private readonly List<int> _dfsDepth = new List<int>();

        // ---------------- 骨骼 ----------------

        /// <summary>骨骼 / IK 链 / 外形页签当前查看的单位。选中的控制器都属于同一单位时自动切过去。</summary>
        private int UnitPicker(AsiKeySession s, List<int> sel)
        {
            int n = _project.units.Count;
            if (n == 0)
                return 0;
            if (sel != null && sel.Count > 0)
            {
                int u0 = _project.UnitOf(sel[0]);
                bool same = true;
                for (int k = 1; k < sel.Count && same; k++)
                    same = _project.UnitOf(sel[k]) == u0;
                if (same && u0 >= 0)
                    _unit = u0;
            }
            _unit = Mathf.Clamp(_unit, 0, n - 1);
            if (n > 1)
            {
                string[] names = new string[n];
                for (int u = 0; u < n; u++)
                    names[u] = _project.units[u].name + "（" + AsiKeyLabels.UnitKind[(int)_project.units[u].kind] + "）";
                _unit = EditorGUILayout.Popup("单位", _unit, names);
            }
            return _unit;
        }

        private void BuildDfs(int unit)
        {
            _dfs.Clear();
            _dfsDepth.Clear();
            int n = _project.bones.Count;
            List<int>[] children = new List<int>[n];
            List<int> roots = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (unit >= 0 && _project.bones[i].unit != unit)
                    continue;
                int parent = _project.bones[i].parent;
                if (parent < 0 || parent >= n)
                {
                    roots.Add(i);
                    continue;
                }
                if (children[parent] == null)
                    children[parent] = new List<int>();
                children[parent].Add(i);
            }
            Stack<int> stack = new Stack<int>();
            Stack<int> depth = new Stack<int>();
            for (int r = roots.Count - 1; r >= 0; r--)
            {
                stack.Push(roots[r]);
                depth.Push(0);
            }
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int d = depth.Pop();
                _dfs.Add(i);
                _dfsDepth.Add(d);
                if (children[i] == null)
                    continue;
                for (int c = children[i].Count - 1; c >= 0; c--)
                {
                    stack.Push(children[i][c]);
                    depth.Push(d + 1);
                }
            }
        }

        private void DrawBonesTab(AsiKeySession s)
        {
            List<int> sel = s.SelectedBones();
            if (_project.units.Count == 0)
            {
                EditorGUILayout.HelpBox("工程里还没有单位，先在「单位」页签添加角色或物体。", MessageType.Info);
                return;
            }
            int unit = UnitPicker(s, sel);
            AsiUnitKind kind = _project.UnitKind(unit);
            EditorGUILayout.LabelField("骨骼（" + _project.UnitBones(unit).Count + "）", Header);
            EditorGUILayout.HelpBox(kind == AsiUnitKind.Humanoid
                ? "人形：按 Biped 结构只为人形骨骼生成控制器。可手动添加头发、裙摆等额外骨骼。"
                : kind == AsiUnitKind.Generic
                    ? "骨骼角色：为蒙皮骨骼生成控制器，可手动增删。"
                    : "物体：物体本身就是一根骨骼（记录位移），可以手动添加它的子物体。", MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("添加选中模型骨骼", "在 Hierarchy 里选中该单位内部的 Transform 后点击")))
                AddSelectedModelBones(s, unit);
            if (GUILayout.Button(new GUIContent("删除选中控制器", "删除选中控制器对应的骨骼定义，子骨骼挂到上一级")))
                RemoveSelectedBones(s);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("自动配对左右", "按骨骼名和静止姿势重新配对镜像骨骼（只在同一单位内配对）")))
                Mutate("自动配对", () => AsiKeyMirror.AutoPair(_project));
            if (GUILayout.Button(new GUIContent("重建该单位", "按目标重新生成该单位的骨骼与控制器，清空该单位的动画")))
            {
                if (EditorUtility.DisplayDialog("K帧工具", "重建会清空单位「" + _project.UnitName(unit) + "」的所有动画、IK 链、外形设置和以它为对象的约束。继续？", "重建", "取消"))
                {
                    AsiKeyPanels.ClearKeySelection();
                    string err = s.RebuildUnit(unit);
                    if (!string.IsNullOrEmpty(err))
                        s.status = err;
                }
            }
            EditorGUILayout.EndHorizontal();

            int com = _project.ComOf(unit);
            if (com >= 0 && _project.BoneByHuman(HumanBodyBones.Hips, unit) >= 0 && _project.BoneByHuman(HumanBodyBones.Spine, unit) >= 0)
            {
                EditorGUI.BeginChangeCheck();
                float ratio = EditorGUILayout.Slider(new GUIContent("质心位置", "0 = 盆骨，1 = 第一节腰椎"), _project.UnitComRatio(unit), 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                    Mutate("质心位置", () => AsiKeyRigBuilder.SetComRatio(_project, unit, ratio));
            }

            if (sel.Count == 1)
                DrawBoneDetails(s, sel[0]);
            else if (sel.Count > 1)
                EditorGUILayout.HelpBox("已选中 " + sel.Count + " 个控制器。", MessageType.None);

            EditorGUILayout.Space(4f);
            BuildDfs(unit);
            HashSet<int> selected = new HashSet<int>(sel);
            for (int k = 0; k < _dfs.Count; k++)
            {
                int i = _dfs[k];
                AsiKeyBone b = _project.bones[i];
                Rect row = GUILayoutUtility.GetRect(10f, 17f, GUILayout.ExpandWidth(true));
                if (selected.Contains(i))
                    EditorGUI.DrawRect(row, new Color(0.24f, 0.37f, 0.59f, 0.8f));
                float indent = 6f + _dfsDepth[k] * 10f;
                Color side = b.isCom ? AsiKeyLabels.ComColor : AsiKeyLabels.SideColor(b.side);
                EditorGUI.DrawRect(new Rect(row.x + indent, row.y + 4f, 8f, 8f), side);
                string label = b.name;
                int chain = _project.ChainOf(i);
                if (chain >= 0)
                    label += "  [" + _project.chains[chain].name + "]";
                if (b.animatePosition && !b.isCom)
                    label += "  (位移)";
                if (!b.isCom && s.modelBones.Length > i && s.modelBones[i] == null)
                    label += "  (丢失)";
                GUI.Label(new Rect(row.x + indent + 12f, row.y, row.width - indent - 12f, row.height), label, EditorStyles.miniLabel);
                Event e = Event.current;
                if (e.type == EventType.MouseDown && e.button == 0 && row.Contains(e.mousePosition))
                {
                    bool add = e.shift || e.control || e.command;
                    if (add && selected.Contains(i))
                    {
                        sel.Remove(i);
                        s.SelectBones(sel, false);
                    }
                    else
                    {
                        s.SelectBones(new[] { i }, add);
                    }
                    if (e.clickCount == 2 && i < s.controls.Length && s.controls[i] != null && SceneView.lastActiveSceneView != null)
                        SceneView.lastActiveSceneView.Frame(new Bounds(s.controls[i].position, Vector3.one * 0.5f), false);
                    e.Use();
                }
            }
        }

        private void DrawBoneDetails(AsiKeySession s, int i)
        {
            AsiKeyBone b = _project.bones[i];
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.BeginChangeCheck();
            string newName = EditorGUILayout.DelayedTextField("名称", b.name);
            if (EditorGUI.EndChangeCheck())
                RenameBone(s, i, newName);
            if (!b.isCom)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("模型骨骼", i < s.modelBones.Length ? s.modelBones[i] : null, typeof(Transform), true);
                    if (b.humanBone >= 0)
                        EditorGUILayout.LabelField("人形骨骼", ((HumanBodyBones)b.humanBone).ToString());
                    int anchor = _project.AnchorOf(i);
                    if (anchor >= 0 && b.parent >= 0)
                        EditorGUILayout.LabelField("跟随", "旋转 " + _project.bones[b.parent].name + "，位置 " + _project.bones[anchor].name);
                }
                EditorGUI.BeginChangeCheck();
                AsiBoneSide side = (AsiBoneSide)EditorGUILayout.Popup("左右", (int)b.side, AsiKeyLabels.Side);
                List<int> same = _project.UnitBones(b.unit);
                string[] names = new string[same.Count + 1];
                names[0] = "（无）";
                for (int j = 0; j < same.Count; j++)
                    names[j + 1] = _project.bones[same[j]].name;
                int mirrorPick = EditorGUILayout.Popup("镜像骨骼", same.IndexOf(b.mirror) + 1, names) - 1;
                int mirror = mirrorPick >= 0 ? same[mirrorPick] : -1;
                if (EditorGUI.EndChangeCheck())
                {
                    Mutate("骨骼左右", () =>
                    {
                        b.side = side;
                        if (mirror != b.mirror)
                        {
                            if (b.mirror >= 0 && _project.bones[b.mirror].mirror == i)
                                _project.bones[b.mirror].mirror = -1;
                            b.mirror = mirror == i ? -1 : mirror;
                            if (b.mirror >= 0)
                            {
                                AsiKeyBone o = _project.bones[b.mirror];
                                if (o.mirror >= 0 && o.mirror != i)
                                    _project.bones[o.mirror].mirror = -1;
                                o.mirror = i;
                            }
                        }
                    });
                }
                EditorGUI.BeginChangeCheck();
                bool pos = EditorGUILayout.Toggle(new GUIContent("记录位移", "为这根骨骼记录位移动画（根骨骼与质心的子骨骼默认可移动；腰椎默认位置跟随盆骨）"), b.animatePosition);
                if (EditorGUI.EndChangeCheck())
                {
                    string err = "";
                    Mutate("记录位移", () => err = AsiKeyRigBuilder.SetAnimatePosition(_project, i, pos));
                    if (!string.IsNullOrEmpty(err))
                        s.status = err;
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void RenameBone(AsiKeySession s, int i, string newName)
        {
            newName = newName.Trim();
            if (string.IsNullOrEmpty(newName) || newName == _project.bones[i].name)
                return;
            if (newName.Contains("/") || newName.StartsWith(AsiKeyRigObjects.UnitPrefix) || _project.BoneByName(newName, _project.bones[i].unit) >= 0)
            {
                s.status = "名称不能包含 / 且不能与其他骨骼重名";
                return;
            }
            Undo.RecordObject(_project, "重命名骨骼");
            if (i < s.controls.Length && s.controls[i] != null)
            {
                Undo.RecordObject(s.controls[i].gameObject, "重命名骨骼");
                s.controls[i].name = newName;
            }
            _project.bones[i].name = newName;
            _project.TouchStructure();
            EditorUtility.SetDirty(_project);
            s.EndStructureEdit();
        }

        private void AddSelectedModelBones(AsiKeySession s, int unit)
        {
            Transform root = s.UnitTarget(unit);
            if (root == null)
            {
                s.status = "找不到单位「" + _project.UnitName(unit) + "」的目标";
                return;
            }
            List<Transform> picks = new List<Transform>();
            Transform[] sel = Selection.GetTransforms(SelectionMode.Unfiltered);
            for (int k = 0; k < sel.Length; k++)
            {
                if (sel[k] != null && s.ControlIndex(sel[k]) < 0 && sel[k].IsChildOf(root) && sel[k] != root)
                    picks.Add(sel[k]);
            }
            if (picks.Count == 0)
            {
                s.status = "在 Hierarchy 里选中单位「" + _project.UnitName(unit) + "」内部的 Transform";
                return;
            }
            picks.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
            s.BeginStructureEdit("添加骨骼");
            int added = 0;
            string last = "";
            using (_project.Scope(unit))
            {
                for (int k = 0; k < picks.Count; k++)
                {
                    string err = AsiKeyRigBuilder.AddBone(_project, root, s.UnitAnimator(unit), picks[k]);
                    if (string.IsNullOrEmpty(err))
                        added++;
                    else
                        last = picks[k].name + "：" + err;
                }
            }
            s.EndStructureEdit();
            s.status = "添加了 " + added + " 根骨骼" + (last.Length > 0 ? "；" + last : "");
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            while (t != null)
            {
                d++;
                t = t.parent;
            }
            return d;
        }

        private void RemoveSelectedBones(AsiKeySession s)
        {
            List<int> sel = s.SelectedBones();
            if (sel.Count == 0)
            {
                s.status = "先选择要删除的控制器";
                return;
            }
            sel.Sort();
            Selection.activeObject = null;
            AsiKeyPanels.ClearKeySelection();
            s.BeginStructureEdit("删除骨骼");
            int removed = 0;
            string last = "";
            for (int k = sel.Count - 1; k >= 0; k--)
            {
                string name = _project.bones[sel[k]].name;
                int u = _project.bones[sel[k]].unit;
                if (!_project.IsCharacter(u) && _project.bones[sel[k]].parent < 0)
                {
                    last = name + "：物体单位的主骨骼不能删除，请在「单位」页签删除单位";
                    continue;
                }
                string err;
                using (_project.Scope(u))
                    err = AsiKeyRigBuilder.RemoveBone(_project, sel[k], s.UnitTarget(u));
                if (string.IsNullOrEmpty(err))
                    removed++;
                else
                    last = name + "：" + err;
            }
            s.EndStructureEdit();
            s.status = "删除了 " + removed + " 根骨骼" + (last.Length > 0 ? "；" + last : "");
        }

        // ---------------- IK 链 ----------------

        private void DrawChainsTab(AsiKeySession s)
        {
            EditorGUILayout.LabelField("IK 链（" + _project.chains.Count + "）", Header);
            EditorGUILayout.HelpBox("链成员的关节必须依次相连（Biped 独立朝向的脚旋转跟随质心，关节仍连在小腿上，也算相连）。关键帧类型决定链在 IK 与 FK 之间如何过渡：固定/滑动 = IK，自由 = FK。", MessageType.None);
            AsiKeyPanels.DrawFingers(s, false);
            EditorGUILayout.BeginHorizontal();
            _newChainName = EditorGUILayout.TextField(_newChainName);
            if (GUILayout.Button("用选中控制器建链", GUILayout.Width(120f)))
            {
                List<int> sel = s.SelectedBones();
                string err = "";
                int index = _project.chains.Count;
                Mutate("创建 IK 链", () =>
                {
                    err = AsiKeyRigBuilder.CreateChain(_project, sel, _newChainName.Trim());
                    if (string.IsNullOrEmpty(err))
                        s.RecomputeFreeTargets(index);
                });
                if (string.IsNullOrEmpty(err))
                {
                    _editChain = index;
                    _newChainName = "";
                    s.status = "已创建 IK 链 " + _project.chains[index].name;
                }
                else
                {
                    s.status = err;
                }
            }
            EditorGUILayout.EndHorizontal();

            List<int> selChains = s.SelectedChains();
            for (int c = 0; c < _project.chains.Count; c++)
            {
                AsiKeyChain chain = _project.chains[c];
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                bool open = _editChain == c;
                Color bg = GUI.backgroundColor;
                if (selChains.Contains(c))
                    GUI.backgroundColor = new Color(0.5f, 0.7f, 1f);
                string chainLabel = _project.units.Count > 1 ? _project.UnitName(_project.ChainUnit(c)) + "/" + chain.name : chain.name;
                if (GUILayout.Button((open ? "▼ " : "▶ ") + chainLabel, EditorStyles.miniButtonLeft))
                    _editChain = open ? -1 : c;
                GUI.backgroundColor = bg;
                if (GUILayout.Button("选末端", EditorStyles.miniButtonMid, GUILayout.Width(48f)))
                    s.SelectBones(new[] { chain.Effector }, false);
                if (GUILayout.Button("删除", EditorStyles.miniButtonRight, GUILayout.Width(40f)))
                {
                    if (EditorUtility.DisplayDialog("K帧工具", "删除 IK 链 \"" + chain.name + "\"？关键帧会拆回各成员的 FK 轨道，IK 参数丢失。", "删除", "取消"))
                    {
                        int cc = c;
                        AsiKeyPanels.ClearKeySelection();
                        Mutate("删除 IK 链", () => AsiKeyRigBuilder.DeleteChain(_project, cc));
                        if (_editChain == cc)
                            _editChain = -1;
                        GUIUtility.ExitGUI();
                    }
                }
                EditorGUILayout.EndHorizontal();
                if (open)
                    DrawChainDetails(s, c);
            }
        }

        private void DrawChainDetails(AsiKeySession s, int c)
        {
            AsiKeyChain chain = _project.chains[c];
            EditorGUI.indentLevel++;
            string members = "";
            for (int m = 0; m < chain.bones.Length; m++)
                members += (m > 0 ? " → " : "") + _project.bones[chain.bones[m]].name;
            EditorGUILayout.LabelField("成员", members, EditorStyles.wordWrappedMiniLabel);
            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.DelayedTextField("名称", chain.name);
            Vector3 pole = EditorGUILayout.Vector3Field(new GUIContent("弯曲方向", "角色根空间。肢体伸直时以此方向决定膝/肘朝向；三节链的膝/肘铰链轴也由它决定，改了后可点「修正现有关键帧」"), chain.poleHint);
            if (EditorGUI.EndChangeCheck())
            {
                Mutate("编辑 IK 链", () =>
                {
                    if (!string.IsNullOrEmpty(name.Trim()))
                        chain.name = name.Trim();
                    chain.poleHint = pole.sqrMagnitude > 1e-8f ? pole.normalized : Vector3.forward;
                });
            }
            if (GUILayout.Button("按静止姿势估算弯曲方向"))
                Mutate("估算弯曲方向", () => chain.poleHint = AsiKeyRigBuilder.GuessPole(_project, chain));
            if (chain.bones.Length == 3 && !AsiKeyFingers.IsFingerChain(_project, c))
                DrawHinge(s, c);

            EditorGUILayout.LabelField("轴心（末端控制器局部空间）", EditorStyles.miniBoldLabel);
            int remove = -1;
            for (int k = 0; k < chain.pivots.Length; k++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                string pn = EditorGUILayout.TextField(k < chain.pivotNames.Length ? chain.pivotNames[k] : "轴心 " + k, GUILayout.Width(80f));
                Vector3 pv;
                using (new EditorGUI.DisabledScope(k == 0))
                    pv = EditorGUILayout.Vector3Field(GUIContent.none, chain.pivots[k]);
                if (EditorGUI.EndChangeCheck())
                {
                    int kk = k;
                    Mutate("编辑轴心", () =>
                    {
                        EnsurePivotNames(chain);
                        chain.pivotNames[kk] = pn;
                        if (kk > 0)
                            chain.pivots[kk] = pv;
                        FixChainJoins(c);
                    });
                }
                using (new EditorGUI.DisabledScope(k == 0))
                {
                    if (GUILayout.Button(new GUIContent("取选中", "设为当前选中物体的位置"), EditorStyles.miniButton, GUILayout.Width(44f)))
                        SetPivotFromSelection(s, c, k);
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20f)))
                        remove = k;
                }
                EditorGUILayout.EndHorizontal();
            }
            if (remove > 0)
                Mutate("删除轴心", () => RemovePivot(c, remove));
            if (GUILayout.Button("添加轴心"))
            {
                Mutate("添加轴心", () =>
                {
                    EnsurePivotNames(chain);
                    List<Vector3> pv = new List<Vector3>(chain.pivots) { Vector3.zero };
                    List<string> pn = new List<string>(chain.pivotNames) { "轴心 " + chain.pivots.Length };
                    chain.pivots = pv.ToArray();
                    chain.pivotNames = pn.ToArray();
                });
            }
            EditorGUI.indentLevel--;
        }

        private void DrawHinge(AsiKeySession s, int c)
        {
            AsiKeyChain chain = _project.chains[c];
            EditorGUILayout.LabelField("膝/肘铰链", EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            bool on = EditorGUILayout.Toggle(new GUIContent("铰链锁定（Biped）",
                "FK 编辑时中间关节只绕铰链轴弯，去掉扭转与侧弯，弯曲角限制在范围内。只作用于今后的编辑，已有关键帧用下方按钮修正"), chain.hinge);
            float lo = chain.bendMin;
            float hi = chain.bendMax;
            using (new EditorGUI.DisabledScope(!on))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(new GUIContent("弯曲范围", "0° 为伸直，正值向弯曲方向弯，不能反折"));
                int indent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                lo = EditorGUILayout.FloatField(lo, GUILayout.Width(40f));
                EditorGUILayout.MinMaxSlider(ref lo, ref hi, 0f, 180f);
                hi = EditorGUILayout.FloatField(hi, GUILayout.Width(40f));
                EditorGUI.indentLevel = indent;
                EditorGUILayout.EndHorizontal();
            }
            if (EditorGUI.EndChangeCheck())
            {
                lo = Mathf.Clamp(lo, 0f, 180f);
                hi = Mathf.Clamp(hi, lo, 180f);
                Mutate("编辑膝/肘铰链", () =>
                {
                    chain.hinge = on;
                    chain.bendMin = lo;
                    chain.bendMax = hi;
                });
            }
            int mid = chain.bones[1];
            if (s.Valid && s.pose.rot != null && mid >= 0 && mid < s.pose.rot.Length)
                EditorGUILayout.LabelField("当前弯曲", AsiKeyHinge.Bend(_project, chain, s.pose.rot[mid]).ToString("F1") + "°");
            using (new EditorGUI.DisabledScope(!on))
            {
                if (GUILayout.Button(new GUIContent("修正现有关键帧", "把基础层该链关键帧里的中间关节投影到铰链上：去掉扭转与侧弯，夹到弯曲范围。可撤销")))
                {
                    int count = 0;
                    Mutate("修正膝/肘关键帧", () => count = AsiKeyRigBuilder.FixHingeKeys(_project, c));
                    s.status = count > 0 ? "已修正 " + chain.name + " 的 " + count + " 个关键帧" : chain.name + " 的关键帧都已在铰链上";
                }
            }
        }

        private static void EnsurePivotNames(AsiKeyChain chain)
        {
            if (chain.pivotNames != null && chain.pivotNames.Length == chain.pivots.Length)
                return;
            string[] names = new string[chain.pivots.Length];
            for (int k = 0; k < names.Length; k++)
                names[k] = chain.pivotNames != null && k < chain.pivotNames.Length ? chain.pivotNames[k] : "轴心 " + k;
            chain.pivotNames = names;
        }

        private void SetPivotFromSelection(AsiKeySession s, int c, int k)
        {
            AsiKeyChain chain = _project.chains[c];
            Transform t = Selection.activeTransform;
            int eff = chain.Effector;
            if (t == null || eff < 0 || eff >= s.controls.Length || s.controls[eff] == null)
            {
                s.status = "先在场景里选中一个物体作为轴心位置";
                return;
            }
            Vector3 local = s.controls[eff].InverseTransformPoint(t.position);
            Mutate("设置轴心", () =>
            {
                chain.pivots[k] = local;
                FixChainJoins(c);
            });
        }

        private void RemovePivot(int c, int k)
        {
            AsiKeyChain chain = _project.chains[c];
            EnsurePivotNames(chain);
            List<Vector3> pv = new List<Vector3>(chain.pivots);
            List<string> pn = new List<string>(chain.pivotNames);
            pv.RemoveAt(k);
            pn.RemoveAt(k);
            chain.pivots = pv.ToArray();
            chain.pivotNames = pn.ToArray();
            for (int l = 0; l < _project.layers.Count; l++)
            {
                AsiKeyTrack tr = _project.FindTrack(l, AsiTrackKind.Chain, c);
                if (tr == null)
                    continue;
                for (int i = 0; i < tr.keys.Count; i++)
                {
                    if (tr.keys[i].pivot == k)
                        tr.keys[i].pivot = 0;
                    else if (tr.keys[i].pivot > k)
                        tr.keys[i].pivot--;
                }
            }
            FixChainJoins(c);
        }

        private void FixChainJoins(int c)
        {
            AsiKeyTrack tr = _project.FindTrack(0, AsiTrackKind.Chain, c);
            if (tr != null)
                AsiKeyEdit.EnforceJoins(_project, tr, -1);
        }

        // ---------------- 外形 ----------------

        private void MutateShapes(string undo, List<int> bones, System.Action<AsiKeyShape, int> fn)
        {
            Undo.RecordObject(_project, undo);
            for (int k = 0; k < bones.Count; k++)
                fn(_project.bones[bones[k]].shape, bones[k]);
            EditorUtility.SetDirty(_project);
            SceneView.RepaintAll();
        }

        private void DrawShapesTab(AsiKeySession s)
        {
            EditorGUILayout.LabelField("控制器外形", Header);
            List<int> sel = s.SelectedBones();
            int unit = UnitPicker(s, sel);
            List<int> all = _project.UnitBones(unit);
            List<int> target = sel.Count > 0 ? sel : all;
            EditorGUILayout.HelpBox(sel.Count > 0
                ? "编辑 " + sel.Count + " 个选中控制器。缩放工具（R）拖动控制器也可以直接调整外形大小。"
                : "没有选中控制器，下面的批量按钮作用于单位「" + _project.UnitName(unit) + "」的全部骨骼。", MessageType.None);

            EditorGUILayout.BeginHorizontal();
            string scope = sel.Count > 0 ? "选中" : "全部";
            if (GUILayout.Button(scope + "→方块"))
                MutateShapes("外形", target, (sh, i) => sh.kind = AsiShapeKind.BipBox);
            if (GUILayout.Button(scope + "→圆环"))
                MutateShapes("外形", target, (sh, i) => sh.kind = AsiShapeKind.Ring);
            if (GUILayout.Button(scope + "→方框"))
                MutateShapes("外形", target, (sh, i) => sh.kind = AsiShapeKind.Rect);
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button(scope + "恢复默认外形"))
            {
                Undo.RecordObject(_project, "恢复默认外形");
                for (int k = 0; k < target.Count; k++)
                {
                    int i = target[k];
                    int u = _project.bones[i].unit;
                    if (!_project.IsCharacter(u) && _project.bones[i].parent < 0 && s.UnitTarget(u) != null)
                    {
                        float len;
                        _project.bones[i].shape = AsiKeyUnits.ObjectShape(s.UnitTarget(u), out len);
                        continue;
                    }
                    using (_project.Scope(u))
                        _project.bones[i].shape = AsiKeyRigBuilder.DefaultShape(_project, i, AsiKeyRigBuilder.UnitSize(_project));
                }
                EditorUtility.SetDirty(_project);
                SceneView.RepaintAll();
            }

            if (sel.Count > 0)
                DrawShapeFields(sel);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("外形预设", EditorStyles.miniBoldLabel);
            _preset = (AsiKeyShapePreset)EditorGUILayout.ObjectField(_preset, typeof(AsiKeyShapePreset), false);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("保存为预设…"))
                SaveShapePreset();
            using (new EditorGUI.DisabledScope(_preset == null))
            {
                if (GUILayout.Button("应用到" + scope))
                {
                    Undo.RecordObject(_project, "应用外形预设");
                    int n = _preset.Apply(_project, sel.Count > 0 ? sel : null);
                    EditorUtility.SetDirty(_project);
                    SceneView.RepaintAll();
                    s.status = "应用了 " + n + " 个外形";
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void SaveShapePreset()
        {
            string folder = string.IsNullOrEmpty(_project.bakeFolder) ? "Assets" : _project.bakeFolder;
            string path = EditorUtility.SaveFilePanelInProject("保存外形预设", _project.name + "_Shapes", "asset", "", folder);
            if (string.IsNullOrEmpty(path))
                return;
            AsiKeyShapePreset preset = AssetDatabase.LoadAssetAtPath<AsiKeyShapePreset>(path);
            if (preset == null)
            {
                preset = CreateInstance<AsiKeyShapePreset>();
                preset.Capture(_project);
                AssetDatabase.CreateAsset(preset, path);
            }
            else
            {
                Undo.RecordObject(preset, "保存外形预设");
                preset.Capture(_project);
                EditorUtility.SetDirty(preset);
            }
            AssetDatabase.SaveAssets();
            _preset = preset;
            ShowNotification(new GUIContent("已保存 " + Path.GetFileName(path)), 1f);
        }

        private void DrawShapeFields(List<int> sel)
        {
            AsiKeyShape first = _project.bones[sel[0]].shape;
            bool Mixed(System.Func<AsiKeyShape, object> get)
            {
                object v = get(first);
                for (int k = 1; k < sel.Count; k++)
                {
                    if (!Equals(get(_project.bones[sel[k]].shape), v))
                        return true;
                }
                return false;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.showMixedValue = Mixed(x => x.kind);
            EditorGUI.BeginChangeCheck();
            AsiShapeKind kind = (AsiShapeKind)EditorGUILayout.Popup("类型", (int)first.kind, AsiKeyLabels.Shape);
            if (EditorGUI.EndChangeCheck())
                MutateShapes("外形类型", sel, (sh, i) => sh.kind = kind);

            EditorGUI.showMixedValue = Mixed(x => x.visible);
            EditorGUI.BeginChangeCheck();
            bool visible = EditorGUILayout.Toggle("显示", first.visible);
            if (EditorGUI.EndChangeCheck())
                MutateShapes("外形显示", sel, (sh, i) => sh.visible = visible);

            EditorGUI.showMixedValue = Mixed(x => x.color);
            EditorGUI.BeginChangeCheck();
            Color color = EditorGUILayout.ColorField("颜色", first.color);
            if (EditorGUI.EndChangeCheck())
                MutateShapes("外形颜色", sel, (sh, i) => sh.color = color);

            switch (first.kind)
            {
                case AsiShapeKind.BipBox:
                    FloatShapeField(sel, "宽", Mixed(x => x.width), first.width, (sh, v) => sh.width = v);
                    FloatShapeField(sel, "高", Mixed(x => x.height), first.height, (sh, v) => sh.height = v);
                    FloatShapeField(sel, "长", Mixed(x => x.length), first.length, (sh, v) => sh.length = v);
                    break;
                case AsiShapeKind.Ring:
                case AsiShapeKind.Rect:
                    FloatShapeField(sel, first.kind == AsiShapeKind.Ring ? "半径 X" : "半宽", Mixed(x => x.radiusX), first.radiusX, (sh, v) => sh.radiusX = v);
                    FloatShapeField(sel, first.kind == AsiShapeKind.Ring ? "半径 Y" : "半高", Mixed(x => x.radiusY), first.radiusY, (sh, v) => sh.radiusY = v);
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(EditorGUIUtility.labelWidth);
                    if (GUILayout.Button(first.kind == AsiShapeKind.Ring ? "改成正圆" : "改成正方形", EditorStyles.miniButton))
                        MutateShapes("外形大小", sel, (sh, i) => sh.radiusY = sh.radiusX);
                    EditorGUILayout.EndHorizontal();
                    break;
                case AsiShapeKind.Mesh:
                    EditorGUI.showMixedValue = Mixed(x => x.mesh);
                    EditorGUI.BeginChangeCheck();
                    Mesh mesh = (Mesh)EditorGUILayout.ObjectField("Mesh", first.mesh, typeof(Mesh), false);
                    if (EditorGUI.EndChangeCheck())
                        MutateShapes("外形 Mesh", sel, (sh, i) => sh.mesh = mesh);
                    EditorGUI.showMixedValue = Mixed(x => x.meshScale);
                    EditorGUI.BeginChangeCheck();
                    Vector3 ms = EditorGUILayout.Vector3Field("Mesh 缩放", first.meshScale);
                    if (EditorGUI.EndChangeCheck())
                        MutateShapes("外形 Mesh", sel, (sh, i) => sh.meshScale = ms);
                    break;
            }

            EditorGUI.showMixedValue = Mixed(x => x.offset);
            EditorGUI.BeginChangeCheck();
            Vector3 offset = EditorGUILayout.Vector3Field(new GUIContent("偏移", "外形坐标系：+Z 沿骨骼"), first.offset);
            if (EditorGUI.EndChangeCheck())
                MutateShapes("外形偏移", sel, (sh, i) => sh.offset = offset);
            EditorGUI.showMixedValue = Mixed(x => x.euler);
            EditorGUI.BeginChangeCheck();
            Vector3 euler = EditorGUILayout.Vector3Field("旋转", first.euler);
            if (EditorGUI.EndChangeCheck())
                MutateShapes("外形旋转", sel, (sh, i) => sh.euler = euler);
            EditorGUI.showMixedValue = false;
            EditorGUILayout.EndVertical();
        }

        private void FloatShapeField(List<int> sel, string label, bool mixed, float value, System.Action<AsiKeyShape, float> set)
        {
            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            float v = EditorGUILayout.FloatField(label, value);
            if (EditorGUI.EndChangeCheck())
            {
                v = Mathf.Max(0.001f, v);
                MutateShapes("外形大小", sel, (sh, i) => set(sh, v));
            }
            EditorGUI.showMixedValue = false;
        }
    }
}
