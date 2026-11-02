using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 烘焙：在隐藏的模型副本上逐帧求值，读出骨骼局部变换。
    /// Generic 片段直接写 Transform 曲线；Humanoid 片段经 HumanPoseHandler 转成肌肉曲线，非人形骨骼仍写 Transform 曲线。
    /// </summary>
    public static class AsiKeyBaker
    {
        public sealed class Clone : System.IDisposable
        {
            public GameObject go;
            public Transform root;

            /// <param name="keepAvatar">false 时去掉副本的 Avatar：Humanoid Animator 会忽略人形骨骼上的 Transform 曲线，采样 Generic 片段前要去掉。</param>
            public Clone(Animator animator, bool keepAvatar = true)
            {
                go = Object.Instantiate(animator.gameObject);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.name = animator.gameObject.name;
                root = go.transform;
                root.SetParent(null, false);
                root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                root.localScale = animator.transform.lossyScale;
                Animator ca = go.GetComponent<Animator>();
                if (ca != null && !keepAvatar)
                    ca.avatar = null;
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                    renderers[i].enabled = false;
            }

            public void Dispose()
            {
                if (go != null)
                    Object.DestroyImmediate(go);
                go = null;
            }
        }

        public static bool Movable(AsiKeyProject p, int i)
        {
            return p.Movable(i);
        }

        /// <summary>
        /// 把求值结果写到副本骨骼上。副本根在原点，代表单位坐标系，所以姿势（工程父级空间）先换算到单位 u 的坐标系。
        /// </summary>
        public static void WritePose(AsiKeyProject p, AsiKeyPose pose, Transform[] bones, int u)
        {
            Quaternion inv = Quaternion.Inverse(pose.UnitRot(u));
            Vector3 up = pose.UnitPos(u);
            int[] order = pose.order;
            for (int o = 0; o < order.Length; o++)
            {
                int i = order[o];
                Transform t = bones[i];
                if (t == null)
                    continue;
                t.rotation = AsiKeyMath.Norm(inv * pose.wrot[i]);
                if (Movable(p, i))
                    t.position = inv * (pose.wpos[i] - up);
            }
        }

        /// <summary>副本里单位 u 的骨骼（其它单位的骨骼为 null）。</summary>
        public static Transform[] MapBones(AsiKeyProject p, Transform root, int u)
        {
            Transform[] bones = new Transform[p.bones.Count];
            for (int i = 0; i < bones.Length; i++)
            {
                AsiKeyBone b = p.bones[i];
                if (b.unit == u && !b.isCom && !string.IsNullOrEmpty(b.path))
                    bones[i] = root.Find(b.path);
            }
            return bones;
        }

        private sealed class BoneSamples
        {
            public int bone;
            public string path;
            public bool pos;
            public Quaternion[] rot;
            public Vector3[] p;
        }

        private static string Folder(AsiKeyProject p)
        {
            return string.IsNullOrEmpty(p.bakeFolder) ? "Assets" : p.bakeFolder.TrimEnd('/');
        }

        private static int CharacterCount(AsiKeyProject p)
        {
            int n = 0;
            for (int u = 0; u < p.units.Count; u++)
            {
                if (p.units[u].IsCharacter)
                    n++;
            }
            return n;
        }

        /// <summary>
        /// 单位 u 的默认片段名：单位自己的片段名优先；只有一个角色单位时沿用工程片段名；否则「工程片段名_单位名」。
        /// </summary>
        public static string ClipNameOf(AsiKeyProject p, int u)
        {
            AsiKeyUnit unit = p.ValidUnit(u) ? p.units[u] : null;
            if (unit != null && !string.IsNullOrEmpty(unit.clipName))
                return unit.clipName;
            string baseName = string.IsNullOrEmpty(p.clipName) ? p.name : p.clipName;
            if (unit == null || unit.IsCharacter && CharacterCount(p) <= 1)
                return baseName;
            return baseName + "_" + unit.name;
        }

        public static string DefaultPath(AsiKeyProject p, int u, bool humanoid)
        {
            return Folder(p) + "/" + ClipNameOf(p, u) + (humanoid ? "_Humanoid" : "") + ".anim";
        }

        public static string MergedPath(AsiKeyProject p)
        {
            string name = !string.IsNullOrEmpty(p.mergedClipName) ? p.mergedClipName : (string.IsNullOrEmpty(p.clipName) ? p.name : p.clipName) + "_Objects";
            return Folder(p) + "/" + name + ".anim";
        }

        private static void Range(AsiKeyProject p, out int start, out int frames, out float fps)
        {
            start = p.startFrame;
            int end = Mathf.Max(p.startFrame + 1, p.endFrame);
            frames = end - start + 1;
            fps = Mathf.Max(1f, p.frameRate);
        }

        private static string CheckPath(ref string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets"))
                return "保存路径必须在 Assets 下";
            if (!path.EndsWith(".anim"))
                path += ".anim";
            return "";
        }

        /// <summary>
        /// 烘焙角色单位 u。所有单位一起求值（约束可以跨单位），再把该单位的骨骼换算到它自己的坐标系写到副本上采样。
        /// </summary>
        /// <param name="humanoid">true 输出 Humanoid 肌肉片段，false 输出专属骨骼片段。</param>
        public static string BakeUnit(AsiKeySession s, int u, bool humanoid, string path, out AnimationClip result)
        {
            result = null;
            AsiKeyProject p = s.project;
            if (p == null || !p.IsCharacter(u))
                return "不是角色单位";
            if (AsiKeySession.StructureLocked)
                return AsiKeySession.LockedMessage;
            Animator animator = s.UnitAnimator(u);
            if (animator == null)
                return "找不到单位「" + p.UnitName(u) + "」的 Animator";
            AsiKeyUnit unit = p.units[u];
            Avatar avatar = animator.avatar != null ? animator.avatar : unit.avatar;
            if (humanoid && (unit.kind != AsiUnitKind.Humanoid || avatar == null || !avatar.isHuman))
                return "单位「" + p.UnitName(u) + "」不是 Humanoid，不能烘焙肌肉片段";
            string perr = CheckPath(ref path);
            if (!string.IsNullOrEmpty(perr))
                return perr;
            int start, frames;
            float fps;
            Range(p, out start, out frames, out fps);

            s.SuspendPreview();
            AnimationClip clip = new AnimationClip();
            clip.name = Path.GetFileNameWithoutExtension(path);
            clip.frameRate = fps;
            string error = "";
            bool built = false;
            AsiKeyHumanBridge bridge = null;
            Clone clone = null;
            try
            {
                clone = new Clone(animator);
                Transform[] bones = MapBones(p, clone.root, u);
                AsiKeyContext ctx = s.ctx;
                AsiKeyPose pose = new AsiKeyPose();
                pose.Ensure(p);

                if (humanoid)
                {
                    bridge = AsiKeyHumanBridge.Create(avatar, clone.root, out error);
                    if (bridge == null)
                        return error;
                }

                List<BoneSamples> samples = new List<BoneSamples>();
                for (int i = 0; i < p.bones.Count; i++)
                {
                    AsiKeyBone b = p.bones[i];
                    if (b.isCom || bones[i] == null)
                        continue;
                    if (humanoid && b.humanBone >= 0)
                        continue;
                    BoneSamples bs = new BoneSamples();
                    bs.bone = i;
                    bs.path = b.path;
                    bs.pos = Movable(p, i);
                    bs.rot = new Quaternion[frames];
                    bs.p = bs.pos ? new Vector3[frames] : null;
                    samples.Add(bs);
                }

                int muscleCount = HumanTrait.MuscleCount;
                float[][] muscles = null;
                Vector3[] bodyPos = null;
                Quaternion[] bodyRot = null;
                if (humanoid)
                {
                    muscles = new float[muscleCount][];
                    for (int m = 0; m < muscleCount; m++)
                        muscles[m] = new float[frames];
                    bodyPos = new Vector3[frames];
                    bodyRot = new Quaternion[frames];
                }

                for (int k = 0; k < frames; k++)
                {
                    if ((k & 15) == 0 && EditorUtility.DisplayCancelableProgressBar("K帧烘焙", clip.name + "  帧 " + (start + k), k / (float)frames))
                        return "已取消";
                    AsiKeyEvaluator.Evaluate(p, ctx, start + k, pose, int.MaxValue, -1);
                    WritePose(p, pose, bones, u);
                    for (int b = 0; b < samples.Count; b++)
                    {
                        BoneSamples bs = samples[b];
                        Transform t = bones[bs.bone];
                        Quaternion q = AsiKeyMath.Norm(t.localRotation);
                        if (k > 0 && Quaternion.Dot(bs.rot[k - 1], q) < 0f)
                            q = AsiKeyMath.Neg(q);
                        bs.rot[k] = q;
                        if (bs.pos)
                            bs.p[k] = t.localPosition;
                    }
                    if (humanoid)
                    {
                        bridge.Read();
                        bodyPos[k] = bridge.pose.bodyPosition;
                        Quaternion br = AsiKeyMath.Norm(bridge.pose.bodyRotation);
                        if (k > 0 && Quaternion.Dot(bodyRot[k - 1], br) < 0f)
                            br = AsiKeyMath.Neg(br);
                        bodyRot[k] = br;
                        for (int m = 0; m < muscleCount; m++)
                            muscles[m][k] = AsiKeyMath.Finite(bridge.pose.muscles[m]) ? bridge.pose.muscles[m] : 0f;
                    }
                }

                float[] times = new float[frames];
                for (int k = 0; k < frames; k++)
                    times[k] = k / fps;

                List<EditorCurveBinding> bindings = new List<EditorCurveBinding>();
                List<AnimationCurve> curves = new List<AnimationCurve>();
                for (int b = 0; b < samples.Count; b++)
                {
                    BoneSamples bs = samples[b];
                    AddQuat(bindings, curves, bs.path, times, bs.rot);
                    if (bs.pos)
                        AddVec(bindings, curves, bs.path, "m_LocalPosition", times, bs.p);
                }
                if (humanoid)
                {
                    bool moved = false;
                    for (int m = 0; m < muscleCount && !moved; m++)
                    {
                        for (int k = 0; k < frames; k++)
                        {
                            if (Mathf.Abs(muscles[m][k]) > 1e-5f)
                            {
                                moved = true;
                                break;
                            }
                        }
                    }
                    if (!moved)
                        return "骨骼姿势没有转成肌肉（Avatar 与模型骨架不匹配？）";
                    for (int m = 0; m < muscleCount; m++)
                        AddFloat(bindings, curves, "", typeof(Animator), AsiKeyHumanBridge.MuscleCurveName(HumanTrait.MuscleName[m]), times, muscles[m]);
                    float[] tmp = new float[frames];
                    string[] tn = { "RootT.x", "RootT.y", "RootT.z" };
                    for (int a = 0; a < 3; a++)
                    {
                        for (int k = 0; k < frames; k++)
                            tmp[k] = bodyPos[k][a];
                        AddFloat(bindings, curves, "", typeof(Animator), tn[a], times, tmp);
                    }
                    string[] qn = { "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
                    for (int a = 0; a < 4; a++)
                    {
                        for (int k = 0; k < frames; k++)
                            tmp[k] = bodyRot[k][a];
                        AddFloat(bindings, curves, "", typeof(Animator), qn[a], times, tmp);
                    }
                }
                AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
                clip.EnsureQuaternionContinuity();

                AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = p.bakeLoop;
                settings.startTime = 0f;
                settings.stopTime = (frames - 1) / fps;
                if (humanoid)
                {
                    settings.keepOriginalPositionY = true;
                    settings.keepOriginalPositionXZ = true;
                    settings.keepOriginalOrientation = true;
                    settings.heightFromFeet = false;
                }
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                built = true;
            }
            finally
            {
                if (!built)
                    Object.DestroyImmediate(clip);
                EditorUtility.ClearProgressBar();
                if (bridge != null)
                    bridge.Dispose();
                if (clone != null)
                    clone.Dispose();
                s.Refresh();
            }

            result = Save(clip, path, p.bakeCompress);
            return result != null ? "" : "保存失败：" + path;
        }

        /// <summary>物体单位的一根骨骼：场景里的 Transform、曲线路径、父级是同单位的哪根骨骼（-1 时用场景父级的当前位姿）。</summary>
        private sealed class ObjectSamples
        {
            public int bone;
            public int parentBone;
            public Transform t;
            public string path;
            public bool pos;
            public Quaternion[] rot;
            public Vector3[] p;
        }

        /// <summary>
        /// 烘焙勾了「烘焙」的物体单位。merged 为 false 时每个物体一个 Clip（曲线路径相对物体本身，给物体自己的 Animator 用）；
        /// 为 true 时合并成一个 Clip，路径相对工程父级（给工程父级上的 Animator 用）。局部值相对物体在场景里的父物体。
        /// </summary>
        public static string BakeObjects(AsiKeySession s, bool merged, List<AnimationClip> results)
        {
            AsiKeyProject p = s.project;
            if (p == null)
                return "没有打开的工程";
            if (AsiKeySession.StructureLocked)
                return AsiKeySession.LockedMessage;
            if (merged && s.projectRoot == null)
                return "合并烘焙需要工程父级（曲线路径相对工程父级）";
            List<int> units = new List<int>();
            for (int u = 0; u < p.units.Count; u++)
            {
                if (!p.units[u].IsCharacter && p.units[u].bake && s.UnitTarget(u) != null)
                    units.Add(u);
            }
            if (units.Count == 0)
                return "没有要烘焙的物体单位";

            int start, frames;
            float fps;
            Range(p, out start, out frames, out fps);
            List<ObjectSamples>[] perUnit = new List<ObjectSamples>[units.Count];
            for (int n = 0; n < units.Count; n++)
            {
                int u = units[n];
                List<ObjectSamples> list = new List<ObjectSamples>();
                Transform target = s.UnitTarget(u);
                for (int i = 0; i < p.bones.Count; i++)
                {
                    AsiKeyBone b = p.bones[i];
                    Transform t = i < s.modelBones.Length ? s.modelBones[i] : null;
                    if (b.unit != u || b.isCom || t == null)
                        continue;
                    ObjectSamples os = new ObjectSamples();
                    os.bone = i;
                    os.t = t;
                    os.parentBone = t.parent != null ? s.ModelBoneIndex(t.parent) : -1;
                    if (os.parentBone >= 0 && p.bones[os.parentBone].unit != u)
                        os.parentBone = -1;
                    os.path = merged ? AnimationUtility.CalculateTransformPath(t, s.projectRoot) : AnimationUtility.CalculateTransformPath(t, target);
                    os.pos = Movable(p, i);
                    os.rot = new Quaternion[frames];
                    os.p = os.pos ? new Vector3[frames] : null;
                    list.Add(os);
                }
                perUnit[n] = list;
            }

            s.SuspendPreview();
            AsiKeyPose pose = new AsiKeyPose();
            pose.Ensure(p);
            try
            {
                for (int k = 0; k < frames; k++)
                {
                    if ((k & 15) == 0 && EditorUtility.DisplayCancelableProgressBar("K帧烘焙物体", "帧 " + (start + k), k / (float)frames))
                        return "已取消";
                    AsiKeyEvaluator.Evaluate(p, s.ctx, start + k, pose, int.MaxValue, -1);
                    for (int n = 0; n < perUnit.Length; n++)
                    {
                        List<ObjectSamples> list = perUnit[n];
                        for (int j = 0; j < list.Count; j++)
                            SampleObject(p, s.ctx, pose, list[j], k);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                s.Refresh();
            }

            float[] times = new float[frames];
            for (int k = 0; k < frames; k++)
                times[k] = k / fps;
            if (merged)
            {
                string path = MergedPath(p);
                List<ObjectSamples> all = new List<ObjectSamples>();
                for (int n = 0; n < perUnit.Length; n++)
                    all.AddRange(perUnit[n]);
                AnimationClip clip = BuildObjectClip(p, path, all, times);
                AnimationClip saved = Save(clip, path, p.bakeCompress);
                if (saved == null)
                    return "保存失败：" + path;
                results.Add(saved);
                return "";
            }
            for (int n = 0; n < perUnit.Length; n++)
            {
                string path = DefaultPath(p, units[n], false);
                AnimationClip clip = BuildObjectClip(p, path, perUnit[n], times);
                AnimationClip saved = Save(clip, path, p.bakeCompress);
                if (saved == null)
                    return "保存失败：" + path;
                results.Add(saved);
            }
            return "";
        }

        private static void SampleObject(AsiKeyProject p, AsiKeyContext ctx, AsiKeyPose pose, ObjectSamples os, int k)
        {
            Quaternion wr = ctx.ToWorldRot(pose.wrot[os.bone]);
            Vector3 wp = ctx.ToWorldPos(pose.wpos[os.bone]);
            Transform parent = os.t.parent;
            Quaternion q;
            Vector3 lp;
            if (os.parentBone >= 0)
            {
                Quaternion pr = ctx.ToWorldRot(pose.wrot[os.parentBone]);
                Vector3 pp = ctx.ToWorldPos(pose.wpos[os.parentBone]);
                Quaternion inv = Quaternion.Inverse(pr);
                q = inv * wr;
                lp = Div(inv * (wp - pp), parent.lossyScale);
            }
            else if (parent != null)
            {
                q = Quaternion.Inverse(parent.rotation) * wr;
                lp = parent.InverseTransformPoint(wp);
            }
            else
            {
                q = wr;
                lp = wp;
            }
            q = AsiKeyMath.Norm(q);
            if (k > 0 && Quaternion.Dot(os.rot[k - 1], q) < 0f)
                q = AsiKeyMath.Neg(q);
            os.rot[k] = q;
            if (os.pos)
                os.p[k] = lp;
        }

        private static Vector3 Div(Vector3 v, Vector3 s)
        {
            return new Vector3(Mathf.Abs(s.x) > 1e-8f ? v.x / s.x : v.x, Mathf.Abs(s.y) > 1e-8f ? v.y / s.y : v.y, Mathf.Abs(s.z) > 1e-8f ? v.z / s.z : v.z);
        }

        private static AnimationClip BuildObjectClip(AsiKeyProject p, string path, List<ObjectSamples> samples, float[] times)
        {
            AnimationClip clip = new AnimationClip();
            clip.name = Path.GetFileNameWithoutExtension(path);
            clip.frameRate = Mathf.Max(1f, p.frameRate);
            List<EditorCurveBinding> bindings = new List<EditorCurveBinding>();
            List<AnimationCurve> curves = new List<AnimationCurve>();
            for (int j = 0; j < samples.Count; j++)
            {
                ObjectSamples os = samples[j];
                AddQuat(bindings, curves, os.path, times, os.rot);
                if (os.pos)
                    AddVec(bindings, curves, os.path, "m_LocalPosition", times, os.p);
            }
            AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
            clip.EnsureQuaternionContinuity();
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = p.bakeLoop;
            settings.startTime = 0f;
            settings.stopTime = times.Length > 0 ? times[times.Length - 1] : 0f;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        /// <summary>上次烘焙的附加说明（压缩后被还原的曲线组数），没有时为空串。</summary>
        public static string LastNote { get; private set; }

        private const float VerifyRotDeg = 0.5f;
        private const float VerifyPos = 0.002f;
        private const float VerifyFloat = 0.01f;

        private static AnimationClip Save(AnimationClip clip, string path, bool compress)
        {
            LastNote = "";
            AsiKeyClipCompressor.Settings cs = AsiKeyClipCompressor.Settings.Bezier();
            bool keepHQ = false;
            if (compress)
            {
                AsiKeyClipCompressor.Result r = AsiKeyClipCompressor.Compress(clip, cs);
                if (r.clip != null)
                {
                    int restored = RestoreOverTolerance(clip, r.clip);
                    RewriteCurves(r.clip);
                    if (restored > 0)
                        LastNote = "（压缩后 " + restored + " 组曲线误差超限，已保留原曲线）";
                    Object.DestroyImmediate(clip);
                    clip = r.clip;
                    keepHQ = r.keepHighQuality;
                }
            }
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir.Replace('\\', '/')))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }
            clip.name = Path.GetFileNameWithoutExtension(path);
            AnimationClip existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            AnimationClip saved;
            if (existing != null)
            {
                Undo.RecordObject(existing, "K帧烘焙");
                EditorUtility.CopySerialized(clip, existing);
                existing.name = clip.name;
                Object.DestroyImmediate(clip);
                saved = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(clip, path);
                saved = clip;
            }
            if (compress)
                AsiKeyClipCompressor.ReapplyStorage(saved, cs, keepHQ);
            EditorUtility.SetDirty(saved);
            AssetDatabase.SaveAssetIfDirty(saved);
            return saved;
        }

        /// <summary>
        /// 逐帧比较压缩前后的曲线，四元数按组比角度、位移按组比距离、其余单条比数值。
        /// 超限的组把原曲线写回压缩结果。被压缩器整条删掉的曲线不管（它只删常量曲线）。
        /// </summary>
        private static int RestoreOverTolerance(AnimationClip src, AnimationClip dst)
        {
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(src);
            Dictionary<string, List<EditorCurveBinding>> groups = new Dictionary<string, List<EditorCurveBinding>>();
            List<string> order = new List<string>();
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding b = bindings[i];
                string prop = b.propertyName;
                bool grouped = prop.StartsWith("m_LocalRotation.") || prop.StartsWith("m_LocalPosition.")
                    || (b.type == typeof(Animator) && (prop.StartsWith("RootQ.") || prop.StartsWith("RootT.")));
                string key = b.path + "|" + b.type.Name + "|" + (grouped ? prop.Substring(0, prop.LastIndexOf('.')) : prop);
                List<EditorCurveBinding> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<EditorCurveBinding>();
                    groups.Add(key, list);
                    order.Add(key);
                }
                list.Add(b);
            }

            float fps = Mathf.Max(1f, src.frameRate);
            int frames = Mathf.RoundToInt(src.length * fps) + 1;
            int restored = 0;
            for (int g = 0; g < order.Count; g++)
            {
                List<EditorCurveBinding> list = groups[order[g]];
                int n = list.Count;
                AnimationCurve[] a = new AnimationCurve[n];
                AnimationCurve[] c = new AnimationCurve[n];
                bool dropped = false;
                for (int k = 0; k < n; k++)
                {
                    a[k] = AnimationUtility.GetEditorCurve(src, list[k]);
                    c[k] = AnimationUtility.GetEditorCurve(dst, list[k]);
                    dropped |= c[k] == null;
                }
                if (dropped)
                    continue;
                string prop = list[0].propertyName;
                bool quat = n == 4 && (prop.StartsWith("m_LocalRotation.") || prop.StartsWith("RootQ."));
                bool vec = !quat && n > 1;
                bool bad = false;
                for (int f = 0; f < frames && !bad; f++)
                {
                    float t = Mathf.Min(f / fps, src.length);
                    if (quat)
                    {
                        Quaternion qa = Sample4(a, list, t);
                        Quaternion qc = Sample4(c, list, t);
                        bad = Quaternion.Angle(AsiKeyMath.Norm(qa), AsiKeyMath.Norm(qc)) > VerifyRotDeg;
                    }
                    else
                    {
                        float sq = 0f;
                        for (int k = 0; k < n; k++)
                        {
                            float d = a[k].Evaluate(t) - c[k].Evaluate(t);
                            sq += d * d;
                        }
                        bad = Mathf.Sqrt(sq) > (vec ? VerifyPos : VerifyFloat);
                    }
                }
                if (!bad)
                    continue;
                for (int k = 0; k < n; k++)
                    AnimationUtility.SetEditorCurve(dst, list[k], a[k]);
                restored++;
            }
            return restored;
        }

        /// <summary>
        /// EnsureQuaternionContinuity 会按自动切线重建播放用的旋转曲线，压缩器在静止段起点用的断开切线因此丢失，播放时会冲过头。
        /// 把编辑器曲线原样再写一遍，播放数据就和编辑器曲线一致了。
        /// </summary>
        private static void RewriteCurves(AnimationClip clip)
        {
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            AnimationCurve[] curves = new AnimationCurve[bindings.Length];
            for (int i = 0; i < bindings.Length; i++)
                curves[i] = AnimationUtility.GetEditorCurve(clip, bindings[i]);
            AnimationUtility.SetEditorCurves(clip, bindings, curves);
        }

        private static Quaternion Sample4(AnimationCurve[] curves, List<EditorCurveBinding> list, float t)
        {
            Quaternion q = Quaternion.identity;
            for (int k = 0; k < 4; k++)
            {
                string prop = list[k].propertyName;
                float v = curves[k].Evaluate(t);
                switch (prop[prop.Length - 1])
                {
                    case 'x': q.x = v; break;
                    case 'y': q.y = v; break;
                    case 'z': q.z = v; break;
                    default: q.w = v; break;
                }
            }
            return q;
        }

        private static AnimationCurve Linear(float[] times, float[] values)
        {
            int n = times.Length;
            Keyframe[] keys = new Keyframe[n];
            for (int k = 0; k < n; k++)
            {
                float inT = k > 0 ? (values[k] - values[k - 1]) / (times[k] - times[k - 1]) : 0f;
                float outT = k < n - 1 ? (values[k + 1] - values[k]) / (times[k + 1] - times[k]) : 0f;
                if (k == 0) inT = outT;
                if (k == n - 1) outT = inT;
                keys[k] = new Keyframe(times[k], values[k], inT, outT);
            }
            return new AnimationCurve(keys);
        }

        private static void AddFloat(List<EditorCurveBinding> bindings, List<AnimationCurve> curves, string path, System.Type type, string prop, float[] times, float[] values)
        {
            bindings.Add(EditorCurveBinding.FloatCurve(path, type, prop));
            curves.Add(Linear(times, values));
        }

        private static void AddQuat(List<EditorCurveBinding> bindings, List<AnimationCurve> curves, string path, float[] times, Quaternion[] q)
        {
            float[] v = new float[times.Length];
            string[] axis = { "x", "y", "z", "w" };
            for (int a = 0; a < 4; a++)
            {
                for (int k = 0; k < v.Length; k++)
                    v[k] = q[k][a];
                AddFloat(bindings, curves, path, typeof(Transform), "m_LocalRotation." + axis[a], times, v);
            }
        }

        private static void AddVec(List<EditorCurveBinding> bindings, List<AnimationCurve> curves, string path, string prop, float[] times, Vector3[] p)
        {
            float[] v = new float[times.Length];
            string[] axis = { "x", "y", "z" };
            for (int a = 0; a < 3; a++)
            {
                for (int k = 0; k < v.Length; k++)
                    v[k] = p[k][a];
                AddFloat(bindings, curves, path, typeof(Transform), prop + "." + axis[a], times, v);
            }
        }
    }
}
