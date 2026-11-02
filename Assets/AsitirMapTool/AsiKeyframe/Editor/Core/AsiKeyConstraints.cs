using System;
using System.Collections.Generic;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 约束求值与偏移。约束在 IK 链求解阶段和链一起按依赖顺序执行（<see cref="AsiKeyEvaluator.Solve"/>），
    /// 读目标骨骼当前的世界姿势或场景物体，把被约束骨骼的局部姿势改成目标结果与原值按影响度的插值。
    /// 偏移总是生效；keepOffset 只决定添加目标时是否抓取偏移。
    /// </summary>
    public static class AsiKeyConstraints
    {
        public const float WeightEps = 1e-4f;

        public static bool ValidBone(AsiKeyProject p, AsiKeyConstraint c)
        {
            return c != null && c.bone >= 0 && c.bone < p.bones.Count;
        }

        public static bool AffectsRot(AsiConstraintKind kind)
        {
            return kind != AsiConstraintKind.Position;
        }

        public static bool AffectsPos(AsiConstraintKind kind)
        {
            return kind == AsiConstraintKind.Position || kind == AsiConstraintKind.Link;
        }

        /// <summary>这一帧的影响度（关键帧与叠加层之后，夹到 0..1）。</summary>
        public static float Influence(AsiKeyProject p, AsiKeyPose pose, int k)
        {
            AsiKeyConstraint c = p.constraints[k];
            if (!c.enabled)
                return 0f;
            float w = pose != null && k < pose.conWeight.Length ? pose.conWeight[k] : c.weight;
            return Mathf.Clamp01(w);
        }

        /// <summary>f 帧生效的链接条目：帧号不超过 f 的最后一个，早于第一个条目时为 -1（约束不起作用）。</summary>
        public static int ActiveLink(AsiKeyConstraint c, float f)
        {
            int best = -1;
            for (int i = 0; i < c.links.Count; i++)
            {
                if (c.links[i].frame <= f + 1e-4f && (best < 0 || c.links[i].frame >= c.links[best].frame))
                    best = i;
            }
            return best;
        }

        private static bool HasTargets(AsiKeyConstraint c)
        {
            for (int t = 0; t < c.targets.Count; t++)
            {
                AsiConstraintTarget tg = c.targets[t];
                if (tg.weight > 0f && (tg.bone >= 0 || !string.IsNullOrEmpty(tg.obj)))
                    return true;
            }
            return false;
        }

        /// <summary>这一帧约束是否需要求解。</summary>
        public static bool Affects(AsiKeyProject p, AsiKeyPose pose, int k)
        {
            AsiKeyConstraint c = p.constraints[k];
            if (!ValidBone(p, c) || Influence(p, pose, k) <= WeightEps)
                return false;
            if (c.kind == AsiConstraintKind.Position && !p.Movable(c.bone))
                return false;
            if (c.kind == AsiConstraintKind.Link)
                return ActiveLink(c, pose.frame) >= 0;
            return HasTargets(c);
        }

        /// <summary>约束在 f 帧读取哪些骨骼的世界姿势（含被约束骨骼的父级）。</summary>
        public static void Inputs(AsiKeyProject p, int k, float f, List<int> inputs)
        {
            AsiKeyConstraint c = p.constraints[k];
            if (!ValidBone(p, c))
                return;
            inputs.Add(p.bones[c.bone].parent);
            inputs.Add(p.LinkParent(c.bone));
            if (c.kind == AsiConstraintKind.Link)
            {
                int e = ActiveLink(c, f);
                if (e >= 0)
                    inputs.Add(c.links[e].bone);
                return;
            }
            for (int t = 0; t < c.targets.Count; t++)
                inputs.Add(c.targets[t].bone);
            if (c.kind == AsiConstraintKind.LookAt && c.upMode == AsiLookUp.Object)
                inputs.Add(c.upBone);
        }

        /// <summary>约束可能读取的全部骨骼（所有链接条目），用于环检测。</summary>
        public static void AllInputs(AsiKeyConstraint c, List<int> inputs)
        {
            for (int t = 0; t < c.targets.Count; t++)
                inputs.Add(c.targets[t].bone);
            if (c.kind == AsiConstraintKind.LookAt && c.upMode == AsiLookUp.Object)
                inputs.Add(c.upBone);
            for (int l = 0; l < c.links.Count; l++)
                inputs.Add(c.links[l].bone);
        }

        // ---------------- 环检测 ----------------

        /// <summary>骨骼 x 的世界姿势是否（沿层级、位置锚点和约束目标）依赖满足 pred 的骨骼。</summary>
        public static bool DependsOn(AsiKeyProject p, int x, Func<int, bool> pred)
        {
            return DependsOn(p, x, pred, new HashSet<int>());
        }

        private static bool DependsOn(AsiKeyProject p, int x, Func<int, bool> pred, HashSet<int> seen)
        {
            if (x < 0 || x >= p.bones.Count || !seen.Add(x))
                return false;
            if (pred(x))
                return true;
            List<int> inputs = new List<int>();
            for (int k = 0; k < p.constraints.Count; k++)
            {
                AsiKeyConstraint c = p.constraints[k];
                if (c.bone != x || !c.enabled)
                    continue;
                inputs.Clear();
                AllInputs(c, inputs);
                for (int i = 0; i < inputs.Count; i++)
                {
                    if (DependsOn(p, inputs[i], pred, seen))
                        return true;
                }
            }
            int a = p.AnchorOf(x);
            if (a >= 0 && DependsOn(p, a, pred, seen))
                return true;
            return DependsOn(p, p.bones[x].parent, pred, seen);
        }

        /// <summary>让 bone 以 target 为目标会不会形成环（target 本身受 bone 带动）。</summary>
        public static bool WouldCycle(AsiKeyProject p, int bone, int target)
        {
            if (bone < 0 || target < 0)
                return false;
            return DependsOn(p, target, x => x == bone);
        }

        /// <summary>骨骼是否经约束依赖某条 IK 链的成员（用于 IK 物体的环检测）。</summary>
        public static bool DependsOnChain(AsiKeyProject p, int bone, int chain)
        {
            if (chain < 0 || chain >= p.chains.Count)
                return false;
            int[] members = p.chains[chain].bones;
            return DependsOn(p, bone, x => Array.IndexOf(members, x) >= 0);
        }

        // ---------------- 求值 ----------------

        /// <summary>目标位姿：骨骼取当前世界姿势，物体取场景位置，都为空时是工程父级（单位矩阵）。</summary>
        public static bool TargetFrame(AsiKeyProject p, AsiKeyContext ctx, AsiKeyPose pose, int bone, string obj,
            out Vector3 pos, out Quaternion rot)
        {
            if (bone >= 0 && bone < pose.count)
            {
                pos = pose.wpos[bone];
                rot = pose.wrot[bone];
                return true;
            }
            if (!string.IsNullOrEmpty(obj))
            {
                if (ctx != null && ctx.ObjectFrame(obj, out pos, out rot))
                    return true;
                pos = Vector3.zero;
                rot = Quaternion.identity;
                return false;
            }
            pos = Vector3.zero;
            rot = Quaternion.identity;
            return bone < 0;
        }

        private static bool Average(AsiKeyProject p, AsiKeyContext ctx, AsiKeyPose pose, AsiKeyConstraint c,
            out Vector3 pos, out Quaternion rot)
        {
            Vector3 sumPos = Vector3.zero;
            Vector4 sumRot = Vector4.zero;
            Quaternion first = Quaternion.identity;
            float total = 0f;
            bool any = false;
            for (int t = 0; t < c.targets.Count; t++)
            {
                AsiConstraintTarget tg = c.targets[t];
                if (tg.weight <= 0f || (tg.bone < 0 && string.IsNullOrEmpty(tg.obj)))
                    continue;
                Vector3 tp;
                Quaternion tr;
                if (!TargetFrame(p, ctx, pose, tg.bone, tg.obj, out tp, out tr))
                    continue;
                if (!any)
                {
                    first = tr;
                    any = true;
                }
                if (Quaternion.Dot(first, tr) < 0f)
                    tr = new Quaternion(-tr.x, -tr.y, -tr.z, -tr.w);
                sumPos += tp * tg.weight;
                sumRot += new Vector4(tr.x, tr.y, tr.z, tr.w) * tg.weight;
                total += tg.weight;
            }
            if (!any || total <= 0f)
            {
                pos = Vector3.zero;
                rot = Quaternion.identity;
                return false;
            }
            pos = sumPos / total;
            rot = AsiKeyMath.Norm(new Quaternion(sumRot.x, sumRot.y, sumRot.z, sumRot.w));
            return true;
        }

        /// <summary>注视：把骨骼局部 aim 轴转向 dir、up 轴尽量朝 upW 的世界旋转（不含偏移）。</summary>
        private static Quaternion Look(AsiKeyConstraint c, Quaternion current, Vector3 dir, Vector3 upW, bool useUp)
        {
            Vector3 aim = AsiKeyConstraint.AxisVector(c.aimAxis);
            if (!useUp)
                return AsiKeyMath.Norm(Quaternion.FromToRotation(current * aim, dir) * current);
            int upAxis = c.upAxis % 3 == c.aimAxis % 3 ? (c.aimAxis + 1) % 3 : c.upAxis;
            Vector3 up = AsiKeyConstraint.AxisVector(upAxis);
            Quaternion look = Quaternion.LookRotation(dir, upW);
            Quaternion basis = Quaternion.LookRotation(aim, up);
            return AsiKeyMath.Norm(look * Quaternion.Inverse(basis));
        }

        /// <summary>
        /// 不考虑影响度时约束给出的世界位姿（含偏移）。withOffset 为 false 时不乘偏移（抓取偏移用）。
        /// 链接约束 entry 指定条目，-1 时取 f 帧生效的条目。
        /// </summary>
        public static bool Desired(AsiKeyProject p, AsiKeyContext ctx, AsiKeyPose pose, int k, bool withOffset,
            int entry, out Vector3 wp, out Quaternion wr)
        {
            AsiKeyConstraint c = p.constraints[k];
            int b = c.bone;
            wp = pose.wpos[b];
            wr = pose.wrot[b];
            Vector3 avgPos;
            Quaternion avgRot;
            switch (c.kind)
            {
                case AsiConstraintKind.Position:
                    if (!Average(p, ctx, pose, c, out avgPos, out avgRot))
                        return false;
                    wp = withOffset ? avgPos + c.offPos : avgPos;
                    return true;
                case AsiConstraintKind.Orientation:
                    if (!Average(p, ctx, pose, c, out avgPos, out avgRot))
                        return false;
                    wr = withOffset ? AsiKeyMath.Norm(avgRot * c.offRot) : avgRot;
                    return true;
                case AsiConstraintKind.LookAt:
                {
                    if (!Average(p, ctx, pose, c, out avgPos, out avgRot))
                        return false;
                    Vector3 dir = avgPos - pose.wpos[b];
                    if (dir.sqrMagnitude < 1e-10f)
                        return false;
                    Vector3 upW = Vector3.up;
                    bool useUp = c.upMode != AsiLookUp.None;
                    if (c.upMode == AsiLookUp.Object)
                    {
                        Vector3 up;
                        Quaternion upRot;
                        if (TargetFrame(p, ctx, pose, c.upBone, c.upObj, out up, out upRot))
                            upW = upRot * AsiKeyConstraint.AxisVector(c.upTargetAxis);
                    }
                    Quaternion current = withOffset ? AsiKeyMath.Norm(pose.wrot[b] * Quaternion.Inverse(c.offRot)) : pose.wrot[b];
                    Quaternion r = Look(c, current, dir.normalized, upW, useUp);
                    wr = withOffset ? AsiKeyMath.Norm(r * c.offRot) : r;
                    return true;
                }
                case AsiConstraintKind.Link:
                {
                    int bone;
                    string obj;
                    Vector3 offPos;
                    Quaternion offRot;
                    if (entry >= 0 && entry < c.links.Count)
                    {
                        AsiLinkEntry raw = c.links[entry];
                        bone = raw.bone;
                        obj = raw.obj;
                        offPos = raw.offPos;
                        offRot = raw.offRot;
                    }
                    else if (!SampleLink(c, pose.frame, out bone, out obj, out offPos, out offRot))
                    {
                        return false;
                    }
                    Vector3 tp;
                    Quaternion tr;
                    if (!TargetFrame(p, ctx, pose, bone, obj, out tp, out tr))
                        return false;
                    if (withOffset)
                    {
                        wp = tp + tr * offPos;
                        wr = AsiKeyMath.Norm(tr * offRot);
                    }
                    else
                    {
                        wp = tp;
                        wr = tr;
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>求解一个约束：把被约束骨骼的局部姿势改成约束结果与当前值按影响度的插值。调用后需要 ComputeWorld。</summary>
        public static void Solve(AsiKeyProject p, AsiKeyContext ctx, AsiKeyPose pose, int k)
        {
            AsiKeyConstraint c = p.constraints[k];
            float inf = Influence(p, pose, k);
            if (!ValidBone(p, c) || inf <= WeightEps)
                return;
            Vector3 wp;
            Quaternion wr;
            if (!Desired(p, ctx, pose, k, true, -1, out wp, out wr))
                return;
            int b = c.bone;
            Vector3 ppos;
            Quaternion prot;
            pose.ParentFrame(p, b, out ppos, out prot);
            Quaternion pinv = Quaternion.Inverse(prot);
            if (AffectsRot(c.kind))
            {
                Quaternion want = inf >= 1f - WeightEps ? wr : Quaternion.Slerp(pose.wrot[b], wr, inf);
                pose.rot[b] = AsiKeyMath.Norm(pinv * want);
            }
            if (AffectsPos(c.kind) && p.Movable(b))
            {
                Vector3 want = inf >= 1f - WeightEps ? wp : Vector3.Lerp(pose.wpos[b], wp, inf);
                pose.pos[b] = pinv * (want - ppos);
            }
        }

        // ---------------- 偏移 ----------------

        private static readonly AsiKeyPose s_pose = new AsiKeyPose();

        /// <summary>关掉约束 k、只保留前 linkCount 个链接条目（-1 为全部）求 f 帧的姿势。</summary>
        private static AsiKeyPose EvaluateWithout(AsiKeyProject p, AsiKeyContext ctx, float f, int k, int linkCount)
        {
            AsiKeyConstraint c = p.constraints[k];
            bool enabled = c.enabled;
            List<AsiLinkEntry> links = c.links;
            try
            {
                if (linkCount < 0)
                    c.enabled = false;
                else
                    c.links = SortedLinks(c).GetRange(0, Mathf.Min(linkCount, c.links.Count));
                AsiKeyEvaluator.Evaluate(p, ctx, f, s_pose, int.MaxValue, -1);
            }
            finally
            {
                c.enabled = enabled;
                c.links = links;
            }
            return s_pose;
        }

        private static List<AsiLinkEntry> SortedLinks(AsiKeyConstraint c)
        {
            List<AsiLinkEntry> sorted = new List<AsiLinkEntry>(c.links);
            sorted.Sort((a, b) => a.frame.CompareTo(b.frame));
            return sorted;
        }

        public static void SortLinks(AsiKeyConstraint c)
        {
            c.links.Sort((a, b) => a.frame.CompareTo(b.frame));
        }

        /// <summary>两条链接是不是跟同一个对象（含都是世界）。</summary>
        public static bool SameLinkTarget(AsiLinkEntry a, AsiLinkEntry b)
        {
            if (a == null || b == null)
                return false;
            if (a.bone != b.bone)
                return false;
            if (a.bone >= 0)
                return true;
            return (a.obj ?? "") == (b.obj ?? "");
        }

        /// <summary>
        /// f 帧的链接父级和相对偏移。同一父级的相邻条目之间，偏移按时间插值；
        /// 父级变了就保持上一条，到下一条那一帧再切换。
        /// </summary>
        public static bool SampleLink(AsiKeyConstraint c, float f, out int bone, out string obj, out Vector3 offPos, out Quaternion offRot)
        {
            bone = -1;
            obj = "";
            offPos = Vector3.zero;
            offRot = Quaternion.identity;
            if (c == null || c.links == null || c.links.Count == 0)
                return false;
            SortLinks(c);
            int e = ActiveLink(c, f);
            if (e < 0)
                return false;
            AsiLinkEntry le = c.links[e];
            bone = le.bone;
            obj = le.obj ?? "";
            offPos = le.offPos;
            offRot = le.offRot;
            if (e + 1 >= c.links.Count)
                return true;
            AsiLinkEntry next = c.links[e + 1];
            float span = next.frame - le.frame;
            if (span <= 1e-4f || f >= next.frame - 1e-4f || !SameLinkTarget(le, next))
                return true;
            float u = Mathf.Clamp01((f - le.frame) / span);
            offPos = Vector3.Lerp(le.offPos, next.offPos, u);
            offRot = Quaternion.Slerp(le.offRot, next.offRot, u);
            return true;
        }

        /// <summary>当前帧还没有链接条目时，按这一帧的相对位姿复制一条（父级不变）。已有则直接返回。</summary>
        public static AsiLinkEntry EnsureEntryAtFrame(AsiKeyConstraint c, float frame)
        {
            if (c == null)
                return null;
            frame = Mathf.Round(frame * 100f) / 100f;
            SortLinks(c);
            for (int i = 0; i < c.links.Count; i++)
            {
                if (Mathf.Abs(c.links[i].frame - frame) < 0.01f)
                    return c.links[i];
            }
            int bone;
            string obj;
            Vector3 offPos;
            Quaternion offRot;
            if (!SampleLink(c, frame, out bone, out obj, out offPos, out offRot))
                return null;
            AsiLinkEntry le = new AsiLinkEntry();
            le.frame = frame;
            le.bone = bone;
            le.obj = bone >= 0 ? "" : obj ?? "";
            le.offPos = offPos;
            le.offRot = offRot;
            c.links.Add(le);
            SortLinks(c);
            return le;
        }

        /// <summary>按 f 帧（关掉约束时）骨骼的位姿抓取偏移，让约束生效后骨骼保持原位。链接约束改用 <see cref="GrabLinkOffsets"/>。</summary>
        public static void GrabOffset(AsiKeyProject p, AsiKeyContext ctx, float f, int k)
        {
            AsiKeyConstraint c = p.constraints[k];
            if (!ValidBone(p, c) || c.kind == AsiConstraintKind.Link)
                return;
            AsiKeyPose pose = EvaluateWithout(p, ctx, f, k, -1);
            Vector3 raw;
            Quaternion rawRot;
            if (!Desired(p, ctx, pose, k, false, -1, out raw, out rawRot))
            {
                ClearOffset(c);
                return;
            }
            int b = c.bone;
            if (c.kind == AsiConstraintKind.Position)
                c.offPos = pose.wpos[b] - raw;
            else
                c.offRot = AsiKeyMath.Norm(Quaternion.Inverse(rawRot) * pose.wrot[b]);
        }

        public static void ClearOffset(AsiKeyConstraint c)
        {
            c.offPos = Vector3.zero;
            c.offRot = Quaternion.identity;
        }

        /// <summary>
        /// 按帧顺序逐个抓取链接条目的偏移：在条目帧上、只保留它之前的条目求骨骼位姿，换算到新父级空间。
        /// from 之前的条目保持不变。
        /// </summary>
        public static void GrabLinkOffsets(AsiKeyProject p, AsiKeyContext ctx, int k, int from = 0)
        {
            AsiKeyConstraint c = p.constraints[k];
            if (!ValidBone(p, c) || c.kind != AsiConstraintKind.Link)
                return;
            SortLinks(c);
            int b = c.bone;
            for (int e = Mathf.Max(0, from); e < c.links.Count; e++)
            {
                AsiLinkEntry le = c.links[e];
                AsiKeyPose pose = EvaluateWithout(p, ctx, le.frame, k, e);
                Vector3 tp;
                Quaternion tr;
                TargetFrame(p, ctx, pose, le.bone, le.obj, out tp, out tr);
                Quaternion inv = Quaternion.Inverse(tr);
                le.offPos = inv * (pose.wpos[b] - tp);
                le.offRot = AsiKeyMath.Norm(inv * pose.wrot[b]);
            }
        }

        // ---------------- 编辑 ----------------

        public enum EditRoute { Free, Offset, Blocked }

        /// <summary>
        /// 编辑被约束骨骼的旋转（rot）或位置（!rot）时的处理方式：没有生效的约束管这个通道时直接改关键帧；
        /// 影响度为 1 时改写约束偏移；影响度在 0..1 之间时拒绝。
        /// </summary>
        public static EditRoute Route(AsiKeyProject p, AsiKeyPose pose, int bone, bool rot, out int constraint)
        {
            constraint = -1;
            EditRoute route = EditRoute.Free;
            for (int k = 0; k < p.constraints.Count; k++)
            {
                AsiKeyConstraint c = p.constraints[k];
                if (c.bone != bone || !Affects(p, pose, k))
                    continue;
                if (rot ? !AffectsRot(c.kind) : !AffectsPos(c.kind))
                    continue;
                if (Influence(p, pose, k) < 1f - WeightEps)
                {
                    constraint = k;
                    return EditRoute.Blocked;
                }
                constraint = k;
                route = EditRoute.Offset;
            }
            return route;
        }

        /// <summary>影响度为 1 时，改写约束 k 的偏移，让骨骼的世界旋转变成 want。</summary>
        public static void RewriteRot(AsiKeyProject p, AsiKeyContext ctx, AsiKeyPose pose, int k, Quaternion want)
        {
            AsiKeyConstraint c = p.constraints[k];
            Quaternion cur = pose.wrot[c.bone];
            Quaternion delta = Quaternion.Inverse(cur) * want;
            if (c.kind == AsiConstraintKind.Link)
            {
                AsiLinkEntry le = EnsureEntryAtFrame(c, pose.frame);
                if (le != null)
                    le.offRot = AsiKeyMath.Norm(le.offRot * delta);
                return;
            }
            c.offRot = AsiKeyMath.Norm(c.offRot * delta);
        }

        /// <summary>影响度为 1 时，改写约束 k 的偏移，让骨骼的世界位置变成 want。</summary>
        public static void RewritePos(AsiKeyProject p, AsiKeyContext ctx, AsiKeyPose pose, int k, Vector3 want)
        {
            AsiKeyConstraint c = p.constraints[k];
            Vector3 d = want - pose.wpos[c.bone];
            if (c.kind == AsiConstraintKind.Link)
            {
                AsiLinkEntry le = EnsureEntryAtFrame(c, pose.frame);
                if (le == null)
                    return;
                Vector3 tp;
                Quaternion tr;
                TargetFrame(p, ctx, pose, le.bone, le.obj, out tp, out tr);
                le.offPos += Quaternion.Inverse(tr) * d;
                return;
            }
            c.offPos += d;
        }

        /// <summary>编辑前的偏移快照（变换轴拖动中每次从快照重算）。</summary>
        public struct OffsetSnapshot
        {
            public int constraint;
            public Vector3 offPos;
            public Quaternion offRot;
            public Vector3[] linkPos;
            public Quaternion[] linkRot;
            public float[] linkFrame;
        }

        public static OffsetSnapshot Snapshot(AsiKeyProject p, int k)
        {
            OffsetSnapshot s = new OffsetSnapshot { constraint = k };
            if (k < 0 || k >= p.constraints.Count)
                return s;
            AsiKeyConstraint c = p.constraints[k];
            s.offPos = c.offPos;
            s.offRot = c.offRot;
            s.linkPos = new Vector3[c.links.Count];
            s.linkRot = new Quaternion[c.links.Count];
            s.linkFrame = new float[c.links.Count];
            for (int i = 0; i < c.links.Count; i++)
            {
                s.linkPos[i] = c.links[i].offPos;
                s.linkRot[i] = c.links[i].offRot;
                s.linkFrame[i] = c.links[i].frame;
            }
            return s;
        }

        public static void Restore(AsiKeyProject p, OffsetSnapshot s)
        {
            if (s.constraint < 0 || s.constraint >= p.constraints.Count)
                return;
            AsiKeyConstraint c = p.constraints[s.constraint];
            c.offPos = s.offPos;
            c.offRot = s.offRot;
            if (s.linkFrame != null)
            {
                for (int i = c.links.Count - 1; i >= 0; i--)
                {
                    if (SnapshotLink(s, c.links[i].frame) < 0)
                        c.links.RemoveAt(i);
                }
            }
            for (int i = 0; i < c.links.Count && s.linkPos != null && i < s.linkPos.Length; i++)
            {
                int src = s.linkFrame != null ? SnapshotLink(s, c.links[i].frame) : i;
                if (src < 0 || src >= s.linkPos.Length)
                    continue;
                c.links[i].offPos = s.linkPos[src];
                c.links[i].offRot = s.linkRot[src];
            }
        }

        private static int SnapshotLink(OffsetSnapshot s, float frame)
        {
            if (s.linkFrame == null)
                return -1;
            for (int i = 0; i < s.linkFrame.Length; i++)
            {
                if (Mathf.Abs(s.linkFrame[i] - frame) < 0.01f)
                    return i;
            }
            return -1;
        }

        // ---------------- 结构维护 ----------------

        /// <summary>删除骨骼后：删掉约束在该骨骼上的约束（返回被删的约束下标，降序），移除指向它的目标，其余下标按 map 重映射。</summary>
        public static List<int> RemapBones(AsiKeyProject p, Func<int, int> map)
        {
            List<int> removed = new List<int>();
            for (int k = p.constraints.Count - 1; k >= 0; k--)
            {
                AsiKeyConstraint c = p.constraints[k];
                int nb = c.bone >= 0 ? map(c.bone) : -1;
                if (nb < 0)
                {
                    removed.Add(k);
                    continue;
                }
                c.bone = nb;
                for (int t = c.targets.Count - 1; t >= 0; t--)
                {
                    AsiConstraintTarget tg = c.targets[t];
                    if (tg.bone < 0)
                        continue;
                    tg.bone = map(tg.bone);
                    if (tg.bone < 0)
                        c.targets.RemoveAt(t);
                }
                if (c.upBone >= 0)
                    c.upBone = map(c.upBone);
                for (int l = c.links.Count - 1; l >= 0; l--)
                {
                    AsiLinkEntry le = c.links[l];
                    if (le.bone < 0)
                        continue;
                    le.bone = map(le.bone);
                    if (le.bone < 0)
                        c.links.RemoveAt(l);
                }
            }
            for (int r = 0; r < removed.Count; r++)
                RemoveAt(p, removed[r]);
            return removed;
        }

        /// <summary>删除约束 k 及其影响度轨道，后面约束的轨道下标前移。</summary>
        public static void RemoveAt(AsiKeyProject p, int k)
        {
            p.constraints.RemoveAt(k);
            for (int l = 0; l < p.layers.Count; l++)
            {
                List<AsiKeyTrack> tracks = p.layers[l].tracks;
                for (int t = tracks.Count - 1; t >= 0; t--)
                {
                    AsiKeyTrack tr = tracks[t];
                    if (tr.kind != AsiTrackKind.Constraint)
                        continue;
                    if (tr.target == k)
                        tracks.RemoveAt(t);
                    else if (tr.target > k)
                        tr.target--;
                }
            }
        }
    }
}
