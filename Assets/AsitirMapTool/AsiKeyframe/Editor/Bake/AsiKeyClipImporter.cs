using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 把已有 AnimationClip 逐帧采样到模型副本上，换算成控制器关键帧，再按误差精简。
    /// 质心取盆骨的运动（盆骨相对质心保持静止姿势）；IK 链写成自由关键帧，目标记在身体空间。
    /// </summary>
    public static class AsiKeyClipImporter
    {
        public struct Options
        {
            public bool replace;
            public int at;
            public float rotTolerance;
            public float posTolerance;
            public AsiInterpMode mode;
        }

        private struct Sampled
        {
            public Quaternion[][] rot;
            public Vector3[][] pos;
            public int frames;
        }

        /// <summary>轨道属于哪个单位（-1 为不属于单个单位，如约束影响度轨道）。</summary>
        public static int TrackUnit(AsiKeyProject p, AsiKeyTrack tr)
        {
            switch (tr.kind)
            {
                case AsiTrackKind.Bone:
                case AsiTrackKind.ComHorizontal:
                case AsiTrackKind.ComVertical:
                case AsiTrackKind.ComRotation:
                    return tr.target >= 0 && tr.target < p.bones.Count ? p.bones[tr.target].unit : -1;
                case AsiTrackKind.Chain:
                    return tr.target >= 0 && tr.target < p.chains.Count && p.chains[tr.target].bones.Length > 0 ? p.bones[p.chains[tr.target].bones[0]].unit : -1;
                default:
                    return -1;
            }
        }

        private static bool ChainInUnit(AsiKeyProject p, int c, int u)
        {
            int[] bones = p.chains[c].bones;
            return bones.Length > 0 && bones[0] >= 0 && bones[0] < p.bones.Count && p.bones[bones[0]].unit == u;
        }

        /// <summary>把片段导入到角色单位 u：只采样、只写该单位的骨骼；替换模式只清该单位的基础层轨道。</summary>
        public static string Import(AsiKeySession s, AnimationClip clip, Options o, int u)
        {
            AsiKeyProject p = s.project;
            if (p == null || !p.IsCharacter(u))
                return "请选择要导入的角色单位";
            if (AsiKeySession.StructureLocked)
                return AsiKeySession.LockedMessage;
            Animator animator = s.UnitAnimator(u);
            if (animator == null)
                return "找不到单位「" + p.UnitName(u) + "」的 Animator";
            if (clip == null)
                return "没有选择片段";
            if (clip.humanMotion && (animator.avatar == null || !animator.avatar.isHuman))
                return "Humanoid 片段需要目标是 Humanoid";

            float fps = Mathf.Max(1f, p.frameRate);
            int frames = Mathf.Max(2, Mathf.RoundToInt(clip.length * fps) + 1);
            s.SuspendPreview();
            Sampled smp;
            string error;
            try
            {
                error = Sample(p, u, animator, clip, fps, frames, out smp);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            if (!string.IsNullOrEmpty(error))
            {
                s.Refresh();
                return error;
            }

            Undo.RecordObject(p, "导入片段");
            p.EnsureLayers();
            int at = o.replace ? 0 : o.at;
            List<AsiKeyTrack> baseTracks = p.layers[0].tracks;
            if (o.replace)
            {
                for (int t = baseTracks.Count - 1; t >= 0; t--)
                {
                    if (TrackUnit(p, baseTracks[t]) == u)
                        baseTracks.RemoveAt(t);
                }
                if (p.units.Count <= 1)
                {
                    p.startFrame = 0;
                    p.endFrame = frames - 1;
                }
                else
                {
                    p.startFrame = Mathf.Min(p.startFrame, 0);
                    p.endFrame = Mathf.Max(p.endFrame, frames - 1);
                }
            }
            else
            {
                p.endFrame = Mathf.Max(p.endFrame, at + frames - 1);
                p.startFrame = Mathf.Min(p.startFrame, at);
            }

            Vector3[] localPos = new Vector3[p.bones.Count];
            Quaternion[] localRot = new Quaternion[p.bones.Count];
            Quaternion[][] lr = new Quaternion[frames][];
            Vector3[][] lp = new Vector3[frames][];
            for (int k = 0; k < frames; k++)
            {
                ToLocal(p, smp.rot[k], smp.pos[k], localRot, localPos);
                lr[k] = (Quaternion[])localRot.Clone();
                lp[k] = (Vector3[])localPos.Clone();
            }

            float[] lever = Levers(p);
            int written = 0;
            int com = p.ComOf(u);
            if (com >= 0)
            {
                written += WriteTrack(p, AsiTrackKind.ComHorizontal, com, new[] { com }, lr, lp, frames, at, o, lever, !o.replace);
                written += WriteTrack(p, AsiTrackKind.ComVertical, com, new[] { com }, lr, lp, frames, at, o, lever, !o.replace);
                written += WriteTrack(p, AsiTrackKind.ComRotation, com, new[] { com }, lr, lp, frames, at, o, lever, !o.replace);
            }
            for (int i = 0; i < p.bones.Count; i++)
            {
                if (p.bones[i].unit != u || p.bones[i].isCom || p.ChainOf(i) >= 0)
                    continue;
                if (!Moves(lr, lp, i, frames, p.bones[i]))
                    continue;
                written += WriteTrack(p, AsiTrackKind.Bone, i, new[] { i }, lr, lp, frames, at, o, lever, !o.replace);
            }
            for (int c = 0; c < p.chains.Count; c++)
            {
                if (ChainInUnit(p, c, u))
                    written += WriteTrack(p, AsiTrackKind.Chain, c, p.chains[c].bones, lr, lp, frames, at, o, lever, !o.replace);
            }
            for (int c = 0; c < p.chains.Count; c++)
            {
                if (ChainInUnit(p, c, u))
                    s.RecomputeFreeTargets(c);
            }
            p.Touch();
            EditorUtility.SetDirty(p);
            s.status = "导入 " + clip.name + " 到「" + p.UnitName(u) + "」：" + frames + " 帧，精简后 " + written + " 个关键帧";
            s.GoTo(at);
            return "";
        }

        private static bool Moves(Quaternion[][] lr, Vector3[][] lp, int i, int frames, AsiKeyBone b)
        {
            for (int k = 0; k < frames; k++)
            {
                if (Quaternion.Angle(lr[k][i], b.restRot) > 0.01f)
                    return true;
                if (b.animatePosition && (lp[k][i] - b.restPos).sqrMagnitude > 1e-8f)
                    return true;
            }
            return false;
        }

        // ---------------- 采样 ----------------

        private static string Sample(AsiKeyProject p, int u, Animator animator, AnimationClip clip, float fps, int frames, out Sampled smp)
        {
            smp = new Sampled();
            smp.frames = frames;
            smp.rot = new Quaternion[frames][];
            smp.pos = new Vector3[frames][];
            using (AsiKeyBaker.Clone clone = new AsiKeyBaker.Clone(animator, clip.humanMotion))
            {
                Transform[] bones = AsiKeyBaker.MapBones(p, clone.root, u);
                bool moved = SampleWith(p, u, clone, bones, clip, fps, frames, smp, false);
                if (!moved && clip.humanMotion)
                    moved = SampleWith(p, u, clone, bones, clip, fps, frames, smp, true);
                if (!moved)
                    return "采样后骨骼没有任何变化：片段的曲线路径可能与目标骨骼不匹配";
            }
            return "";
        }

        private static bool SampleWith(AsiKeyProject p, int u, AsiKeyBaker.Clone clone, Transform[] bones, AnimationClip clip, float fps, int frames, Sampled smp, bool animationMode)
        {
            AnimationModeDriver driver = null;
            if (animationMode)
            {
                if (AnimationMode.InAnimationMode())
                    return false;
                driver = ScriptableObject.CreateInstance<AnimationModeDriver>();
                driver.hideFlags = HideFlags.HideAndDontSave;
                AnimationMode.StartAnimationMode(driver);
            }
            bool moved = false;
            int hips = p.BoneByHuman(HumanBodyBones.Hips, u);
            int com = p.ComOf(u);
            try
            {
                for (int k = 0; k < frames; k++)
                {
                    if ((k & 15) == 0)
                        EditorUtility.DisplayProgressBar("导入片段", clip.name + "  帧 " + k, k / (float)frames);
                    float t = Mathf.Min(k / fps, clip.length);
                    clone.root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    if (animationMode)
                    {
                        AnimationMode.BeginSampling();
                        AnimationMode.SampleAnimationClip(clone.go, clip, t);
                        AnimationMode.EndSampling();
                    }
                    else
                    {
                        clip.SampleAnimation(clone.go, t);
                    }
                    Quaternion[] wr = new Quaternion[p.bones.Count];
                    Vector3[] wp = new Vector3[p.bones.Count];
                    for (int i = 0; i < p.bones.Count; i++)
                    {
                        AsiKeyBone b = p.bones[i];
                        if (bones[i] != null)
                        {
                            wr[i] = AsiKeyMath.Norm(bones[i].rotation);
                            wp[i] = bones[i].position;
                            if (!moved && (Quaternion.Angle(wr[i], b.rigRestRot) > 0.01f || (wp[i] - b.rigRestPos).sqrMagnitude > 1e-8f))
                                moved = true;
                        }
                        else
                        {
                            wr[i] = b.rigRestRot;
                            wp[i] = b.rigRestPos;
                        }
                    }
                    if (com >= 0 && hips >= 0 && bones[hips] != null)
                    {
                        AsiKeyBone hb = p.bones[hips];
                        Quaternion comRot = AsiKeyMath.Norm(wr[hips] * Quaternion.Inverse(hb.restRot));
                        wr[com] = comRot;
                        wp[com] = wp[hips] - comRot * hb.restPos;
                    }
                    smp.rot[k] = wr;
                    smp.pos[k] = wp;
                }
            }
            finally
            {
                if (driver != null)
                {
                    AnimationMode.StopAnimationMode(driver);
                    Object.DestroyImmediate(driver);
                }
            }
            return moved;
        }

        private static void ToLocal(AsiKeyProject p, Quaternion[] wr, Vector3[] wp, Quaternion[] lr, Vector3[] lp)
        {
            for (int i = 0; i < p.bones.Count; i++)
            {
                int parent = p.bones[i].parent;
                if (parent < 0)
                {
                    lr[i] = wr[i];
                    lp[i] = wp[i];
                }
                else
                {
                    Quaternion inv = Quaternion.Inverse(wr[parent]);
                    lr[i] = AsiKeyMath.Norm(inv * wr[i]);
                    lp[i] = inv * (wp[i] - wp[parent]);
                }
            }
        }

        // ---------------- 精简写入 ----------------

        /// <summary>每根骨骼到最远子孙（至少到自身末端）的距离。旋转误差乘它就是子孙处的位移误差。</summary>
        private static float[] Levers(AsiKeyProject p)
        {
            int n = p.bones.Count;
            float[] lever = new float[n];
            for (int i = 0; i < n; i++)
                lever[i] = p.bones[i].boneLength;
            for (int j = 0; j < n; j++)
            {
                for (int a = 0; a < n; a++)
                {
                    if (a != j && p.IsUnder(j, a))
                        lever[a] = Mathf.Max(lever[a], (p.bones[j].rigRestPos - p.bones[a].rigRestPos).magnitude);
                }
            }
            return lever;
        }

        /// <summary>
        /// 把一根骨骼逐帧的局部姿势（从 at 帧起）按误差精简写进基础层，覆盖这段范围内原有的关键帧。质心骨骼写三条质心轨道。返回写入的关键帧数。
        /// </summary>
        public static int WriteBoneKeys(AsiKeyProject p, int bone, Quaternion[] rot, Vector3[] pos, int at, float rotTolerance, float posTolerance)
        {
            int frames = rot.Length;
            int n = p.bones.Count;
            Quaternion[][] lr = new Quaternion[frames][];
            Vector3[][] lp = new Vector3[frames][];
            for (int k = 0; k < frames; k++)
            {
                lr[k] = new Quaternion[n];
                lp[k] = new Vector3[n];
                lr[k][bone] = rot[k];
                lp[k][bone] = pos[k];
            }
            Options o;
            o.replace = false;
            o.at = at;
            o.rotTolerance = rotTolerance;
            o.posTolerance = posTolerance;
            o.mode = AsiInterpMode.TCB;
            float[] lever = Levers(p);
            int[] members = { bone };
            if (!p.bones[bone].isCom)
                return WriteTrack(p, AsiTrackKind.Bone, bone, members, lr, lp, frames, at, o, lever, true);
            int written = WriteTrack(p, AsiTrackKind.ComHorizontal, bone, members, lr, lp, frames, at, o, lever, true);
            written += WriteTrack(p, AsiTrackKind.ComVertical, bone, members, lr, lp, frames, at, o, lever, true);
            written += WriteTrack(p, AsiTrackKind.ComRotation, bone, members, lr, lp, frames, at, o, lever, true);
            return written;
        }

        private static int WriteTrack(AsiKeyProject p, AsiTrackKind kind, int target, int[] members, Quaternion[][] lr, Vector3[][] lp,
            int frames, int at, Options o, float[] lever, bool clearRange)
        {
            AsiKeyTrack temp = new AsiKeyTrack();
            temp.kind = kind;
            temp.target = target;
            AsiKeyTrack existing = p.FindTrack(0, kind, target);
            temp.mode = existing != null && existing.keys.Count > 0 ? existing.mode : o.mode;
            temp.rotOrder = existing != null ? existing.rotOrder : AsiRotOrder.XYZ;
            int m = p.MemberCount(temp);
            int mask = p.PosMask(temp);
            int rotCh = m * 3;
            int pb = kind == AsiTrackKind.Bone ? target : (kind == AsiTrackKind.Chain ? -1 : target);

            Vector3[][] euler = new Vector3[frames][];
            for (int k = 0; k < frames; k++)
            {
                euler[k] = new Vector3[m];
                for (int j = 0; j < m; j++)
                {
                    Quaternion q = lr[k][members[j]];
                    Vector3 hint = k > 0 ? euler[k - 1][j] : AsiKeyMath.QuatToEuler(q, temp.rotOrder, Vector3.zero);
                    euler[k][j] = AsiKeyMath.QuatToEuler(q, temp.rotOrder, hint);
                }
            }

            List<int> kept = new List<int> { 0, frames - 1 };
            for (int iter = 0; iter < 64; iter++)
            {
                Build(p, temp, kind, members, kept, lr, lp, euler, pb, 0);
                List<int> add = new List<int>();
                for (int seg = 0; seg + 1 < kept.Count; seg++)
                {
                    int a = kept[seg];
                    int b = kept[seg + 1];
                    int worst = -1;
                    float worstErr = 0f;
                    for (int k = a + 1; k < b; k++)
                    {
                        float err = 0f;
                        for (int j = 0; j < m; j++)
                        {
                            Quaternion q = AsiKeyTrackEval.EvalRot(temp, j, k, Quaternion.identity);
                            float deg = Quaternion.Angle(q, lr[k][members[j]]);
                            err = Mathf.Max(err, deg / Mathf.Max(1e-4f, o.rotTolerance));
                            err = Mathf.Max(err, deg * Mathf.Deg2Rad * lever[members[j]] / Mathf.Max(1e-6f, o.posTolerance));
                        }
                        if (mask != 0 && pb >= 0)
                        {
                            Vector3 v = AsiKeyTrackEval.EvalPos(temp, mask, rotCh, k, lp[k][pb]);
                            err = Mathf.Max(err, (v - lp[k][pb]).magnitude / Mathf.Max(1e-6f, o.posTolerance));
                        }
                        if (err > worstErr)
                        {
                            worstErr = err;
                            worst = k;
                        }
                    }
                    if (worst >= 0 && worstErr > 1f)
                        add.Add(worst);
                }
                if (add.Count == 0)
                    break;
                kept.AddRange(add);
                kept.Sort();
            }
            Build(p, temp, kind, members, kept, lr, lp, euler, pb, at);

            AsiKeyTrack tr = p.GetTrack(0, kind, target);
            if (clearRange)
            {
                for (int k = tr.keys.Count - 1; k >= 0; k--)
                {
                    float f = tr.keys[k].frame;
                    if (f >= at - 0.01f && f <= at + frames - 1 + 0.01f)
                        tr.keys.RemoveAt(k);
                }
            }
            if (tr.keys.Count == 0)
            {
                tr.mode = temp.mode;
                tr.rotOrder = temp.rotOrder;
            }
            tr.keys.AddRange(temp.keys);
            AsiKeyEdit.SortKeys(tr);
            if (tr.mode == AsiInterpMode.Curve)
                AsiKeyEdit.RebuildEulerFromRot(p, tr);
            AsiKeyEdit.AfterEdit(p, tr, 0, null);
            return temp.keys.Count;
        }

        private static void Build(AsiKeyProject p, AsiKeyTrack temp, AsiTrackKind kind, int[] members, List<int> kept,
            Quaternion[][] lr, Vector3[][] lp, Vector3[][] euler, int pb, int offset)
        {
            temp.keys.Clear();
            int m = p.MemberCount(temp);
            for (int n = 0; n < kept.Count; n++)
            {
                int k = kept[n];
                AsiKey key = new AsiKey();
                key.frame = k + offset;
                key.type = kind == AsiTrackKind.Chain ? AsiKeyType.Free : AsiKeyType.Set;
                key.ikSpace = AsiIKSpace.Body;
                key.ikBlend = 0f;
                key.joinToPrev = false;
                AsiKeyEdit.EnsureArrays(p, temp, key);
                for (int j = 0; j < m; j++)
                {
                    key.rot[j] = lr[k][members[j]];
                    key.euler[j] = euler[k][j];
                }
                if (pb >= 0)
                    key.pos = lp[k][pb];
                temp.keys.Add(key);
            }
            AsiKeyEdit.RecomputeTangents(p, temp);
        }
    }
}
