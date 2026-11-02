using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>Motion 面板（四种关键帧、删除、跳转、姿势、IK 参数）与 Key Info。</summary>
    public static partial class AsiKeyPanels
    {
        private static readonly List<AsiKeyTrack> _infoTracks = new List<AsiKeyTrack>();
        private static int _infoTrack;
        private static readonly AsiKey s_prevKey = new AsiKey();
        private static readonly AsiKey s_nextKey = new AsiKey();

        public static void DrawMotionPanel(AsiKeySession s)
        {
            if (!Begin(s))
                return;
            List<int> sel = s.SelectedBones();
            string names = sel.Count == 0 ? "（未选择控制器，在场景里点选外形）" : "";
            for (int k = 0; k < sel.Count && k < 4; k++)
                names += (k > 0 ? "、" : "") + _project.bones[sel[k]].name;
            if (sel.Count > 4)
                names += " 等 " + sel.Count + " 个";
            EditorGUILayout.LabelField("选中", names, EditorStyles.wordWrappedMiniLabel);

            bool hasCom = false;
            for (int k = 0; k < sel.Count; k++)
                hasCom |= _project.bones[sel[k]].isCom;
            if (hasCom || _project.AnyHumanoid)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("质心轨道", GUILayout.Width(60f));
                s.comKeyH = GUILayout.Toggle(s.comKeyH, "水平", EditorStyles.miniButtonLeft);
                s.comKeyV = GUILayout.Toggle(s.comKeyV, "垂直", EditorStyles.miniButtonMid);
                s.comKeyR = GUILayout.Toggle(s.comKeyR, "旋转", EditorStyles.miniButtonRight);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(sel.Count == 0))
            {
                if (ColoredButton("注册 (" + AsiKeyShortcuts.Binding(AsiKeyShortcuts.SetKey) + ")", AsiKeyLabels.KeyColor(AsiKeyType.Set), GUILayout.Height(24f)))
                    s.SetKey(AsiKeyType.Set);
                if (ColoredButton("固定", AsiKeyLabels.KeyColor(AsiKeyType.Planted), GUILayout.Height(24f)))
                    s.SetKey(AsiKeyType.Planted);
                if (ColoredButton("滑动", AsiKeyLabels.KeyColor(AsiKeyType.Sliding), GUILayout.Height(24f)))
                    s.SetKey(AsiKeyType.Sliding);
                if (ColoredButton("自由", AsiKeyLabels.KeyColor(AsiKeyType.Free), GUILayout.Height(24f)))
                    s.SetKey(AsiKeyType.Free);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(sel.Count == 0))
            {
                if (GUILayout.Button("删除关键帧"))
                    s.DeleteKeysAtFrame();
            }
            if (GUILayout.Button(new GUIContent("◀ 上一关键帧", AsiKeyShortcuts.Binding(AsiKeyShortcuts.PrevKey))))
                s.JumpKey(-1);
            if (GUILayout.Button(new GUIContent("下一关键帧 ▶", AsiKeyShortcuts.Binding(AsiKeyShortcuts.NextKey))))
                s.JumpKey(1);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("复制姿势", "复制当前帧全部骨骼的姿势")))
                s.CopyPose();
            using (new EditorGUI.DisabledScope(AsiKeySession.Clipboard == null))
            {
                if (GUILayout.Button(new GUIContent("粘贴", "粘贴到选中的骨骼；没选中时粘贴全部")))
                    s.PastePose(false);
                if (GUILayout.Button(new GUIContent("镜像粘贴", "左右互换后粘贴")))
                    s.PastePose(true);
            }
            if (GUILayout.Button(new GUIContent("镜像当前", "把当前姿势左右镜像（选中骨骼时只改选中的）")))
                s.MirrorCurrentPose();
            EditorGUILayout.EndHorizontal();

            List<int> chains = s.SelectedChains();
            if (chains.Count > 0)
                DrawChainMotion(s, chains[0]);
            DrawFingers(s, true);
        }

        private static void DrawChainMotion(AsiKeySession s, int c)
        {
            AsiKeyChain chain = _project.chains[c];
            AsiChainState st = s.pose.chains[c];
            EditorGUILayout.Space(4f);
            GUILayout.Label("IK：" + chain.name, EditorStyles.boldLabel);
            string state = st.valid
                ? "IK Blend " + st.blend.ToString("0.00") + "   空间 " + AsiKeyLabels.Space[(int)st.a.space] + (st.join ? "   连接上一帧" : "")
                : "该链还没有关键帧（纯 FK）";
            EditorGUILayout.LabelField("当前状态", state, EditorStyles.miniLabel);
            AsiKeyTrack bindTrack;
            AsiKey bindKey;
            if (GoverningChainKey(_project, c, s.frame, out bindTrack, out bindKey) && bindKey.type == AsiKeyType.Sliding)
                DrawSlidingBinding(s, bindTrack, bindKey, true);

            int[] values;
            string[] pivots = PivotNames(_project, chain, out values);
            int pivot = PivotOption(values, s.PivotChoice(c));
            EditorGUI.BeginChangeCheck();
            pivot = EditorGUILayout.Popup(new GUIContent("IK 轴心", "固定/滑动关键帧绕这个点转动（脚趾根/脚跟/脚掌/脚尖等）"), pivot, pivots);
            if (EditorGUI.EndChangeCheck())
                SetPivot(s, c, values[pivot]);

            using (new EditorGUI.DisabledScope(!st.valid || chain.bones.Length < 3))
            {
                EditorGUI.BeginChangeCheck();
                float sw = EditorGUILayout.Slider(new GUIContent("膝/肘朝向", SwivelTip(chain)), st.valid ? st.swivel : 0f, -180f, 180f);
                if (EditorGUI.EndChangeCheck())
                    s.SetSwivel(c, sw);
            }
        }

        private static string SwivelTip(AsiKeyChain chain)
        {
            return chain.bones.Length == 3
                ? "Swivel：绕「链根→目标」轴转动中间关节。0 是关键帧姿势本身的弯曲平面，打关键帧时转角并入姿势、数值回到 0"
                : "Swivel：绕「链根→目标」轴转动中间关节";
        }

        // ---------------- Key Info ----------------

        private static bool ResolveInfoKey(AsiKeySession s, out AsiKeyTrack track, out AsiKey key)
        {
            track = null;
            key = null;
            if (_selKeys.Count == 1)
            {
                foreach (AsiKey k in _selKeys)
                    key = k;
                track = FindTrackOfKey(key);
                if (track != null)
                    return true;
                key = null;
            }
            s.TracksFor(s.SelectedBones(), _project.activeLayer, _infoTracks, false);
            if (_infoTracks.Count == 0)
                return false;
            _infoTrack = Mathf.Clamp(_infoTrack, 0, _infoTracks.Count - 1);
            if (_infoTracks.Count > 1)
            {
                string[] names = new string[_infoTracks.Count];
                for (int i = 0; i < names.Length; i++)
                    names[i] = _project.TrackName(_infoTracks[i]);
                _infoTrack = EditorGUILayout.Popup("轨道", _infoTrack, names);
            }
            track = _infoTracks[_infoTrack];
            int idx = AsiKeyTrackEval.FindKeyIndex(track.keys, s.frame);
            key = idx >= 0 ? track.keys[idx] : null;
            return true;
        }

        private static AsiKeyTrack FindTrackOfKey(AsiKey key)
        {
            for (int l = 0; l < _project.layers.Count; l++)
            {
                List<AsiKeyTrack> tracks = _project.layers[l].tracks;
                for (int t = 0; t < tracks.Count; t++)
                {
                    if (tracks[t].keys.Contains(key))
                        return tracks[t];
                }
            }
            return null;
        }

        private static int LayerOfTrack(AsiKeyTrack tr)
        {
            for (int l = 0; l < _project.layers.Count; l++)
            {
                if (_project.layers[l].tracks.Contains(tr))
                    return l;
            }
            return 0;
        }

        public static void DrawKeyInfo(AsiKeySession s)
        {
            if (!Begin(s))
                return;
            AsiKeyTrack tr;
            AsiKey key;
            if (!ResolveInfoKey(s, out tr, out key))
            {
                EditorGUILayout.HelpBox("选中控制器或时间轴上的一个关键帧后在这里编辑插值和 IK 参数。", MessageType.None);
                return;
            }
            int layer = LayerOfTrack(tr);
            EditorGUILayout.LabelField("轨道", _project.TrackName(tr) + (layer > 0 ? "（" + _project.layers[layer].name + "）" : ""));

            EditorGUI.BeginChangeCheck();
            AsiInterpMode mode = (AsiInterpMode)EditorGUILayout.Popup("插值", (int)tr.mode, AsiKeyLabels.Interp);
            if (EditorGUI.EndChangeCheck() && mode != tr.mode)
            {
                if (tr.keys.Count < 2 || EditorUtility.DisplayDialog("切换插值", "把这条轨道的关键帧从 " + AsiKeyLabels.Interp[(int)tr.mode] + " 转成 " +
                    AsiKeyLabels.Interp[(int)mode] + "。关键帧上的姿势保留，段间的过渡会变化（有损）。", "转换", "取消"))
                {
                    Mutate("切换插值", () => AsiKeyEdit.ConvertMode(_project, tr, mode));
                }
            }
            if (tr.mode == AsiInterpMode.Curve && _project.MemberCount(tr) > 0)
            {
                EditorGUI.BeginChangeCheck();
                AsiRotOrder order = (AsiRotOrder)EditorGUILayout.Popup("旋转顺序", (int)tr.rotOrder, AsiKeyLabels.RotOrder);
                if (EditorGUI.EndChangeCheck())
                    Mutate("旋转顺序", () => AsiKeyEdit.SetRotOrder(_project, tr, order));
                if (GUILayout.Button("欧拉过滤"))
                    Mutate("欧拉过滤", () => AsiKeyEdit.EulerFilter(_project, tr));
            }

            if (key == null)
            {
                EditorGUILayout.HelpBox("当前帧在这条轨道上没有关键帧。", MessageType.None);
                return;
            }
            EditorGUILayout.Space(2f);
            EditorGUI.BeginChangeCheck();
            float frame = EditorGUILayout.FloatField("帧", key.frame);
            if (EditorGUI.EndChangeCheck())
            {
                List<AsiKey> one = new List<AsiKey> { key };
                float delta = Mathf.Round(frame) - key.frame;
                Mutate("移动关键帧", () => AsiKeyEdit.MoveKeys(_project, tr, one, delta, layer));
            }

            if (tr.mode == AsiInterpMode.TCB)
                DrawTCB(tr, key, layer);
            else
                DrawTangents(tr, key, layer);

            if (tr.kind == AsiTrackKind.Chain && layer == 0)
                DrawChainKey(s, tr, key);
        }

        private static void DrawTCB(AsiKeyTrack tr, AsiKey key, int layer)
        {
            EditorGUI.BeginChangeCheck();
            float t = EditorGUILayout.Slider("张力 Tension", key.tension, 0f, 50f);
            float c = EditorGUILayout.Slider("连续 Continuity", key.continuity, 0f, 50f);
            float b = EditorGUILayout.Slider("偏移 Bias", key.bias, 0f, 50f);
            float et = EditorGUILayout.Slider("缓入 Ease To", key.easeTo, 0f, 50f);
            float ef = EditorGUILayout.Slider("缓出 Ease From", key.easeFrom, 0f, 50f);
            if (EditorGUI.EndChangeCheck())
                Mutate("TCB 参数", () => AsiKeyEdit.ApplyTCB(key, t, c, b, et, ef));
            Rect r = GUILayoutUtility.GetRect(200f, 70f, GUILayout.ExpandWidth(true));
            DrawTCBPreview(r, key);
        }

        /// <summary>Max 式 TCB 预览：三个关键帧，中间是当前关键帧。</summary>
        private static void DrawTCBPreview(Rect r, AsiKey key)
        {
            EditorGUI.DrawRect(r, new Color(0.12f, 0.12f, 0.12f));
            const int n = 40;
            Vector3[] pts = new Vector3[n * 2 + 1];
            float[] v = { 0f, 1f, 0f };
            AsiKey[] ks = { s_prevKey, key, s_nextKey };
            int idx = 0;
            for (int seg = 0; seg < 2; seg++)
            {
                for (int i = 0; i <= n; i++)
                {
                    if (seg == 1 && i == 0)
                        continue;
                    float u = i / (float)n;
                    float eu = AsiKeyInterpTCB.EaseU(ks[seg], ks[seg + 1], u);
                    bool has0 = seg == 1;
                    bool has3 = seg == 0;
                    float p0 = has0 ? v[0] : v[seg];
                    float p3 = has3 ? v[2] : v[seg + 1];
                    float y = AsiKeyInterpTCB.Seg(p0, v[seg], v[seg + 1], p3, has0, has3, ks[seg], ks[seg + 1], 1f, 1f, 1f, eu);
                    float x = (seg + u) * 0.5f;
                    pts[idx++] = new Vector3(r.x + 8f + x * (r.width - 16f), r.yMax - 10f - y * (r.height - 24f), 0f);
                }
            }
            Handles.BeginGUI();
            Handles.color = new Color(1f, 0.85f, 0.3f);
            Handles.DrawAAPolyLine(2f, pts);
            Handles.color = Color.white;
            for (int k = 0; k < 3; k++)
            {
                Vector2 c = new Vector2(r.x + 8f + k * 0.5f * (r.width - 16f), r.yMax - 10f - v[k] * (r.height - 24f));
                EditorGUI.DrawRect(new Rect(c.x - 3f, c.y - 3f, 6f, 6f), k == 1 ? new Color(1f, 0.4f, 0.3f) : Color.gray);
            }
            Handles.EndGUI();
        }

        private static void DrawTangents(AsiKeyTrack tr, AsiKey key, int layer)
        {
            if (key.tan == null || key.tan.Length == 0)
                return;
            AsiTangent t0 = key.tan[0];
            EditorGUI.BeginChangeCheck();
            AsiTangentType tin = (AsiTangentType)EditorGUILayout.Popup("入切线", (int)t0.inType, AsiKeyLabels.Tangent);
            AsiTangentType tout = (AsiTangentType)EditorGUILayout.Popup("出切线", (int)t0.outType, AsiKeyLabels.Tangent);
            bool weighted = EditorGUILayout.Toggle("权重切线", t0.weighted);
            bool broken = EditorGUILayout.Toggle("断开切线", t0.broken);
            if (EditorGUI.EndChangeCheck())
            {
                Mutate("切线", () =>
                {
                    for (int c = 0; c < key.tan.Length; c++)
                    {
                        AsiTangent t = key.tan[c];
                        if (tin != t0.inType) t.inType = tin == AsiTangentType.Stepped ? AsiTangentType.Flat : tin;
                        if (tout != t0.outType) t.outType = tout;
                        t.weighted = weighted;
                        t.broken = broken;
                        key.tan[c] = t;
                    }
                    AsiKeyEdit.RecomputeTangents(_project, tr);
                });
            }
            EditorGUILayout.HelpBox("逐通道调整切线请用曲线编辑器。", MessageType.None);
        }

        private static void DrawChainKey(AsiKeySession s, AsiKeyTrack tr, AsiKey key)
        {
            AsiKeyChain chain = _project.chains[tr.target];
            EditorGUILayout.Space(4f);
            GUILayout.Label("IK 关键帧", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AsiKeyType type = (AsiKeyType)EditorGUILayout.Popup("类型", (int)key.type, AsiKeyLabels.KeyType);
            if (EditorGUI.EndChangeCheck() && type != key.type)
            {
                s.SetKeyType(tr, key, type);
                return;
            }

            EditorGUI.BeginChangeCheck();
            float blend = EditorGUILayout.Slider("IK Blend", key.ikBlend, 0f, 1f);
            bool join = key.joinToPrev;
            if (key.type == AsiKeyType.Planted)
                EditorGUILayout.LabelField("连接上一帧", key.joinToPrev ? "是（跟随上一帧）" : "无上一帧");
            else
                join = EditorGUILayout.Toggle(new GUIContent("连接上一帧", "Join to Prev：与前一个 IK 关键帧共用轴心锚点（踩踏）"), key.joinToPrev);
            float swivel = EditorGUILayout.Slider(new GUIContent("膝/肘朝向", SwivelTip(chain)), key.swivel, -180f, 180f);
            int[] pivotValues;
            string[] pivots = PivotNames(_project, chain, out pivotValues);
            int pivot = pivotValues[EditorGUILayout.Popup("IK 轴心", PivotOption(pivotValues, key.pivot), pivots)];
            if (EditorGUI.EndChangeCheck())
            {
                Mutate("IK 参数", () =>
                {
                    key.ikBlend = blend;
                    if (key.type != AsiKeyType.Planted)
                        key.joinToPrev = join;
                    key.swivel = swivel;
                    key.pivot = pivot;
                    AsiKeyEdit.AfterEdit(_project, tr, 0, key);
                });
            }

            if (key.type == AsiKeyType.Sliding)
                DrawSlidingBinding(s, tr, key, false);
            else if (key.type == AsiKeyType.Planted)
                DrawPlantedBinding(s, tr, key);
            else
                DrawFreeBinding(s, tr, key);
        }

        /// <summary>滑动关键帧自己决定绑世界还是绑某个对象。拖动关键帧只改时间，相对位置和角度留在关键帧上。</summary>
        private static void DrawSlidingBinding(AsiKeySession s, AsiKeyTrack tr, AsiKey key, bool compact)
        {
            EditorGUILayout.Space(2f);
            bool pickingThis = s.PickingBind && s.pickBindKey == key;
            int mode = key.ikSpace == AsiIKSpace.Object || pickingThis ? 1 : 0;
            int next = GUILayout.Toolbar(mode, new[] { "绑世界", "绑对象" });
            if (next != mode)
            {
                if (next == 0)
                {
                    if (pickingThis)
                        s.EndPick();
                    if (key.ikSpace == AsiIKSpace.Object)
                        s.ConvertKeySpace(tr, key, AsiIKSpace.World, -1, "");
                }
                else
                    s.BeginBindPick(tr, key);
                return;
            }
            if (pickingThis)
                EditorGUILayout.HelpBox("在 Scene 里点骨骼控制器或场景物体。Esc 或右键取消。", MessageType.Info);
            else if (key.ikSpace == AsiIKSpace.Object)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("当前：" + BindingName(s, key), EditorStyles.miniLabel);
                if (GUILayout.Button(new GUIContent("点选", "在 Scene 里点另一根骨骼或场景物体，作为这条滑动关键帧的绑定"), EditorStyles.miniButton, GUILayout.Width(40f)))
                    s.BeginBindPick(tr, key);
                EditorGUILayout.EndHorizontal();
                Transform cur = BindingTransform(s, key);
                if (cur == null)
                    EditorGUILayout.HelpBox("找不到绑定对象，目前按世界空间计算。", MessageType.Warning);
            }
            if (!compact)
                EditorGUILayout.HelpBox("绑对象时在 Scene 里点选，可以点角色骨骼。拖动关键帧只改时间，相对位置和角度不变。", MessageType.None);
        }

        /// <summary>固定关键帧不选绑定对象，显示它跟随的上一帧决定了什么。</summary>
        private static void DrawPlantedBinding(AsiKeySession s, AsiKeyTrack tr, AsiKey key)
        {
            int idx = tr.keys.IndexOf(key);
            string text = idx <= 0
                ? "世界（没有上一帧，按滑动工作）"
                : "跟随上一帧：" + BindingName(s, tr.keys[idx - 1]);
            EditorGUILayout.LabelField(new GUIContent("绑定", "固定关键帧不能选绑定对象，空间永远跟上一帧"), text);
        }

        private static void DrawFreeBinding(AsiKeySession s, AsiKeyTrack tr, AsiKey key)
        {
            EditorGUI.BeginChangeCheck();
            AsiIKSpace space = (AsiIKSpace)EditorGUILayout.Popup(new GUIContent("空间", "切换空间时自动换算目标，该帧姿势不变"), (int)key.ikSpace, AsiKeyLabels.Space);
            if (EditorGUI.EndChangeCheck() && space != key.ikSpace)
            {
                if (space == AsiIKSpace.Object && key.ikBone < 0 && string.IsNullOrEmpty(key.ikObject))
                {
                    s.status = "先把类型改成滑动，再指定绑定对象";
                    return;
                }
                s.ConvertKeySpace(tr, key, space, key.ikBone, key.ikObject);
                return;
            }
            if (key.ikSpace == AsiIKSpace.Object)
            {
                Transform cur = BindingTransform(s, key);
                EditorGUI.BeginChangeCheck();
                Transform t = (Transform)EditorGUILayout.ObjectField("IK 物体", cur, typeof(Transform), true);
                if (EditorGUI.EndChangeCheck() && t != null)
                {
                    int bone = s.BoneOfTransform(t);
                    s.ConvertKeySpace(tr, key, AsiIKSpace.Object, bone, bone < 0 ? AsiKeyUtil.EncodeObjectRef(t) : "");
                }
                if (cur == null)
                    EditorGUILayout.HelpBox("找不到 IK 物体，目标按角色根空间处理。", MessageType.Warning);
            }
        }

        private static Transform BindingTransform(AsiKeySession s, AsiKey key)
        {
            if (key.ikBone >= 0 && key.ikBone < s.controls.Length)
                return s.controls[key.ikBone];
            return s.ctx.Resolve(key.ikObject);
        }

        /// <summary>当前时间上生效的那条链关键帧（正在这一帧上，或它所在段的左帧）。</summary>
        public static bool GoverningChainKey(AsiKeyProject p, int chain, float frame, out AsiKeyTrack tr, out AsiKey key)
        {
            tr = p.FindTrack(0, AsiTrackKind.Chain, chain);
            key = null;
            if (tr == null || tr.keys.Count == 0)
                return false;
            int exact = AsiKeyTrackEval.FindKeyIndex(tr.keys, frame);
            if (exact >= 0)
            {
                key = tr.keys[exact];
                return true;
            }
            int i;
            float u;
            int loc = AsiKeyTrackEval.Locate(tr.keys, frame, out i, out u);
            if (loc == AsiKeyTrackEval.Before)
                key = tr.keys[0];
            else if (loc == AsiKeyTrackEval.After)
                key = tr.keys[tr.keys.Count - 1];
            else
                key = tr.keys[i];
            return key != null;
        }

        /// <summary>单选一根骨骼时，Scene 第二排用的说明：关键帧类型，以及绑世界还是哪个对象。</summary>
        public static string SingleBoneCaption(AsiKeySession s, int bone)
        {
            AsiKeyProject p = s.project;
            if (bone < 0 || bone >= p.bones.Count)
                return "";
            string name = p.bones[bone].name;
            int chain = p.ChainOf(bone);
            if (chain < 0)
                chain = AsiKeyFingers.TipChain(p, bone);
            if (chain >= 0)
            {
                AsiKeyTrack tr;
                AsiKey key;
                if (!GoverningChainKey(p, chain, s.frame, out tr, out key))
                    return name + "  没有关键帧";
                string type = AsiKeyLabels.KeyType[(int)key.type] + "关键帧";
                if (key.type == AsiKeyType.Sliding || key.type == AsiKeyType.Planted)
                    return name + "  " + type + "  绑定" + BindingName(s, key);
                return name + "  " + type;
            }
            if (p.bones[bone].isCom)
                return name + (ComKeyed(p, bone, s.frame) ? "  质心关键帧" : "  当前帧没有关键帧");
            AsiKeyTrack boneTrack = p.FindTrack(p.activeLayer, AsiTrackKind.Bone, bone);
            int idx = boneTrack == null ? -1 : AsiKeyTrackEval.FindKeyIndex(boneTrack.keys, s.frame);
            return name + (idx >= 0 ? "  注册关键帧" : "  当前帧没有关键帧");
        }

        private static bool ComKeyed(AsiKeyProject p, int bone, float frame)
        {
            AsiTrackKind[] kinds = { AsiTrackKind.ComHorizontal, AsiTrackKind.ComVertical, AsiTrackKind.ComRotation };
            for (int i = 0; i < kinds.Length; i++)
            {
                AsiKeyTrack tr = p.FindTrack(0, kinds[i], bone);
                if (tr != null && AsiKeyTrackEval.FindKeyIndex(tr.keys, frame) >= 0)
                    return true;
            }
            return false;
        }

        private static string BindingName(AsiKeySession s, AsiKey key)
        {
            if (key.ikSpace == AsiIKSpace.Body)
                return "身体";
            if (key.ikSpace != AsiIKSpace.Object)
                return "世界";
            if (key.ikBone >= 0 && key.ikBone < s.project.bones.Count)
                return s.project.BoneLabel(key.ikBone);
            Transform t = s.ctx.Resolve(key.ikObject);
            return t != null ? t.name : "对象（丢失）";
        }
    }
}
