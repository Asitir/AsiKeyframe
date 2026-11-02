using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 一个打开中的 K 帧工程：绑定工程父级、各单位目标与场景控制器，求值、预览、检测拖动和打关键帧。
    /// 同一时间只有一个会话。
    /// </summary>
    public partial class AsiKeySession
    {
        public static AsiKeySession Current { get; private set; }
        public static event Action Repaint;

        public AsiKeyProject project;
        /// <summary>工程父级；为空时姿势空间是世界空间。</summary>
        public Transform projectRoot;
        /// <summary>各单位的目标（角色是 Animator 所在物体，物体单位是物体本身）。找不到时为空。</summary>
        public Transform[] unitTargets = new Transform[0];
        public Animator[] unitAnimators = new Animator[0];
        /// <summary>控制器根下各单位的子根「[U] 单位名」。</summary>
        public Transform[] unitRoots = new Transform[0];
        public Transform rigRoot;
        public Transform[] controls = new Transform[0];
        public Transform[] modelBones = new Transform[0];
        public readonly AsiKeyPose pose = new AsiKeyPose();
        public readonly AsiKeyContext ctx = new AsiKeyContext();

        public float frame;
        public bool autoKey;
        public bool playing;
        public bool loop = true;
        public bool comKeyH = true;
        public bool comKeyV = true;
        public bool comKeyR = true;
        public bool humanPreview;
        public string status = "";

        private AnimationModeDriver _driver;
        private readonly AsiKeyPreview _preview = new AsiKeyPreview();
        private AsiKeyHumanBridge[] _humans = new AsiKeyHumanBridge[0];
        private Quaternion[] _writtenRot = new Quaternion[0];
        private Vector3[] _writtenPos = new Vector3[0];
        private readonly Dictionary<Transform, int> _controlIndex = new Dictionary<Transform, int>();
        private readonly Dictionary<Transform, int> _modelIndex = new Dictionary<Transform, int>();
        private int _structStamp = int.MinValue;
        private int _evalVersion = -1;
        private bool _poseDirty;
        private bool _needsObjectSolve;
        private readonly HashSet<int> _pendingKey = new HashSet<int>();
        private double _playStart;
        private float _playFrom;
        private Vector3 _rootPos;
        private Quaternion _rootRot = Quaternion.identity;
        private Vector3[] _unitWPos = new Vector3[0];
        private Quaternion[] _unitWRot = new Quaternion[0];
        private bool _hasRoot;
        private bool _hooked;
        private readonly Dictionary<int, int> _pivotChoice = new Dictionary<int, int>();
        private int _hierRoot = -1;
        private readonly List<int> _hierSel = new List<int>();

        public bool Valid
        {
            get
            {
                return project != null && rigRoot != null && controls != null && (!_hasRoot || projectRoot != null)
                    && controls.Length == project.bones.Count && _writtenRot.Length == project.bones.Count
                    && unitTargets.Length == UnitSlots;
            }
        }

        /// <summary>单位数。</summary>
        public int UnitSlots
        {
            get { return project.units.Count; }
        }

        public Transform UnitTarget(int u)
        {
            return u >= 0 && u < unitTargets.Length ? unitTargets[u] : null;
        }

        public Animator UnitAnimator(int u)
        {
            return u >= 0 && u < unitAnimators.Length ? unitAnimators[u] : null;
        }

        /// <summary>模型骨骼（或物体单位的物体）对应的骨骼下标，没有控制器时为 -1。</summary>
        public int ModelBoneIndex(Transform t)
        {
            int i;
            return t != null && _modelIndex.TryGetValue(t, out i) ? i : -1;
        }

        /// <summary>控制器或模型骨骼对应的骨骼下标。</summary>
        public int BoneOfTransform(Transform t)
        {
            int i = ControlIndex(t);
            return i >= 0 ? i : ModelBoneIndex(t);
        }

        /// <summary>t 是否在某个单位目标内部（含目标本身）。</summary>
        public bool InsideAnyUnit(Transform t)
        {
            if (t == null)
                return false;
            for (int u = 0; u < unitTargets.Length; u++)
            {
                if (unitTargets[u] != null && t.IsChildOf(unitTargets[u]))
                    return true;
            }
            return false;
        }

        /// <summary>t 所在的单位（单位目标或其子物体），没有时为 -1。</summary>
        public int UnitOfTransform(Transform t)
        {
            if (t == null)
                return -1;
            for (int u = 0; u < unitTargets.Length; u++)
            {
                if (unitTargets[u] != null && t.IsChildOf(unitTargets[u]))
                    return u;
            }
            return -1;
        }

        public bool PoseDirty
        {
            get { return _poseDirty; }
        }

        /// <summary>当前编辑层是在 IK 之后叠加的层。</summary>
        public bool SplitActive
        {
            get { return project.activeLayer > 0 && !project.ActiveLayer.keepBaseIK; }
        }

        public static void RaiseRepaint()
        {
            if (Repaint != null)
                Repaint();
        }

        // ---------------- 生命周期 ----------------

        /// <summary>打开工程；同一工程已在运行时直接返回它。会话不依附于任何窗口，自己持有预览用的 AnimationMode driver。</summary>
        public static AsiKeySession Start(AsiKeyProject p)
        {
            if (Current != null && Current.project == p)
            {
                if (!Current.Valid)
                    Current.Bind();
                return Current;
            }
            StopCurrent();
            AsiKeySession s = new AsiKeySession();
            s.project = p;
            s._driver = ScriptableObject.CreateInstance<AnimationModeDriver>();
            s._driver.name = "AsiKeyframe Preview";
            s._driver.hideFlags = HideFlags.HideAndDontSave;
            Current = s;
            s.Hook();
            s.Bind();
            RaiseRepaint();
            return s;
        }

        public static void StopCurrent()
        {
            if (Current == null)
                return;
            AsiKeySession s = Current;
            Current = null;
            s.Shutdown();
            SceneView.RepaintAll();
            RaiseRepaint();
        }

        private void Shutdown()
        {
            playing = false;
            Unhook();
            AsiKeyGizmo.Release();
            _preview.End();
            DisposeHuman();
            AsiKeyRigObjects.DestroyRoot(rigRoot);
            rigRoot = null;
            controls = new Transform[0];
            unitRoots = new Transform[0];
            _controlIndex.Clear();
            if (_driver != null)
            {
                UnityEngine.Object.DestroyImmediate(_driver);
                _driver = null;
            }
        }

        private void Hook()
        {
            if (_hooked)
                return;
            _hooked = true;
            EditorApplication.update += Update;
            SceneView.duringSceneGui += AsiKeyRigDrawer.OnSceneGUI;
            HandleUtility.pickGameObjectCustomPasses += AsiKeyRigDrawer.Pick;
            Undo.undoRedoPerformed += OnUndo;
            EditorApplication.playModeStateChanged += OnPlayMode;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            Selection.selectionChanged += OnSelection;
            EditorApplication.quitting += StopCurrent;
            AsiKeyShortcuts.SetActive(true);
        }

        private void Unhook()
        {
            if (!_hooked)
                return;
            _hooked = false;
            EditorApplication.update -= Update;
            SceneView.duringSceneGui -= AsiKeyRigDrawer.OnSceneGUI;
            HandleUtility.pickGameObjectCustomPasses -= AsiKeyRigDrawer.Pick;
            Undo.undoRedoPerformed -= OnUndo;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            Selection.selectionChanged -= OnSelection;
            EditorApplication.quitting -= StopCurrent;
            AsiKeyShortcuts.SetActive(false);
        }

        private static void OnBeforeReload()
        {
            StopCurrent();
        }

        /// <summary>切换播放模式前结束会话（场景要换成副本或换回来），进入后由窗口重新打开。</summary>
        private static void OnPlayMode(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.ExitingPlayMode)
                StopCurrent();
        }

        /// <summary>
        /// 播放模式下场景是临时副本，模型已被 Animator 摆过姿势：结构编辑会读到错误的静止姿势、记下退出后失效的引用，烘焙也会复制到运行中的物体，一律禁止。
        /// </summary>
        public static bool StructureLocked
        {
            get { return EditorApplication.isPlaying; }
        }

        public const string LockedMessage = "播放模式下不能修改骨骼结构和单位，也不能烘焙/导入，退出播放后再操作";

        private void OnSceneSaving(Scene scene, string path)
        {
            _preview.Restore();
        }

        private void OnSceneSaved(Scene scene)
        {
            if (Valid)
                WriteAll();
        }

        private void OnSceneClosing(Scene scene, bool removing)
        {
            if ((projectRoot != null && projectRoot.gameObject.scene == scene) || (rigRoot != null && rigRoot.gameObject.scene == scene))
            {
                StopCurrent();
                return;
            }
            for (int u = 0; u < unitTargets.Length; u++)
            {
                if (unitTargets[u] != null && unitTargets[u].gameObject.scene == scene)
                {
                    StopCurrent();
                    return;
                }
            }
        }

        private void OnSelection()
        {
            TrackHierarchy();
            RaiseRepaint();
            SceneView.RepaintAll();
        }

        /// <summary>层级模式只在根仍选中、且选择变化只是加选或减选时保持。</summary>
        private void TrackHierarchy()
        {
            if (_hierRoot < 0)
                return;
            List<int> now = Valid ? SelectedBones() : new List<int>();
            bool keep = now.Contains(_hierRoot);
            if (keep)
            {
                bool superset = true;
                bool subset = true;
                for (int k = 0; k < _hierSel.Count && superset; k++)
                    superset = now.Contains(_hierSel[k]);
                for (int k = 0; k < now.Count && subset; k++)
                    subset = _hierSel.Contains(now[k]);
                keep = superset || subset;
            }
            _hierSel.Clear();
            if (keep)
                _hierSel.AddRange(now);
            else
                _hierRoot = -1;
        }

        private void OnUndo()
        {
            if (project == null)
                return;
            project.EnsureLayers();
            if (!Valid)
            {
                Bind();
                return;
            }
            SyncStructure();
            Refresh();
        }

        // ---------------- 绑定 ----------------

        public bool Bind()
        {
            playing = false;
            _preview.End();
            DisposeHuman();
            controls = new Transform[0];
            if (project == null)
                return false;
            project.EnsureLayers();

            _hasRoot = !string.IsNullOrEmpty(project.rootId) || !string.IsNullOrEmpty(project.rootPath);
            projectRoot = _hasRoot ? AsiKeyUtil.ResolveTransform(project.rootId, project.rootPath) : null;
            if (_hasRoot && projectRoot == null)
            {
                status = "找不到工程父级：" + project.rootPath;
                return false;
            }
            if (projectRoot != null && !StructureLocked)
            {
                string rp = AsiKeyUtil.ScenePath(projectRoot);
                if (project.rootPath != rp || project.rootName != projectRoot.name)
                {
                    project.rootPath = rp;
                    project.rootName = projectRoot.name;
                    EditorUtility.SetDirty(project);
                }
            }
            string warn = ResolveUnits();

            List<int> created = new List<int>();
            for (int u = 0; u < project.units.Count; u++)
            {
                if (unitTargets[u] == null || HasBones(u))
                    continue;
                if (StructureLocked)
                {
                    warn += (warn.Length > 0 ? "；" : "") + project.units[u].name + "：还没有骨骼，退出播放后打开工程生成";
                    continue;
                }
                string error = AsiKeyUnits.BuildBones(project, u, projectRoot, unitTargets[u]);
                EditorUtility.SetDirty(project);
                if (!string.IsNullOrEmpty(error))
                    warn += (warn.Length > 0 ? "；" : "") + project.units[u].name + "：" + error;
                else
                    created.Add(u);
            }
            if (created.Count > 0)
                project.TouchStructure();

            FollowRoot();
            if (rigRoot == null)
                rigRoot = ResolveRigRoot();
            SyncStructure();
            status = warn;
            GoTo(frame);
            for (int k = 0; k < created.Count; k++)
                KeyRestPose(created[k]);
            return true;
        }

        private bool HasBones(int u)
        {
            for (int i = 0; i < project.bones.Count; i++)
            {
                if (project.bones[i].unit == u)
                    return true;
            }
            return false;
        }

        /// <summary>解析各单位目标，更新路径记录。返回找不到的单位提示。</summary>
        private string ResolveUnits()
        {
            int n = UnitSlots;
            unitTargets = new Transform[n];
            unitAnimators = new Animator[n];
            string warn = "";
            for (int u = 0; u < project.units.Count; u++)
            {
                Transform t = AsiKeyUnits.ResolveTarget(project, u);
                unitTargets[u] = t;
                if (t == null)
                {
                    warn += (warn.Length > 0 ? "；" : "") + "找不到单位「" + project.units[u].name + "」的目标：" + project.units[u].targetPath;
                    continue;
                }
                if (project.IsCharacter(u))
                    unitAnimators[u] = t.GetComponent<Animator>();
                if (!StructureLocked && AsiKeyUnits.RefreshPath(project, u, t))
                    EditorUtility.SetDirty(project);
            }
            return warn;
        }

        private Scene RigScene()
        {
            if (projectRoot != null)
                return projectRoot.gameObject.scene;
            for (int u = 0; u < unitTargets.Length; u++)
            {
                if (unitTargets[u] != null)
                    return unitTargets[u].gameObject.scene;
            }
            return SceneManager.GetActiveScene();
        }

        private Transform ResolveRigRoot()
        {
            AsiKeyRigObjects.DestroyAll();
            return AsiKeyRigObjects.CreateRoot(project, projectRoot, RigScene());
        }

        /// <summary>重新生成单位 u 的骨骼定义，清空该单位的动画。</summary>
        public string RebuildUnit(int u)
        {
            if (!project.ValidUnit(u))
                return "无效单位";
            if (StructureLocked)
                return LockedMessage;
            _preview.End();
            DisposeHuman();
            Undo.RecordObject(project, "重建骨骼");
            string error = AsiKeyUnits.RebuildUnit(project, u, projectRoot);
            EditorUtility.SetDirty(project);
            _structStamp = int.MinValue;
            SyncStructure();
            GoTo(frame);
            if (string.IsNullOrEmpty(error))
                KeyRestPose(u);
            return error;
        }

        /// <summary>把 t 加为新单位（生成骨骼并在第 0 帧打绑定姿势关键帧）。</summary>
        public string AddUnit(Transform t, AsiUnitKind kind)
        {
            if (project == null)
                return "没有打开的工程";
            if (StructureLocked)
                return LockedMessage;
            _preview.End();
            DisposeHuman();
            Undo.RecordObject(project, "添加单位");
            int u;
            string error = AsiKeyUnits.AddUnit(project, projectRoot, t, kind, out u);
            EditorUtility.SetDirty(project);
            if (!string.IsNullOrEmpty(error))
                return error;
            ResolveUnits();
            _structStamp = int.MinValue;
            SyncStructure();
            GoTo(frame);
            KeyRestPose(u);
            status = "已添加单位「" + project.units[u].name + "」";
            return "";
        }

        public void RenameUnit(int u, string name)
        {
            name = (name ?? "").Trim().Replace('/', '_');
            if (!project.ValidUnit(u) || string.IsNullOrEmpty(name) || name == project.units[u].name)
                return;
            if (StructureLocked)
            {
                status = LockedMessage;
                return;
            }
            for (int k = 0; k < project.units.Count; k++)
            {
                if (k != u && project.units[k].name == name)
                {
                    status = "单位名「" + name + "」已被使用";
                    return;
                }
            }
            Undo.RecordObject(project, "重命名单位");
            project.units[u].name = name;
            if (u < unitRoots.Length && unitRoots[u] != null)
            {
                Undo.RecordObject(unitRoots[u].gameObject, "重命名单位");
                unitRoots[u].name = AsiKeyRigObjects.UnitRootName(project, u);
            }
            project.TouchStructure();
            EditorUtility.SetDirty(project);
            EndStructureEdit();
        }

        /// <summary>把单位的目标换成 t。类型（人形 / 骨骼 / 物体）不同时重建该单位的骨骼。</summary>
        public string RetargetUnit(int u, Transform t)
        {
            if (!project.ValidUnit(u) || t == null)
                return "无效单位或没有选择物体";
            if (StructureLocked)
                return LockedMessage;
            AsiKeyUnit unit = project.units[u];
            AsiUnitKind kind = unit.kind == AsiUnitKind.Object ? AsiUnitKind.Object : AsiKeyUnits.DetectKind(t);
            if (unit.kind != AsiUnitKind.Object && kind == AsiUnitKind.Object)
                return "角色单位的新目标需要 Animator";
            string error = AsiKeyUnits.CanAdd(project, projectRoot, t, kind, u);
            if (!string.IsNullOrEmpty(error))
                return error;
            Undo.RecordObject(project, "更换单位目标");
            bool rebuild = kind != unit.kind;
            unit.targetId = AsiKeyUtil.GetId(t.gameObject);
            unit.targetPath = AsiKeyUtil.ScenePath(t);
            unit.kind = kind;
            Animator a = t.GetComponent<Animator>();
            if (a != null && kind != AsiUnitKind.Object)
                unit.avatar = a.avatar;
            EditorUtility.SetDirty(project);
            ResolveUnits();
            if (rebuild)
                return RebuildUnit(u);
            _structStamp = int.MinValue;
            SyncStructure();
            GoTo(frame);
            return "";
        }

        public void RemoveUnit(int u)
        {
            if (!project.ValidUnit(u))
                return;
            if (StructureLocked)
            {
                status = LockedMessage;
                return;
            }
            _preview.End();
            DisposeHuman();
            Undo.RecordObject(project, "删除单位");
            string name = project.units[u].name;
            AsiKeyUnits.RemoveUnit(project, u);
            EditorUtility.SetDirty(project);
            ResolveUnits();
            _structStamp = int.MinValue;
            SyncStructure();
            GoTo(frame);
            status = "已删除单位「" + name + "」";
        }

        /// <summary>
        /// 设定工程父级。全部单位目标必须在它下面。物体单位的静止姿势与基础层关键帧在工程父级空间里，按新旧父级的相对位姿换算，场景里的位置不变。
        /// </summary>
        public string SetProjectRoot(Transform root)
        {
            if (project == null)
                return "没有打开的工程";
            if (StructureLocked)
                return LockedMessage;
            for (int u = 0; u < unitTargets.Length; u++)
            {
                Transform t = unitTargets[u];
                if (t != null && root != null && (t == root || !t.IsChildOf(root)))
                    return "单位「" + project.UnitName(u) + "」不在「" + root.name + "」下面";
            }
            Undo.RecordObject(project, "设为工程父级");
            Vector3 oldPos = projectRoot != null ? projectRoot.position : Vector3.zero;
            Quaternion oldRot = projectRoot != null ? projectRoot.rotation : Quaternion.identity;
            Vector3 newPos = root != null ? root.position : Vector3.zero;
            Quaternion newRot = root != null ? root.rotation : Quaternion.identity;
            Quaternion inv = Quaternion.Inverse(newRot);
            Quaternion dr = AsiKeyMath.Norm(inv * oldRot);
            Vector3 dp = inv * (oldPos - newPos);
            for (int i = 0; i < project.bones.Count; i++)
            {
                AsiKeyBone b = project.bones[i];
                if (b.parent >= 0 || project.IsCharacter(b.unit))
                    continue;
                b.restPos = dp + dr * b.restPos;
                b.restRot = AsiKeyMath.Norm(dr * b.restRot);
                b.origPos = dp + dr * b.origPos;
                b.origRot = AsiKeyMath.Norm(dr * b.origRot);
                b.rigRestPos = b.restPos;
                b.rigRestRot = b.restRot;
                AsiKeyTrack tr = project.layers.Count > 0 ? project.FindTrack(0, AsiTrackKind.Bone, i) : null;
                if (tr == null)
                    continue;
                for (int k = 0; k < tr.keys.Count; k++)
                {
                    AsiKey key = tr.keys[k];
                    AsiKeyEdit.EnsureArrays(project, tr, key);
                    key.rot[0] = AsiKeyMath.Norm(dr * key.rot[0]);
                    key.pos = dp + dr * key.pos;
                }
                AsiKeyEdit.RebuildEulerFromRot(project, tr);
                AsiKeyEdit.RecomputeTangents(project, tr);
            }
            project.rootId = root != null ? AsiKeyUtil.GetId(root.gameObject) : "";
            project.rootPath = root != null ? AsiKeyUtil.ScenePath(root) : "";
            project.rootName = root != null ? root.name : "";
            project.TouchStructure();
            EditorUtility.SetDirty(project);
            Bind();
            return "";
        }

        /// <summary>结构编辑前调用：停掉预览，让模型回到原始姿势以便读取静止姿势。</summary>
        public void BeginStructureEdit(string undoName)
        {
            _preview.End();
            DisposeHuman();
            Undo.RecordObject(project, undoName);
        }

        /// <summary>让模型回到原始姿势（烘焙、导入前复制模型用），下次刷新时自动恢复预览。</summary>
        public void SuspendPreview()
        {
            playing = false;
            _preview.End();
            DisposeHuman();
        }

        public void EndStructureEdit()
        {
            EditorUtility.SetDirty(project);
            SyncStructure();
            Refresh();
        }

        public void SyncStructure()
        {
            if (project == null)
                return;
            if (!StructureLocked && AsiKeyFingers.AddTipPivots(project))
            {
                EditorUtility.SetDirty(project);
                status = "已补上指尖轴心（不显示）。选中手指末节后按选择子级可以选到";
            }
            if (AsiKeyFingers.ReleaseHinge(project))
                EditorUtility.SetDirty(project);
            if (AsiKeyFingers.AlignCurlPoles(project))
                EditorUtility.SetDirty(project);
            if (AsiKeyFingers.AlignShapeFrames(project))
                EditorUtility.SetDirty(project);
            if (unitTargets.Length != UnitSlots)
                ResolveUnits();
            if (rigRoot == null)
                rigRoot = ResolveRigRoot();
            pose.Ensure(project);
            controls = AsiKeyRigObjects.Sync(project, rigRoot, pose.order, out unitRoots);
            _controlIndex.Clear();
            for (int i = 0; i < controls.Length; i++)
            {
                if (controls[i] != null)
                    _controlIndex[controls[i]] = i;
            }
            int n = project.bones.Count;
            modelBones = new Transform[n];
            _modelIndex.Clear();
            for (int i = 0; i < n; i++)
            {
                AsiKeyBone b = project.bones[i];
                Transform ut = UnitTarget(b.unit);
                if (b.isCom || ut == null)
                    continue;
                if (string.IsNullOrEmpty(b.path))
                    modelBones[i] = project.IsCharacter(b.unit) ? null : ut;
                else
                    modelBones[i] = ut.Find(b.path);
                if (modelBones[i] != null && !_modelIndex.ContainsKey(modelBones[i]))
                    _modelIndex[modelBones[i]] = i;
            }
            _writtenRot = new Quaternion[n];
            _writtenPos = new Vector3[n];
            _dragStart.Clear();
            if (_structStamp != project.structureStamp)
            {
                _hierRoot = -1;
                _hierSel.Clear();
            }
            _structStamp = project.structureStamp;
            _pivotChoice.Clear();
            ctx.ClearCache();
            InvalidateTrajectories();
        }

        private void DisposeHuman()
        {
            for (int u = 0; u < _humans.Length; u++)
            {
                if (_humans[u] != null)
                    _humans[u].Dispose();
            }
            _humans = new AsiKeyHumanBridge[0];
        }

        // ---------------- 求值与写入 ----------------

        public void Refresh()
        {
            GoTo(frame);
        }

        public void GoTo(float f)
        {
            if (!Valid)
                return;
            frame = f;
            Evaluate();
            WriteAll();
        }

        private void Evaluate()
        {
            if (project.structureStamp != _structStamp)
                SyncStructure();
            FollowRoot();
            int act = project.activeLayer;
            int split = SplitActive ? act : -1;
            AsiKeyEvaluator.Evaluate(project, ctx, frame, pose, act, split);
            _evalVersion = project.version;
            _poseDirty = false;
            _pendingKey.Clear();
            _heldChildren.Clear();
            _needsObjectSolve = UsesSceneObjects();
        }

        private bool UsesSceneObjects()
        {
            for (int c = 0; c < pose.chainCount; c++)
            {
                AsiChainState s = pose.chains[c];
                if (!s.valid || s.blend <= 1e-4f)
                    continue;
                if (s.a.space == AsiIKSpace.Object && s.a.bone < 0 && !string.IsNullOrEmpty(s.a.obj))
                    return true;
                if (s.mix > 0f && s.b.space == AsiIKSpace.Object && s.b.bone < 0 && !string.IsNullOrEmpty(s.b.obj))
                    return true;
            }
            for (int k = 0; k < project.constraints.Count; k++)
            {
                AsiKeyConstraint c = project.constraints[k];
                if (!c.enabled)
                    continue;
                for (int t = 0; t < c.targets.Count; t++)
                {
                    if (c.targets[t].bone < 0 && !string.IsNullOrEmpty(c.targets[t].obj))
                        return true;
                }
                if (c.upMode == AsiLookUp.Object && c.upBone < 0 && !string.IsNullOrEmpty(c.upObj))
                    return true;
                for (int l = 0; l < c.links.Count; l++)
                {
                    if (c.links[l].bone < 0 && !string.IsNullOrEmpty(c.links[l].obj))
                        return true;
                }
            }
            return false;
        }

        /// <summary>角色单位的坐标系取 Animator 所在物体；物体单位就是工程父级（单位矩阵）。</summary>
        private Transform UnitFrameSource(int u)
        {
            if (!project.IsCharacter(u))
                return null;
            return UnitTarget(u);
        }

        /// <summary>读取工程父级与各单位坐标系到上下文，并让控制器根和单位子根跟上。</summary>
        private void FollowRoot()
        {
            Vector3 rp = projectRoot != null ? projectRoot.position : Vector3.zero;
            Quaternion rr = projectRoot != null ? projectRoot.rotation : Quaternion.identity;
            if (rigRoot != null)
            {
                if (rigRoot.position != rp || rigRoot.rotation != rr)
                    rigRoot.SetPositionAndRotation(rp, rr);
                if (rigRoot.localScale != Vector3.one)
                    rigRoot.localScale = Vector3.one;
            }
            ctx.SetRig(rp, rr);
            _rootPos = rp;
            _rootRot = rr;
            int n = UnitSlots;
            ctx.SetUnitCount(n);
            if (_unitWPos.Length != n)
            {
                _unitWPos = new Vector3[n];
                _unitWRot = new Quaternion[n];
            }
            for (int u = 0; u < n; u++)
            {
                Transform t = UnitFrameSource(u);
                if (t != null)
                {
                    ctx.SetUnitWorld(u, t.position, t.rotation);
                    _unitWPos[u] = t.position;
                    _unitWRot[u] = t.rotation;
                }
                else
                {
                    ctx.SetUnitIdentity(u);
                    _unitWPos[u] = Vector3.zero;
                    _unitWRot[u] = Quaternion.identity;
                }
                Transform ur = u < unitRoots.Length ? unitRoots[u] : null;
                if (ur != null)
                {
                    if (ur.localPosition != ctx.unitPos[u])
                        ur.localPosition = ctx.unitPos[u];
                    if (ur.localRotation != ctx.unitRot[u])
                        ur.localRotation = ctx.unitRot[u];
                }
            }
        }

        /// <summary>工程父级或某个角色单位在场景里被移动了。</summary>
        private bool FramesMoved()
        {
            Vector3 rp = projectRoot != null ? projectRoot.position : Vector3.zero;
            Quaternion rr = projectRoot != null ? projectRoot.rotation : Quaternion.identity;
            if (rp != _rootPos || rr != _rootRot)
                return true;
            if (_unitWPos.Length != UnitSlots)
                return true;
            for (int u = 0; u < _unitWPos.Length; u++)
            {
                Transform t = UnitFrameSource(u);
                Vector3 p = t != null ? t.position : Vector3.zero;
                Quaternion r = t != null ? t.rotation : Quaternion.identity;
                if (p != _unitWPos[u] || r != _unitWRot[u])
                    return true;
            }
            return false;
        }

        public bool Movable(int i)
        {
            return project.Movable(i);
        }

        private void WriteAll()
        {
            int n = project.bones.Count;
            for (int i = 0; i < n; i++)
            {
                Transform c = controls[i];
                if (c == null)
                    continue;
                c.localPosition = pose.pos[i];
                c.localRotation = pose.rot[i];
                _writtenPos[i] = c.localPosition;
                _writtenRot[i] = c.localRotation;
            }
            WriteModel();
            SceneView.RepaintAll();
            RaiseRepaint();
        }

        private void WriteModel()
        {
            if (modelBones.Length == 0)
                return;
            if (!_preview.Active)
            {
                if (!EditorApplication.isPlaying && AnimationMode.InAnimationMode())
                    status = "Animation 窗口正处于预览/录制状态，K帧工具接管了 AnimationMode，请不要同时使用";
                _preview.Begin(_driver);
                PauseAnimators();
            }
            int[] order = pose.order;
            for (int o = 0; o < order.Length; o++)
            {
                int i = order[o];
                Transform t = i < modelBones.Length ? modelBones[i] : null;
                if (t == null)
                    continue;
                _preview.Register(t, UnitTarget(project.bones[i].unit));
                t.rotation = ctx.ToWorldRot(pose.wrot[i]);
                if (Movable(i))
                    t.position = ctx.ToWorldPos(pose.wpos[i]);
            }
            if (!humanPreview)
                return;
            if (_humans.Length != unitAnimators.Length)
            {
                DisposeHuman();
                _humans = new AsiKeyHumanBridge[unitAnimators.Length];
            }
            for (int u = 0; u < unitAnimators.Length; u++)
            {
                Animator a = unitAnimators[u];
                if (a == null || !project.UnitIsHumanoid(u) || a.avatar == null)
                    continue;
                if (_humans[u] == null)
                {
                    string error;
                    _humans[u] = AsiKeyHumanBridge.Create(a.avatar, a.transform, out error);
                    if (_humans[u] == null)
                        status = error;
                }
                AsiKeyHumanBridge h = _humans[u];
                if (h == null)
                    continue;
                for (int j = 1; j < h.joints.Length; j++)
                {
                    if (h.IsHumanJoint(j))
                        _preview.Register(h.joints[j], a.transform);
                }
                h.Retarget();
            }
        }

        /// <summary>播放模式：暂停各单位自身及其上级（到工程父级为止）的 Animator，免得它们每帧覆盖 K帧 写入的姿势。编辑模式下无效果。</summary>
        private void PauseAnimators()
        {
            for (int u = 0; u < unitTargets.Length; u++)
            {
                for (Transform t = unitTargets[u]; t != null; t = t.parent)
                {
                    _preview.Pause(t.GetComponent<Animator>());
                    if (t == projectRoot)
                        break;
                }
            }
        }

        // ---------------- 每帧更新 ----------------

        private void Update()
        {
            // 在同一次 update 派发里被 StopCurrent 结束时，Unity 已取好的委托列表里还有本方法，会再进来一次。
            if (Current != this)
            {
                Unhook();
                return;
            }
            if (project == null)
            {
                StopCurrent();
                return;
            }
            if (_hasRoot && projectRoot == null)
            {
                status = "工程父级已丢失";
                return;
            }
            if (rigRoot == null || !Valid)
            {
                Bind();
                return;
            }
            if (project.structureStamp != _structStamp || AnyControlMissing())
            {
                SyncStructure();
                Refresh();
                return;
            }
            if (playing)
            {
                TickPlay();
                return;
            }
            bool moved = FramesMoved();
            DetectScale();
            DetectEdits();
            if (_pendingKey.Count > 0 && GUIUtility.hotControl == 0)
                KeyPending();
            if (!_poseDirty && project.version != _evalVersion)
            {
                Refresh();
            }
            else if (moved || _needsObjectSolve)
            {
                FollowRoot();
                AsiKeyEvaluator.Solve(project, ctx, pose);
                WriteAll();
            }
        }

        private bool AnyControlMissing()
        {
            for (int i = 0; i < controls.Length; i++)
            {
                if (controls[i] == null)
                    return true;
            }
            return false;
        }

        // ---------------- 播放 ----------------

        public void TogglePlay()
        {
            if (playing)
            {
                StopPlay();
                return;
            }
            if (!Valid)
                return;
            playing = true;
            _playStart = EditorApplication.timeSinceStartup;
            _playFrom = frame >= project.endFrame ? project.startFrame : frame;
        }

        public void StopPlay()
        {
            if (!playing)
                return;
            playing = false;
            GoTo(Mathf.Round(frame));
        }

        private void TickPlay()
        {
            double now = EditorApplication.timeSinceStartup;
            float f = _playFrom + (float)((now - _playStart) * Mathf.Max(1f, project.frameRate));
            float start = project.startFrame;
            float end = project.endFrame;
            if (f > end)
            {
                float span = end - start;
                if (loop && span > 0f)
                {
                    f = start + Mathf.Repeat(f - start, span);
                }
                else
                {
                    f = end;
                    playing = false;
                }
            }
            GoTo(f);
        }

        // ---------------- 选择 ----------------

        public int ControlIndex(Transform t)
        {
            int i;
            return t != null && _controlIndex.TryGetValue(t, out i) ? i : -1;
        }

        /// <summary>选中的控制器，包括同时选中的父子（Selection.transforms 会剔除已选物体的子级）。</summary>
        public List<int> SelectedBones()
        {
            List<int> list = new List<int>();
            Transform[] sel = Selection.GetTransforms(SelectionMode.Unfiltered);
            for (int k = 0; k < sel.Length; k++)
            {
                int i = ControlIndex(sel[k]);
                if (i >= 0 && !list.Contains(i))
                    list.Add(i);
            }
            return list;
        }

        public bool IsSelected(int i)
        {
            if (i < 0 || i >= controls.Length || controls[i] == null)
                return false;
            return Selection.Contains(controls[i].gameObject);
        }

        /// <summary>双击选中的层级根：旋转时整条层级按相对轴卷曲，移动只动根。不在层级模式时为 -1。</summary>
        public int HierarchyRoot
        {
            get { return _hierRoot >= 0 && IsSelected(_hierRoot) ? _hierRoot : -1; }
        }

        /// <summary>选中 root 和关节连接下的全部子级（不可选的骨骼不选，但继续往下找），进入层级模式。</summary>
        public void SelectHierarchy(int root)
        {
            if (!Valid || root < 0 || root >= project.bones.Count)
                return;
            List<int> list = new List<int> { root };
            List<int> open = new List<int> { root };
            for (int k = 0; k < open.Count; k++)
            {
                for (int j = 0; j < project.bones.Count; j++)
                {
                    if (project.LinkParent(j) != open[k] || open.Contains(j))
                        continue;
                    open.Add(j);
                    if (AsiKeyRigDrawer.Pickable(this, j))
                        list.Add(j);
                }
            }
            _hierRoot = -1;
            SelectBones(list, false);
            _hierRoot = root;
            _hierSel.Clear();
            _hierSel.AddRange(list);
            status = "层级选择：" + project.bones[root].name + " 和 " + (list.Count - 1) + " 根子级";
            RaiseRepaint();
        }

        public void SelectBones(IList<int> bones, bool add)
        {
            List<UnityEngine.Object> objs = new List<UnityEngine.Object>();
            if (add)
                objs.AddRange(Selection.objects);
            for (int k = 0; k < bones.Count; k++)
            {
                int i = bones[k];
                if (i >= 0 && i < controls.Length && controls[i] != null && !objs.Contains(controls[i].gameObject))
                    objs.Add(controls[i].gameObject);
            }
            Selection.objects = objs.ToArray();
        }

        /// <summary>选中骨骼所在的 IK 链（去重）。</summary>
        public List<int> SelectedChains()
        {
            List<int> list = new List<int>();
            List<int> bones = SelectedBones();
            for (int k = 0; k < bones.Count; k++)
            {
                int c = project.ChainOf(bones[k]);
                if (c < 0)
                    c = AsiKeyFingers.TipChain(project, bones[k]);
                if (c >= 0 && !list.Contains(c))
                    list.Add(c);
            }
            return list;
        }

        /// <summary>
        /// 当前 IK 轴心：手动选过的优先，其次是 IK 生效时当前段的轴心，否则用默认轴心
        /// （脚链是脚趾根 <see cref="AsiKeyChain.ChildPivot"/>，其余是末端原点）。
        /// </summary>
        public int PivotChoice(int chain)
        {
            int v;
            if (_pivotChoice.TryGetValue(chain, out v))
                return v;
            if (chain < 0 || chain >= pose.chainCount || chain >= project.chains.Count)
                return 0;
            AsiChainState s = pose.chains[chain];
            if (s.valid && s.blend >= 0.5f)
                return s.pivot;
            return project.UsesChildPivot(project.chains[chain]) ? AsiKeyChain.ChildPivot : 0;
        }

        public void SetPivotChoice(int chain, int pivot)
        {
            _pivotChoice[chain] = pivot;
        }

        public static AsiKeyPoseClip Clipboard
        {
            get { return s_clipboard; }
        }

        private static AsiKeyPoseClip s_clipboard;

        // ---------------- 轨迹 ----------------

        private readonly Dictionary<int, Vector3[]> _traj = new Dictionary<int, Vector3[]>();
        private readonly List<int> _trajBones = new List<int>();
        private readonly AsiKeyPose _trajPose = new AsiKeyPose();
        private int _trajVersion = -1;
        private int _trajStart;
        private int _trajEnd;
        private int _trajLayer;

        public void InvalidateTrajectories()
        {
            _trajVersion = -1;
        }

        /// <summary>按需重算末端轨迹（角色空间）。拖动中不重算，用上一份。</summary>
        public void UpdateTrajectories(List<int> bones)
        {
            bool same = bones.Count == _trajBones.Count;
            for (int k = 0; same && k < bones.Count; k++)
                same = _trajBones.Contains(bones[k]);
            bool stale = !same || _trajVersion != project.version || _trajStart != project.startFrame
                || _trajEnd != project.endFrame || _trajLayer != project.activeLayer;
            if (!stale)
                return;
            if (GUIUtility.hotControl != 0 && same && _traj.Count > 0)
                return;
            int start = project.startFrame;
            int end = Mathf.Max(start, Mathf.Min(project.endFrame, start + 3000));
            int count = end - start + 1;
            _traj.Clear();
            _trajBones.Clear();
            _trajBones.AddRange(bones);
            for (int k = 0; k < bones.Count; k++)
                _traj[bones[k]] = new Vector3[count];
            int act = project.activeLayer;
            int split = SplitActive ? act : -1;
            for (int f = 0; f < count; f++)
            {
                AsiKeyEvaluator.Evaluate(project, ctx, start + f, _trajPose, act, split);
                for (int k = 0; k < bones.Count; k++)
                {
                    int b = bones[k];
                    if (b >= 0 && b < _trajPose.count)
                        _traj[b][f] = _trajPose.wpos[b];
                }
            }
            _trajVersion = project.version;
            _trajStart = project.startFrame;
            _trajEnd = project.endFrame;
            _trajLayer = act;
        }

        public Vector3[] Trajectory(int bone)
        {
            Vector3[] v;
            return _traj.TryGetValue(bone, out v) ? v : null;
        }

        public int TrajectoryStart
        {
            get { return _trajStart; }
        }
    }
}
