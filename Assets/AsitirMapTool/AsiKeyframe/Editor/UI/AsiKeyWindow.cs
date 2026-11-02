using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// K 帧工具主窗口：工程工具栏与骨骼、IK 链、外形、烘焙/导入页签。
    /// 时间轴、Motion、Key Info、层在 Scene 视图的面板里（AsiKeyOverlays）。
    /// 关掉窗口会结束 K 帧；最大化别的窗口时 Unity 会暂时销毁本窗口，这时会话保留，还原后重建的窗口接着用。
    /// </summary>
    public partial class AsiKeyWindow : EditorWindow
    {
        [SerializeField] private AsiKeyProject _project;
        [SerializeField] private float _frame;
        [SerializeField] private int _tab;
        [SerializeField] private int _unit;

        private Vector2 _scroll;

        private static readonly string[] Tabs = { "骨骼", "IK 链", "外形", "烘焙/导入", "单位", "约束" };

        [MenuItem("AsitirTool/K帧工具", false, 20)]
        public static void Open()
        {
            AsiKeyWindow w = GetWindow<AsiKeyWindow>();
            w.titleContent = new GUIContent("K帧工具");
            w.minSize = new Vector2(360f, 420f);
            w.Show();
        }

        private AsiKeySession S
        {
            get
            {
                AsiKeySession s = AsiKeySession.Current;
                return s != null && _project != null && s.project == _project ? s : null;
            }
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("K帧工具");
            AsiKeySession.Repaint += OnSessionRepaint;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            wantsMouseMove = false;
            if (_project == null && AsiKeySession.Current != null)
                _project = AsiKeySession.Current.project;
            if (_project != null && !ModeChanging)
                EditorApplication.delayCall += StartSessionDelayed;
        }

        private void OnDisable()
        {
            AsiKeySession.Repaint -= OnSessionRepaint;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            if (S != null)
                _frame = S.frame;
        }

        private void OnDestroy()
        {
            EditorApplication.delayCall -= StopIfNoWindow;
            EditorApplication.delayCall += StopIfNoWindow;
        }

        /// <summary>窗口真正关掉才结束会话。最大化时其它停靠窗口会被暂存销毁，还原时重建，这期间保留会话。</summary>
        private static void StopIfNoWindow()
        {
            if (AsiKeySession.Current == null || Resources.FindObjectsOfTypeAll<AsiKeyWindow>().Length > 0)
                return;
            foreach (EditorWindow w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (w != null && w.maximized)
                    return;
            }
            AsiKeySession.StopCurrent();
        }

        private void OnSessionRepaint()
        {
            Repaint();
        }

        private void OnUndoRedo()
        {
            Repaint();
        }

        /// <summary>正在进入或退出播放模式（会话在切换前结束，切换完成后再打开）。</summary>
        private static bool ModeChanging
        {
            get { return EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode; }
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (S != null)
                _frame = S.frame;
            if (_project != null && (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.EnteredEditMode))
            {
                EditorApplication.delayCall -= StartSessionDelayed;
                EditorApplication.delayCall += StartSessionDelayed;
            }
        }

        private void StartSessionDelayed()
        {
            if (this == null || _project == null || ModeChanging)
                return;
            StartSession();
        }

        private void StartSession()
        {
            if (_project == null)
                return;
            bool running = AsiKeySession.Current != null && AsiKeySession.Current.project == _project;
            AsiKeySession s = AsiKeySession.Start(_project);
            if (s.Valid && !running)
                s.GoTo(_frame);
            Repaint();
        }

        private void OpenProject(AsiKeyProject p)
        {
            if (p == _project)
                return;
            if (S != null)
                AsiKeySession.StopCurrent();
            _project = p;
            _frame = p != null ? p.startFrame : 0f;
            AsiKeyPanels.ClearKeySelection();
            if (p != null && !ModeChanging)
                StartSession();
        }

        // ---------------- 工程 ----------------

        private static Transform SelectedSceneTransform()
        {
            GameObject go = Selection.activeGameObject;
            if (go == null || EditorUtility.IsPersistent(go))
                return null;
            return go.transform;
        }

        /// <summary>
        /// 新建工程：选中物体作为工程父级。选中的是带 Animator 的角色时，可以改用它的父物体当工程父级并把它加为第一个单位。
        /// </summary>
        private void NewProject()
        {
            Transform sel = SelectedSceneTransform();
            if (sel == null)
            {
                EditorUtility.DisplayDialog("K帧工具", "先在场景里选中一个物体作为工程父级（它下面的角色、镜头、灯光、道具可以加为单位）。", "好");
                return;
            }
            Transform root = sel;
            Transform firstUnit = null;
            if (sel.GetComponent<Animator>() != null)
            {
                int pick = EditorUtility.DisplayDialogComplex("K帧工具",
                    "「" + sel.name + "」是带 Animator 的角色。\n可以用它的父物体" + (sel.parent != null ? "「" + sel.parent.name + "」" : "（没有父物体时用世界）") +
                    "作为工程父级，并把它加为第一个单位；也可以直接把它当成工程父级。",
                    "父物体为父级并加为单位", "取消", "它本身作为父级");
                if (pick == 1)
                    return;
                if (pick == 0)
                {
                    root = sel.parent;
                    firstUnit = sel;
                }
            }
            string baseName = root != null ? root.name : sel.name;
            string path = EditorUtility.SaveFilePanelInProject("新建 K帧工程", baseName + "_Key", "asset", "选择工程保存位置");
            if (string.IsNullOrEmpty(path))
                return;
            AsiKeyProject p = CreateInstance<AsiKeyProject>();
            SetRootFields(p, root);
            p.bakeFolder = Path.GetDirectoryName(path).Replace('\\', '/');
            p.mergedClipName = baseName + "_Objects";
            AssetDatabase.CreateAsset(p, path);
            AssetDatabase.SaveAssets();
            OpenProject(p);
            if (firstUnit != null && S != null)
            {
                string err = S.AddUnit(firstUnit, AsiKeyUnits.DetectKind(firstUnit));
                if (!string.IsNullOrEmpty(err))
                    S.status = err;
            }
            _tab = firstUnit != null ? 0 : 4;
            EditorGUIUtility.PingObject(p);
        }

        private static void SetRootFields(AsiKeyProject p, Transform root)
        {
            p.rootId = root != null ? AsiKeyUtil.GetId(root.gameObject) : "";
            p.rootPath = root != null ? AsiKeyUtil.ScenePath(root) : "";
            p.rootName = root != null ? root.name : "";
        }

        /// <summary>把工程父级换成选中物体（全部单位必须在它下面）。会话没起来时（父级丢失）直接改记录再重连。</summary>
        private void SetRootToSelection()
        {
            Transform t = SelectedSceneTransform();
            if (t == null || _project == null)
            {
                EditorUtility.DisplayDialog("K帧工具", "先在场景里选中要作为工程父级的物体。", "好");
                return;
            }
            AsiKeySession s = S;
            if (s != null && s.Valid)
            {
                string err = s.SetProjectRoot(t);
                if (!string.IsNullOrEmpty(err))
                    s.status = err;
                return;
            }
            Undo.RecordObject(_project, "设为工程父级");
            SetRootFields(_project, t);
            EditorUtility.SetDirty(_project);
            if (S != null)
                AsiKeySession.StopCurrent();
            StartSession();
        }

        private void SaveProject()
        {
            if (_project == null)
                return;
            EditorUtility.SetDirty(_project);
            AssetDatabase.SaveAssetIfDirty(_project);
            ShowNotification(new GUIContent("已保存"), 0.8f);
        }

        // ---------------- 绘制 ----------------

        private void OnGUI()
        {
            AsiKeySession s = S;
            if (s != null)
                _frame = s.frame;
            DrawToolbar(s);
            if (_project == null)
            {
                DrawWelcome();
                return;
            }
            if (s == null || !s.Valid)
            {
                DrawNotBound(s);
                return;
            }

            DrawTarget(s);
            _tab = GUILayout.Toolbar(Mathf.Clamp(_tab, 0, Tabs.Length - 1), Tabs);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            bool locked = AsiKeySession.StructureLocked && (_tab == 0 || _tab == 3 || _tab == 4);
            if (locked)
                EditorGUILayout.HelpBox(AsiKeySession.LockedMessage + "。", MessageType.Info);
            using (new EditorGUI.DisabledScope(locked))
            {
                switch (_tab)
                {
                    case 0: DrawBonesTab(s); break;
                    case 1: DrawChainsTab(s); break;
                    case 2: DrawShapesTab(s); break;
                    case 3: DrawBakeTab(s); break;
                    case 4: DrawUnitsTab(s); break;
                    case 5: DrawConstraintsTab(s); break;
                }
            }
            EditorGUILayout.EndScrollView();

            Rect status = GUILayoutUtility.GetRect(position.width, 18f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(status, new Color(0f, 0f, 0f, 0.2f));
            GUI.Label(new Rect(status.x + 6f, status.y + 1f, status.width - 12f, status.height), s.status, EditorStyles.miniLabel);
        }

        private void DrawToolbar(AsiKeySession s)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            using (new EditorGUI.DisabledScope(AsiKeySession.StructureLocked))
            {
                if (GUILayout.Button(new GUIContent("新建", "以场景里选中的物体为工程父级新建 K帧工程"), EditorStyles.toolbarButton, GUILayout.Width(40f)))
                    NewProject();
            }
            EditorGUI.BeginChangeCheck();
            AsiKeyProject p = (AsiKeyProject)EditorGUILayout.ObjectField(_project, typeof(AsiKeyProject), false, GUILayout.MinWidth(120f));
            if (EditorGUI.EndChangeCheck())
                OpenProject(p);
            using (new EditorGUI.DisabledScope(_project == null))
            {
                if (GUILayout.Button("保存", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                    SaveProject();
                using (new EditorGUI.DisabledScope(AsiKeySession.StructureLocked))
                {
                    if (GUILayout.Button(new GUIContent("设为父级", "把场景里选中的物体设为工程父级（全部单位必须在它下面）"), EditorStyles.toolbarButton, GUILayout.Width(60f)))
                        SetRootToSelection();
                }
            }
            using (new EditorGUI.DisabledScope(s == null || !s.Valid))
            {
                if (GUILayout.Button("曲线编辑器", EditorStyles.toolbarButton, GUILayout.Width(66f)))
                    AsiKeyCurveEditorWindow.Open();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTarget(AsiKeySession s)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("工程父级：" + (s.projectRoot != null ? s.projectRoot.name : "（世界）") + "　单位 " + _project.units.Count + " 个", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(s.projectRoot == null))
            {
                if (GUILayout.Button("选中父级", EditorStyles.miniButton, GUILayout.Width(60f)))
                    Selection.activeGameObject = s.projectRoot.gameObject;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("时间轴、Motion、Key Info、层在 Scene 视图的「K帧」面板里，Scene 顶部的 K帧 工具栏可以开关面板和显示项。\n" +
                "在 Scene 里选中控制器后：移动/旋转工具显示 K帧变换轴（仿 Max，环内拖动自由旋转、单击穿透选骨骼，最外圈屏幕旋转，工具栏「变换轴」可关）；" +
                "双击控制器选中它和全部子级（旋转时整条按相对轴一起转，移动只动它）；" +
                "右键单击打开快捷菜单，X 键切换参考坐标（局部/世界，跟 Unity 一致），Alt+右键单击切换变换中心、复制粘贴姿势；快捷键见工具栏「快捷键」（" +
                AsiKeyShortcuts.Binding(AsiKeyShortcuts.Help) + "）。\n关掉本窗口会结束 K 帧，可以把它停靠在别的窗口后面。", MessageType.None);
            if (EditorApplication.isPlaying)
                EditorGUILayout.HelpBox("播放模式：单位的 Animator 已暂停，模型由 K帧 接管，结束 K帧或退出播放后恢复。\n" +
                    "关键帧、约束、IK 链、外形的改动会保留到退出播放之后（记得保存工程）；这期间的撤销记录在退出播放时会被 Unity 清空。", MessageType.Info);
        }

        private void DrawWelcome()
        {
            EditorGUILayout.Space(20f);
            EditorGUILayout.HelpBox("1. 在场景里选中一个物体作为工程父级（选中角色时可以改用它的父物体，并把角色加为第一个单位）。\n" +
                "2. 点工具栏的「新建」，选择工程保存位置。\n3. 在「单位」页签把父级下的角色、镜头、灯光、道具加进工程，加进来的单位才能 K 帧。\n" +
                "编辑时工具会在场景里放一套隐藏的控制器（不显示在 Hierarchy、不存进场景，关闭工程后自动删除）；人形按 3ds Max Biped 结构生成，质心在盆骨与第一节腰椎之间。\n" +
                "也可以把已有的 K帧工程拖到工具栏的工程栏里打开。", MessageType.Info);
            Transform t = SelectedSceneTransform();
            using (new EditorGUI.DisabledScope(t == null))
            {
                if (GUILayout.Button(t != null ? "以 " + t.name + " 新建工程" : "以选中物体新建工程", GUILayout.Height(28f)))
                    NewProject();
            }
        }

        private void DrawNotBound(AsiKeySession s)
        {
            EditorGUILayout.Space(10f);
            string msg = s != null && !string.IsNullOrEmpty(s.status) ? s.status : "工程还没有绑定到场景。";
            EditorGUILayout.HelpBox(msg + "\n工程父级路径：" + (string.IsNullOrEmpty(_project.rootPath) ? "（世界）" : _project.rootPath), MessageType.Warning);
            if (GUILayout.Button("重新连接", GUILayout.Height(24f)))
                StartSession();
            using (new EditorGUI.DisabledScope(AsiKeySession.StructureLocked))
            {
                if (GUILayout.Button("把工程父级换成场景里选中的物体", GUILayout.Height(24f)))
                    SetRootToSelection();
            }
        }

        // ---------------- 小工具 ----------------

        private void Mutate(string undo, Action fn)
        {
            Undo.RecordObject(_project, undo);
            fn();
            _project.Touch();
            EditorUtility.SetDirty(_project);
            if (S != null)
                S.Refresh();
        }

        private static GUIStyle s_header;

        private static GUIStyle Header
        {
            get
            {
                if (s_header == null)
                {
                    s_header = new GUIStyle(EditorStyles.boldLabel);
                    s_header.fontSize = 12;
                }
                return s_header;
            }
        }
    }
}
