using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 姿势库窗口：按分类存放全身 / 局部姿势（带缩略图），复制当前角色的姿势，粘贴时可选质心的水平 / 垂直 / 旋转、可镜像（取对侧骨骼）。
    /// 单击选中，双击粘贴，右键更多操作。
    /// </summary>
    public class AsiKeyPoseWindow : EditorWindow
    {
        private const string PrefCategory = "AsiKey.PoseLib.Category";
        private const string PrefMirror = "AsiKey.PoseLib.Mirror";
        private const string PrefComH = "AsiKey.PoseLib.ComH";
        private const string PrefComV = "AsiKey.PoseLib.ComV";
        private const string PrefComR = "AsiKey.PoseLib.ComR";
        private const string PrefThumb = "AsiKey.PoseLib.Thumb";

        private readonly Dictionary<AsiKeyPoseEntry, Texture2D> _thumbs = new Dictionary<AsiKeyPoseEntry, Texture2D>();
        private Vector2 _scroll;
        private AsiKeyPoseEntry _selected;
        private bool _renamingCategory;
        private string _categoryName = "";

        [MenuItem("AsitirTool/K帧姿势库", false, 21)]
        public static void Open()
        {
            AsiKeyPoseWindow w = GetWindow<AsiKeyPoseWindow>();
            w.titleContent = new GUIContent("K帧姿势库");
            w.minSize = new Vector2(320f, 260f);
            w.Show();
        }

        public static bool Mirror
        {
            get { return EditorPrefs.GetBool(PrefMirror, false); }
            set { EditorPrefs.SetBool(PrefMirror, value); }
        }

        public static bool ComH
        {
            get { return EditorPrefs.GetBool(PrefComH, true); }
            set { EditorPrefs.SetBool(PrefComH, value); }
        }

        public static bool ComV
        {
            get { return EditorPrefs.GetBool(PrefComV, true); }
            set { EditorPrefs.SetBool(PrefComV, value); }
        }

        public static bool ComR
        {
            get { return EditorPrefs.GetBool(PrefComR, true); }
            set { EditorPrefs.SetBool(PrefComR, value); }
        }

        /// <summary>当前分类下标（按分类名记在 EditorPrefs，找不到时为 0）。</summary>
        public static int CurrentCategory(AsiKeyPoseLibrary lib)
        {
            int k = lib.CategoryIndex(EditorPrefs.GetString(PrefCategory, AsiKeyPoseLibrary.DefaultCategory));
            return k >= 0 ? k : 0;
        }

        private static void SetCategory(AsiKeyPoseLibrary lib, int index)
        {
            EditorPrefs.SetString(PrefCategory, lib.Category(index).name);
        }

        /// <summary>把当前角色的全身 / 选中骨骼的姿势存进当前分类，返回新条目（失败为 null，原因在 s.status）。</summary>
        public static AsiKeyPoseEntry CaptureToLibrary(AsiKeySession s, bool fullBody)
        {
            if (s == null || !s.Valid)
                return null;
            AsiKeyPoseEntry e = s.CapturePose(fullBody);
            if (e != null)
            {
                AsiKeyPoseLibrary lib = AsiKeyPoseLibrary.Instance;
                AsiKeyPoseCategory cat = lib.Category(CurrentCategory(lib));
                Undo.RecordObject(lib, "复制姿势到姿势库");
                e.name = UniqueName(cat, fullBody ? "全身姿势" : "局部姿势");
                cat.poses.Add(e);
                lib.Save();
                s.status = "已存入姿势库「" + cat.name + "」：" + e.name;
                foreach (AsiKeyPoseWindow w in Resources.FindObjectsOfTypeAll<AsiKeyPoseWindow>())
                {
                    w._selected = e;
                    w.Repaint();
                }
            }
            AsiKeySession.RaiseRepaint();
            SceneView.RepaintAll();
            return e;
        }

        public static bool Paste(AsiKeySession s, AsiKeyPoseEntry e, bool mirror)
        {
            if (s == null || !s.Valid || e == null)
                return false;
            bool ok = s.PasteLibraryPose(e, mirror, ComH, ComV, ComR);
            AsiKeySession.RaiseRepaint();
            SceneView.RepaintAll();
            return ok;
        }

        private static string UniqueName(AsiKeyPoseCategory cat, string prefix)
        {
            for (int k = 1; ; k++)
            {
                string n = prefix + " " + k;
                bool used = false;
                for (int j = 0; j < cat.poses.Count && !used; j++)
                    used = cat.poses[j].name == n;
                if (!used)
                    return n;
            }
        }

        private void OnDisable()
        {
            foreach (Texture2D t in _thumbs.Values)
            {
                if (t != null)
                    DestroyImmediate(t);
            }
            _thumbs.Clear();
        }

        private void OnSelectionChange()
        {
            Repaint();
        }

        private Texture2D Thumb(AsiKeyPoseEntry e)
        {
            Texture2D t;
            if (_thumbs.TryGetValue(e, out t) && t != null)
                return t;
            if (e.thumbnail == null || e.thumbnail.Length == 0)
                return null;
            t = new Texture2D(2, 2, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            t.LoadImage(e.thumbnail);
            _thumbs[e] = t;
            return t;
        }

        private void OnGUI()
        {
            AsiKeyPoseLibrary lib = AsiKeyPoseLibrary.Instance;
            AsiKeySession s = AsiKeySession.Current;
            bool live = s != null && s.Valid;
            int ci = CurrentCategory(lib);
            AsiKeyPoseCategory cat = lib.Category(ci);
            if (_selected != null && !cat.poses.Contains(_selected))
                _selected = null;

            DrawCategoryBar(lib, ci, cat);

            using (new EditorGUI.DisabledScope(!live))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("复制全身姿势", "当前角色（选中骨骼所在的角色）全部骨骼")))
                    CaptureToLibrary(s, true);
                if (GUILayout.Button(new GUIContent("复制选中骨骼（局部姿势）", "只记选中的骨骼，缩略图会拉近到这些骨骼")))
                    CaptureToLibrary(s, false);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("粘贴质心", GUILayout.Width(52f));
            ComH = GUILayout.Toggle(ComH, "水平", EditorStyles.miniButtonLeft, GUILayout.Width(44f));
            ComV = GUILayout.Toggle(ComV, "垂直", EditorStyles.miniButtonMid, GUILayout.Width(44f));
            ComR = GUILayout.Toggle(ComR, "旋转", EditorStyles.miniButtonRight, GUILayout.Width(44f));
            GUILayout.Space(10f);
            Mirror = GUILayout.Toggle(Mirror, new GUIContent("镜像（取对侧骨骼）", "每根骨骼粘贴对侧骨骼的姿态并左右镜像"), GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            float size = EditorPrefs.GetFloat(PrefThumb, 110f);
            float ns = GUILayout.HorizontalSlider(size, 70f, 200f, GUILayout.Width(70f));
            if (!Mathf.Approximately(ns, size))
                EditorPrefs.SetFloat(PrefThumb, ns);
            EditorGUILayout.EndHorizontal();

            DrawGrid(lib, cat, s, live, EditorPrefs.GetFloat(PrefThumb, 110f));
            DrawDetails(lib, cat, s, live);

            string status = live ? s.status : "没有打开 K帧工程（AsitirTool/K帧工具）";
            if (!string.IsNullOrEmpty(status))
                EditorGUILayout.HelpBox(status, MessageType.None);
        }

        private void DrawCategoryBar(AsiKeyPoseLibrary lib, int ci, AsiKeyPoseCategory cat)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (_renamingCategory)
            {
                _categoryName = EditorGUILayout.TextField(_categoryName, EditorStyles.toolbarTextField);
                if (GUILayout.Button("确定", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                {
                    string n = _categoryName.Trim();
                    if (n.Length > 0 && n != cat.name && lib.CategoryIndex(n) < 0)
                    {
                        Undo.RecordObject(lib, "重命名姿势分类");
                        cat.name = n;
                        lib.Save();
                        SetCategory(lib, ci);
                    }
                    _renamingCategory = false;
                    GUI.FocusControl(null);
                }
                if (GUILayout.Button("取消", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                    _renamingCategory = false;
                EditorGUILayout.EndHorizontal();
                return;
            }
            string[] names = new string[lib.categories.Count];
            for (int k = 0; k < names.Length; k++)
                names[k] = lib.categories[k].name + "（" + lib.categories[k].poses.Count + "）";
            int nc = EditorGUILayout.Popup(ci, names, EditorStyles.toolbarPopup, GUILayout.MinWidth(100f));
            if (nc != ci)
            {
                SetCategory(lib, nc);
                _selected = null;
            }
            if (GUILayout.Button(new GUIContent("新建分类"), EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
            {
                Undo.RecordObject(lib, "新建姿势分类");
                lib.categories.Add(new AsiKeyPoseCategory { name = lib.UniqueCategoryName("新分类") });
                lib.Save();
                SetCategory(lib, lib.categories.Count - 1);
                _categoryName = lib.categories[lib.categories.Count - 1].name;
                _renamingCategory = true;
            }
            if (GUILayout.Button("重命名", EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
            {
                _categoryName = cat.name;
                _renamingCategory = true;
            }
            using (new EditorGUI.DisabledScope(lib.categories.Count <= 1))
            {
                if (GUILayout.Button("删除分类", EditorStyles.toolbarButton, GUILayout.ExpandWidth(false))
                    && (cat.poses.Count == 0 || EditorUtility.DisplayDialog("删除姿势分类", "删除分类「" + cat.name + "」和其中的 " + cat.poses.Count + " 个姿势？", "删除", "取消")))
                {
                    Undo.RecordObject(lib, "删除姿势分类");
                    lib.categories.RemoveAt(ci);
                    lib.Save();
                    SetCategory(lib, Mathf.Max(0, ci - 1));
                    _selected = null;
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawGrid(AsiKeyPoseLibrary lib, AsiKeyPoseCategory cat, AsiKeySession s, bool live, float size)
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (cat.poses.Count == 0)
            {
                EditorGUILayout.HelpBox("这个分类还没有姿势。选中角色的骨骼后点上面的「复制全身姿势」或「复制选中骨骼」。", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }
            float cell = size + 8f;
            int cols = Mathf.Max(1, Mathf.FloorToInt((position.width - 20f) / cell));
            int rows = (cat.poses.Count + cols - 1) / cols;
            float labelH = 30f;
            Rect area = GUILayoutUtility.GetRect(cols * cell, rows * (cell + labelH));
            Event ev = Event.current;
            for (int k = 0; k < cat.poses.Count; k++)
            {
                AsiKeyPoseEntry e = cat.poses[k];
                Rect r = new Rect(area.x + (k % cols) * cell + 4f, area.y + (k / cols) * (cell + labelH) + 4f, size, size);
                Rect label = new Rect(r.x, r.yMax + 1f, size, labelH - 2f);
                bool sel = e == _selected;
                if (ev.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f), sel ? new Color(0.25f, 0.5f, 0.95f) : new Color(0.1f, 0.1f, 0.1f));
                    Texture2D t = Thumb(e);
                    if (t != null)
                        GUI.DrawTexture(r, t, ScaleMode.ScaleToFit);
                    else
                        EditorGUI.DrawRect(r, new Color(0.2f, 0.2f, 0.22f));
                    GUI.Label(new Rect(r.x + 3f, r.y + 2f, r.width, 16f), e.fullBody ? "全身" : "局部 " + e.CopiedCount, EditorStyles.whiteMiniLabel);
                    GUI.Label(label, e.name, EditorStyles.wordWrappedMiniLabel);
                }
                Rect hit = new Rect(r.x, r.y, r.width, r.height + labelH);
                if (ev.type == EventType.MouseDown && hit.Contains(ev.mousePosition))
                {
                    _selected = e;
                    if (ev.button == 0 && ev.clickCount == 2 && live)
                        Paste(s, e, Mirror);
                    else if (ev.button == 1)
                        EntryMenu(lib, cat, e, s, live).ShowAsContext();
                    GUI.FocusControl(null);
                    ev.Use();
                    Repaint();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private GenericMenu EntryMenu(AsiKeyPoseLibrary lib, AsiKeyPoseCategory cat, AsiKeyPoseEntry e, AsiKeySession s, bool live)
        {
            GenericMenu m = new GenericMenu();
            AsiKeyPanels.AddMenu(m, "粘贴", live, () => Paste(s, e, false));
            AsiKeyPanels.AddMenu(m, "镜像粘贴（取对侧骨骼）", live, () => Paste(s, e, true));
            m.AddSeparator("");
            for (int k = 0; k < lib.categories.Count; k++)
            {
                AsiKeyPoseCategory to = lib.categories[k];
                AsiKeyPanels.AddMenu(m, "移动到分类/" + to.name.Replace('/', '∕'), to != cat, () =>
                {
                    Undo.RecordObject(lib, "移动姿势");
                    cat.poses.Remove(e);
                    to.poses.Add(e);
                    lib.Save();
                    Repaint();
                });
            }
            int idx = cat.poses.IndexOf(e);
            AsiKeyPanels.AddMenu(m, "前移", idx > 0, () => MoveEntry(lib, cat, e, -1));
            AsiKeyPanels.AddMenu(m, "后移", idx >= 0 && idx < cat.poses.Count - 1, () => MoveEntry(lib, cat, e, 1));
            m.AddSeparator("");
            AsiKeyPanels.AddMenu(m, "删除", true, () => DeleteEntry(lib, cat, e));
            return m;
        }

        private void MoveEntry(AsiKeyPoseLibrary lib, AsiKeyPoseCategory cat, AsiKeyPoseEntry e, int dir)
        {
            int idx = cat.poses.IndexOf(e);
            int to = idx + dir;
            if (idx < 0 || to < 0 || to >= cat.poses.Count)
                return;
            Undo.RecordObject(lib, "调整姿势顺序");
            cat.poses.RemoveAt(idx);
            cat.poses.Insert(to, e);
            lib.Save();
            Repaint();
        }

        private void DeleteEntry(AsiKeyPoseLibrary lib, AsiKeyPoseCategory cat, AsiKeyPoseEntry e)
        {
            Undo.RecordObject(lib, "删除姿势");
            cat.poses.Remove(e);
            lib.Save();
            if (_selected == e)
                _selected = null;
            Repaint();
        }

        private void DrawDetails(AsiKeyPoseLibrary lib, AsiKeyPoseCategory cat, AsiKeySession s, bool live)
        {
            if (_selected == null)
                return;
            AsiKeyPoseEntry e = _selected;
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            string n = EditorGUILayout.DelayedTextField(e.name);
            if (EditorGUI.EndChangeCheck() && n.Trim().Length > 0)
            {
                Undo.RecordObject(lib, "重命名姿势");
                e.name = n.Trim();
                lib.Save();
            }
            using (new EditorGUI.DisabledScope(!live))
            {
                if (GUILayout.Button("粘贴", GUILayout.Width(48f)))
                    Paste(s, e, false);
                if (GUILayout.Button("镜像粘贴", GUILayout.Width(64f)))
                    Paste(s, e, true);
            }
            if (GUILayout.Button("删除", GUILayout.Width(44f)))
                DeleteEntry(lib, cat, e);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField((e.fullBody ? "全身姿势" : "局部姿势（" + e.CopiedCount + " 根骨骼）") + "   来自「" + e.unitName + "」   " + e.created, EditorStyles.miniLabel);
        }
    }
}
