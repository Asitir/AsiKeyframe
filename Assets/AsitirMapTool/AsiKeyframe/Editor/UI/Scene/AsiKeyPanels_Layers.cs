using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>动画层面板：基础层之上的叠加层，按权重叠加旋转与位移增量。</summary>
    public static partial class AsiKeyPanels
    {
        private static readonly GUIContent LayersHelp = new GUIContent("?",
            "基础层记录完整姿势和 IK 关键帧；叠加层默认记增量，按权重叠加。\n" +
            "「覆写」开启后，本层打过关键帧的骨骼替换它上面各层的姿势，没打过的骨骼不动。\n" +
            "「保持底层 IK」开启时增量在 IK 求解之前叠加，踩踏的脚不会被带走；关闭时增量直接加在最终姿势上。\n" +
            "权重可以打关键帧，「时间轴」把这一层的权重关键帧显示出来。\n" +
            "正在编辑的层用 ● 标记，打关键帧和拖动控制器都写进该层。");

        public static void DrawLayersPanel(AsiKeySession s)
        {
            if (!Begin(s))
                return;
            for (int l = 0; l < _project.layers.Count; l++)
            {
                AsiKeyLayer layer = _project.layers[l];
                bool active = _project.activeLayer == l;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                Color bg = GUI.backgroundColor;
                if (active)
                    GUI.backgroundColor = new Color(1f, 0.55f, 0.3f);
                if (GUILayout.Button(active ? "●" : "○", EditorStyles.miniButton, GUILayout.Width(24f)) && !active)
                    SetActiveLayer(l);
                GUI.backgroundColor = bg;
                EditorGUI.BeginChangeCheck();
                string name = EditorGUILayout.DelayedTextField(layer.name);
                if (EditorGUI.EndChangeCheck() && !string.IsNullOrEmpty(name.Trim()))
                    Mutate("层名称", () => layer.name = name.Trim());
                if (l > 0)
                {
                    using (new EditorGUI.DisabledScope(l <= 1))
                    {
                        if (GUILayout.Button("↑", EditorStyles.miniButtonLeft, GUILayout.Width(22f)))
                            MoveLayer(l, -1);
                    }
                    using (new EditorGUI.DisabledScope(l >= _project.layers.Count - 1))
                    {
                        if (GUILayout.Button("↓", EditorStyles.miniButtonMid, GUILayout.Width(22f)))
                            MoveLayer(l, 1);
                    }
                    if (GUILayout.Button("×", EditorStyles.miniButtonRight, GUILayout.Width(22f)))
                    {
                        if (EditorUtility.DisplayDialog("K帧工具", "删除层 \"" + layer.name + "\" 及其所有关键帧？", "删除", "取消"))
                        {
                            int ll = l;
                            _selKeys.Clear();
                            Mutate("删除层", () =>
                            {
                                _project.layers.RemoveAt(ll);
                                if (_project.activeLayer >= ll)
                                    _project.activeLayer = Mathf.Max(0, _project.activeLayer - 1);
                            });
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                EditorGUILayout.EndHorizontal();

                if (l == 0)
                {
                    EditorGUILayout.LabelField("基础层（完整姿势与 IK）", EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUI.BeginChangeCheck();
                    float shown = AsiKeyEvaluator.LayerWeightAt(_project, l, s.frame);
                    float weight = EditorGUILayout.Slider("权重", shown, 0f, 1f);
                    EditorGUILayout.BeginHorizontal();
                    bool mute = GUILayout.Toggle(layer.mute, new GUIContent("禁用", "这一层不参与求值，拖动控制器也看不到效果"), EditorStyles.miniButtonLeft);
                    bool over = GUILayout.Toggle(layer.overwrite, new GUIContent("覆写", "本层打过关键帧的骨骼替换上面各层的姿势，没打过的骨骼保持上面的结果"), EditorStyles.miniButtonMid);
                    bool keep = GUILayout.Toggle(layer.keepBaseIK, new GUIContent("保持底层 IK", "增量在 IK 之前叠加，踩踏/滑动的末端位置不变"), EditorStyles.miniButtonRight);
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    bool show = GUILayout.Toggle(layer.showWeight, new GUIContent("时间轴", "在时间轴上显示这一层的权重关键帧"), EditorStyles.miniButtonLeft);
                    if (GUILayout.Button(new GUIContent("K 权重", "把当前权重打到这一帧"), EditorStyles.miniButtonRight))
                        s.KeyLayerWeight(l, weight);
                    EditorGUILayout.EndHorizontal();
                    if (EditorGUI.EndChangeCheck())
                    {
                        int ll = l;
                        float written = weight;
                        bool weightMoved = Mathf.Abs(weight - shown) > 1e-4f;
                        AsiKeyTrack wt = _project.FindTrack(ll, AsiTrackKind.LayerWeight, ll);
                        bool keyed = wt != null && wt.keys.Count > 0;
                        Mutate("层设置", () =>
                        {
                            AsiKeyLayer ly = _project.layers[ll];
                            ly.mute = mute;
                            ly.overwrite = over;
                            ly.keepBaseIK = keep;
                            if (show)
                                _project.GetTrack(ll, AsiTrackKind.LayerWeight, ll);
                            ly.showWeight = show;
                            if (weightMoved && !keyed && !s.autoKey)
                                ly.weight = written;
                        });
                        if (weightMoved && (keyed || s.autoKey))
                            s.KeyLayerWeight(ll, written);
                    }
                    int keys = 0;
                    for (int t = 0; t < layer.tracks.Count; t++)
                        keys += layer.tracks[t].keys.Count;
                    EditorGUILayout.LabelField(layer.tracks.Count + " 条轨道，" + keys + " 个关键帧", EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("新建叠加层"))
            {
                Mutate("新建层", () =>
                {
                    AsiKeyLayer layer = new AsiKeyLayer();
                    layer.name = "层 " + _project.layers.Count;
                    _project.layers.Add(layer);
                    _project.activeLayer = _project.layers.Count - 1;
                });
                _selKeys.Clear();
            }
            using (new EditorGUI.DisabledScope(_project.activeLayer == 0))
            {
                if (GUILayout.Button(new GUIContent("回到基础层", "编辑基础层")))
                    SetActiveLayer(0);
                if (GUILayout.Button(new GUIContent("从基础层拷贝 Pose", "把选中骨骼在基础层的相对旋转（可移动骨骼还有位置）写进当前层")))
                    s.CopyBasePose();
            }
            GUILayout.Label(LayersHelp, EditorStyles.miniLabel, GUILayout.Width(12f));
            EditorGUILayout.EndHorizontal();
            if (_project.activeLayer > 0 && _project.ActiveLayer.mute)
                EditorGUILayout.HelpBox("当前编辑的层已禁用，拖动控制器看不到效果。", MessageType.Warning);
        }

        private static void SetActiveLayer(int l)
        {
            _selKeys.Clear();
            Mutate("切换编辑层", () => _project.activeLayer = Mathf.Clamp(l, 0, _project.layers.Count - 1));
        }

        private static void MoveLayer(int l, int dir)
        {
            int to = l + dir;
            if (l <= 0 || to <= 0 || to >= _project.layers.Count)
                return;
            Mutate("调整层顺序", () =>
            {
                AsiKeyLayer a = _project.layers[l];
                _project.layers[l] = _project.layers[to];
                _project.layers[to] = a;
                if (_project.activeLayer == l)
                    _project.activeLayer = to;
                else if (_project.activeLayer == to)
                    _project.activeLayer = l;
            });
        }
    }
}
