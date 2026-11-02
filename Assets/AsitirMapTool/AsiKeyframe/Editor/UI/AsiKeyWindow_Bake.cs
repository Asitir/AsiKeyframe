using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>烘焙与导入页签。</summary>
    public partial class AsiKeyWindow
    {
        [SerializeField] private AnimationClip _importClip;
        [SerializeField] private int _importUnit;
        [SerializeField] private bool _importReplace = true;
        [SerializeField] private float _importRotTol = 0.5f;
        [SerializeField] private float _importPosTol = 0.002f;
        [SerializeField] private AsiInterpMode _importMode = AsiInterpMode.TCB;
        private AnimationClip _lastBaked;

        private void DrawBakeTab(AsiKeySession s)
        {
            EditorGUILayout.LabelField("烘焙", Header);
            EditorGUI.BeginChangeCheck();
            string clipName = EditorGUILayout.TextField("片段名", _project.clipName);
            EditorGUILayout.BeginHorizontal();
            string folder = EditorGUILayout.TextField("保存目录", _project.bakeFolder);
            if (GUILayout.Button("…", GUILayout.Width(24f)))
            {
                string abs = EditorUtility.OpenFolderPanel("选择保存目录", string.IsNullOrEmpty(folder) ? "Assets" : folder, "");
                if (!string.IsNullOrEmpty(abs))
                {
                    string rel = AsiKeyUtil.ToAssetPath(abs);
                    if (!string.IsNullOrEmpty(rel))
                        folder = rel;
                    else
                        s.status = "目录必须在工程的 Assets 下";
                }
            }
            EditorGUILayout.EndHorizontal();
            bool loop = EditorGUILayout.Toggle("循环", _project.bakeLoop);
            bool compress = EditorGUILayout.Toggle(new GUIContent("压缩", "用贝塞尔压缩精简烘焙结果"), _project.bakeCompress);
            if (EditorGUI.EndChangeCheck())
            {
                Mutate("烘焙设置", () =>
                {
                    _project.clipName = clipName;
                    _project.bakeFolder = folder;
                    _project.bakeLoop = loop;
                    _project.bakeCompress = compress;
                });
            }
            EditorGUILayout.LabelField("范围", _project.startFrame + " - " + _project.endFrame + " 帧，" + _project.frameRate + " fps（在时间轴上方修改）", EditorStyles.miniLabel);

            DrawBakeUnits(s);
            if (_lastBaked != null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.ObjectField("上次烘焙", _lastBaked, typeof(AnimationClip), false);
                if (GUILayout.Button("定位", GUILayout.Width(40f)))
                    EditorGUIUtility.PingObject(_lastBaked);
                EditorGUILayout.EndHorizontal();
            }

            if (_project.AnyHumanoid)
            {
                EditorGUILayout.Space(4f);
                EditorGUI.BeginChangeCheck();
                bool hp = EditorGUILayout.ToggleLeft(new GUIContent("Humanoid 实际效果预览", "预览时让姿势经过一次肌肉往返，显示 Humanoid 限位与重定向后的真实效果。控制器仍显示原始姿势。"), s.humanPreview);
                if (EditorGUI.EndChangeCheck())
                {
                    s.SuspendPreview();
                    s.humanPreview = hp;
                    s.Refresh();
                }
                if (s.humanPreview)
                    EditorGUILayout.HelpBox("模型显示的是 Humanoid 片段的效果：超出肌肉限位的旋转会被截断，扭转会重新分配。", MessageType.None);
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("导入已有片段", Header);
            List<int> chars = CharacterUnits();
            if (chars.Count == 0)
            {
                EditorGUILayout.HelpBox("工程里没有角色单位，不能导入片段。", MessageType.Info);
                return;
            }
            if (!chars.Contains(_importUnit))
                _importUnit = chars[0];
            string[] names = new string[chars.Count];
            for (int k = 0; k < names.Length; k++)
                names[k] = _project.UnitName(chars[k]);
            _importUnit = chars[EditorGUILayout.Popup("导入到单位", chars.IndexOf(_importUnit), names)];
            _importClip = (AnimationClip)EditorGUILayout.ObjectField("片段", _importClip, typeof(AnimationClip), false);
            _importReplace = EditorGUILayout.Popup("方式", _importReplace ? 0 : 1, new[] { "替换该单位基础层全部动画", "从当前帧开始覆盖" }) == 0;
            _importMode = (AsiInterpMode)EditorGUILayout.Popup("新轨道插值", (int)_importMode, AsiKeyLabels.Interp);
            _importRotTol = Mathf.Max(0.01f, EditorGUILayout.FloatField(new GUIContent("旋转误差（度）", "精简关键帧时允许的最大旋转误差"), _importRotTol));
            _importPosTol = Mathf.Max(0.0001f, EditorGUILayout.FloatField(new GUIContent("位移误差（米）", "精简关键帧时允许的最大位移误差。关节旋转误差传到子骨骼末端造成的位移也按它限制"), _importPosTol));
            using (new EditorGUI.DisabledScope(_importClip == null))
            {
                if (GUILayout.Button("导入", GUILayout.Height(24f)))
                    RunImport(s);
            }
            EditorGUILayout.HelpBox("导入只写基础层。质心取盆骨的运动，IK 链写成自由关键帧，之后可以把需要踩住的帧改成固定/滑动关键帧。", MessageType.None);
        }

        private List<int> CharacterUnits()
        {
            List<int> list = new List<int>();
            for (int u = 0; u < _project.units.Count; u++)
            {
                if (_project.units[u].IsCharacter)
                    list.Add(u);
            }
            return list;
        }

        /// <summary>每个角色单位一行（勾选烘焙、片段名、两种烘焙按钮），物体单位按「各自 / 合并」烘焙。</summary>
        private void DrawBakeUnits(AsiKeySession s)
        {
            bool anyObject = false;
            for (int u = 0; u < _project.units.Count; u++)
            {
                AsiKeyUnit unit = _project.units[u];
                if (!unit.IsCharacter)
                {
                    anyObject = true;
                    continue;
                }
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(_project.UnitName(u) + (unit.kind == AsiUnitKind.Humanoid ? "（人形）" : "（骨骼）"), EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                EditorGUI.BeginChangeCheck();
                bool bake = GUILayout.Toggle(unit.bake, new GUIContent("批量烘焙", "「全部烘焙」时包含该单位"), GUILayout.Width(70f));
                bool bh = unit.bakeHumanoid;
                if (unit.kind == AsiUnitKind.Humanoid)
                    bh = GUILayout.Toggle(unit.bakeHumanoid, new GUIContent("批量出 Humanoid", "「全部烘焙」时出 Humanoid Clip，否则出专属骨骼 Clip"), GUILayout.Width(110f));
                if (EditorGUI.EndChangeCheck())
                {
                    Mutate("烘焙设置", () =>
                    {
                        unit.bake = bake;
                        unit.bakeHumanoid = bh;
                    });
                }
                EditorGUILayout.EndHorizontal();
                EditorGUI.BeginChangeCheck();
                string cn = EditorGUILayout.TextField(new GUIContent("片段名", "空时用「" + AsiKeyBaker.ClipNameOf(_project, u) + "」"), unit.clipName);
                if (EditorGUI.EndChangeCheck())
                    Mutate("烘焙设置", () => unit.clipName = cn);
                EditorGUILayout.BeginHorizontal();
                int uu = u;
                if (GUILayout.Button(new GUIContent("专属骨骼 Clip", "Transform 曲线，只适用于这套骨骼（Generic）")))
                    RunBake(s, uu, false);
                using (new EditorGUI.DisabledScope(unit.kind != AsiUnitKind.Humanoid))
                {
                    if (GUILayout.Button(new GUIContent("Humanoid Clip", "肌肉曲线，可用于任何 Humanoid 角色；额外骨骼仍写 Transform 曲线")))
                        RunBake(s, uu, true);
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            if (anyObject)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUILayout.Label("物体单位", EditorStyles.boldLabel);
                for (int u = 0; u < _project.units.Count; u++)
                {
                    AsiKeyUnit unit = _project.units[u];
                    if (unit.IsCharacter)
                        continue;
                    EditorGUI.BeginChangeCheck();
                    bool bake = EditorGUILayout.ToggleLeft(_project.UnitName(u) + "　→ " + AsiKeyBaker.ClipNameOf(_project, u), unit.bake);
                    if (EditorGUI.EndChangeCheck())
                        Mutate("烘焙设置", () => unit.bake = bake);
                }
                EditorGUI.BeginChangeCheck();
                int mode = EditorGUILayout.Popup(new GUIContent("方式", "各自：每个物体一个 Clip，路径相对物体本身；合并：一个 Generic Clip，路径相对工程父级，挂在工程父级的 Animator 上播放"),
                    (int)_project.objectBakeMode, new[] { "各自一个 Clip", "合并到工程父级的一个 Clip" });
                string merged = _project.mergedClipName;
                if (mode == (int)AsiObjectBakeMode.Merged)
                    merged = EditorGUILayout.TextField("合并片段名", _project.mergedClipName);
                if (EditorGUI.EndChangeCheck())
                {
                    Mutate("烘焙设置", () =>
                    {
                        _project.objectBakeMode = (AsiObjectBakeMode)mode;
                        _project.mergedClipName = merged;
                    });
                }
                if (GUILayout.Button("烘焙物体"))
                    RunBakeObjects(s);
                EditorGUILayout.EndVertical();
            }
            if (_project.units.Count > 1 && GUILayout.Button(new GUIContent("全部烘焙", "勾了「批量烘焙」的角色（人形出 Humanoid Clip，其它出专属骨骼 Clip）加上物体"), GUILayout.Height(26f)))
                RunBakeAll(s);
        }

        private bool ConfirmOverwrite(string path)
        {
            return AssetDatabase.LoadAssetAtPath<Object>(path) == null
                || EditorUtility.DisplayDialog("K帧工具", path + " 已存在，覆盖？（保留 GUID，引用不会丢）", "覆盖", "取消");
        }

        private void BakeFailed(AsiKeySession s, string err)
        {
            s.status = "烘焙失败：" + err;
            EditorUtility.DisplayDialog("K帧工具", s.status, "好");
        }

        private void RunBake(AsiKeySession s, int u, bool humanoid)
        {
            string path = AsiKeyBaker.DefaultPath(_project, u, humanoid);
            if (!ConfirmOverwrite(path))
                return;
            AnimationClip clip;
            string err = AsiKeyBaker.BakeUnit(s, u, humanoid, path, out clip);
            if (!string.IsNullOrEmpty(err))
            {
                BakeFailed(s, err);
                return;
            }
            _lastBaked = clip;
            s.status = "已烘焙 " + path + AsiKeyBaker.LastNote;
            EditorGUIUtility.PingObject(clip);
            GUIUtility.ExitGUI();
        }

        private void RunBakeObjects(AsiKeySession s)
        {
            List<AnimationClip> clips = new List<AnimationClip>();
            string err = AsiKeyBaker.BakeObjects(s, _project.objectBakeMode == AsiObjectBakeMode.Merged, clips);
            if (!string.IsNullOrEmpty(err))
            {
                BakeFailed(s, err);
                return;
            }
            _lastBaked = clips.Count > 0 ? clips[clips.Count - 1] : null;
            s.status = "已烘焙 " + clips.Count + " 个物体片段";
            if (_lastBaked != null)
                EditorGUIUtility.PingObject(_lastBaked);
            GUIUtility.ExitGUI();
        }

        private void RunBakeAll(AsiKeySession s)
        {
            if (!EditorUtility.DisplayDialog("K帧工具", "烘焙全部勾选的单位，已存在的片段会被覆盖（保留 GUID）。继续？", "烘焙", "取消"))
                return;
            int n = 0;
            for (int u = 0; u < _project.units.Count; u++)
            {
                AsiKeyUnit unit = _project.units[u];
                if (!unit.IsCharacter || !unit.bake)
                    continue;
                bool humanoid = unit.kind == AsiUnitKind.Humanoid && unit.bakeHumanoid;
                AnimationClip clip;
                string err = AsiKeyBaker.BakeUnit(s, u, humanoid, AsiKeyBaker.DefaultPath(_project, u, humanoid), out clip);
                if (!string.IsNullOrEmpty(err))
                {
                    BakeFailed(s, _project.UnitName(u) + "：" + err);
                    return;
                }
                _lastBaked = clip;
                n++;
            }
            List<AnimationClip> clips = new List<AnimationClip>();
            bool anyObject = false;
            for (int u = 0; u < _project.units.Count; u++)
                anyObject |= !_project.units[u].IsCharacter && _project.units[u].bake;
            if (anyObject)
            {
                string err = AsiKeyBaker.BakeObjects(s, _project.objectBakeMode == AsiObjectBakeMode.Merged, clips);
                if (!string.IsNullOrEmpty(err))
                {
                    BakeFailed(s, err);
                    return;
                }
                if (clips.Count > 0)
                    _lastBaked = clips[clips.Count - 1];
            }
            s.status = "已烘焙 " + (n + clips.Count) + " 个片段";
            GUIUtility.ExitGUI();
        }

        private void RunImport(AsiKeySession s)
        {
            if (_importReplace && !EditorUtility.DisplayDialog("K帧工具", "替换会清空「" + _project.UnitName(_importUnit) + "」基础层现有的全部关键帧（叠加层和其它单位保留）。继续？", "导入", "取消"))
                return;
            AsiKeyClipImporter.Options o;
            o.replace = _importReplace;
            o.at = Mathf.RoundToInt(s.frame);
            o.rotTolerance = _importRotTol;
            o.posTolerance = _importPosTol;
            o.mode = _importMode;
            AsiKeyPanels.ClearKeySelection();
            string err = AsiKeyClipImporter.Import(s, _importClip, o, _importUnit);
            if (!string.IsNullOrEmpty(err))
            {
                s.status = "导入失败：" + err;
                EditorUtility.DisplayDialog("K帧工具", s.status, "好");
            }
            GUIUtility.ExitGUI();
        }
    }
}
