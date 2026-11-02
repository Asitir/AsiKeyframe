using System.Collections.Generic;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 求值流程：基础层轨道 → 「保持底层 IK」的叠加层（IK 前）→ IK 求解与 IKBlend 混合 → 其余叠加层（IK 后）。
    /// </summary>
    public static class AsiKeyEvaluator
    {
        private static Quaternion[] s_ik = new Quaternion[16];
        private static readonly List<int> s_remaining = new List<int>();
        private static readonly List<int> s_chainOrder = new List<int>();

        /// <param name="layerLimit">只叠加到这一层（含）。</param>
        /// <param name="split">这一层若在 IK 之后叠加，它的增量单独放进 actRot/actPos，供编辑时换算。</param>
        public static void Evaluate(AsiKeyProject p, AsiKeyContext ctx, float f, AsiKeyPose pose, int layerLimit, int split)
        {
            EvaluateFK(p, f, pose, layerLimit, split);
            Solve(p, ctx, pose);
        }

        public static void EvaluateFK(AsiKeyProject p, float f, AsiKeyPose pose, int layerLimit, int split)
        {
            p.EnsureLayers();
            pose.Ensure(p);
            pose.ResetToRest(p);
            pose.frame = f;

            List<AsiKeyTrack> baseTracks = p.layers[0].tracks;
            for (int t = 0; t < baseTracks.Count; t++)
            {
                AsiKeyTrack tr = baseTracks[t];
                if (tr.keys.Count == 0)
                    continue;
                ApplyAbsolute(p, tr, f, pose);
            }

            int last = Mathf.Min(layerLimit, p.layers.Count - 1);
            for (int l = 1; l <= last; l++)
            {
                AsiKeyLayer layer = p.layers[l];
                float w = LayerWeightAt(p, l, f);
                if (layer.mute || w <= 1e-4f)
                    continue;
                int target = layer.keepBaseIK ? 0 : (l == split ? 2 : 1);
                for (int t = 0; t < layer.tracks.Count; t++)
                {
                    AsiKeyTrack tr = layer.tracks[t];
                    if (tr.keys.Count == 0 || tr.kind == AsiTrackKind.LayerWeight)
                        continue;
                    ApplyDelta(p, tr, f, pose, w, target, layer.overwrite);
                }
            }
        }

        /// <summary>这一层在 f 帧的权重。有权重关键帧时用关键帧，否则用层上的权重。</summary>
        public static float LayerWeightAt(AsiKeyProject p, int layer, float f)
        {
            if (layer <= 0 || layer >= p.layers.Count)
                return 1f;
            AsiKeyLayer ly = p.layers[layer];
            AsiKeyTrack tr = p.FindTrack(layer, AsiTrackKind.LayerWeight, layer);
            if (tr == null || tr.keys.Count == 0)
                return ly.weight;
            return Mathf.Clamp01(AsiKeyTrackEval.EvalPos(tr, 1, 0, f, new Vector3(ly.weight, 0f, 0f)).x);
        }

        private static bool ValidCom(AsiKeyPose pose, int com)
        {
            return com >= 0 && com < pose.count;
        }

        /// <summary>质心轨道的 target 是该单位质心的下标，无效时为 -1。</summary>
        public static int ComTarget(AsiKeyProject p, AsiKeyTrack tr)
        {
            int t = tr.target;
            return t >= 0 && t < p.bones.Count && p.bones[t].isCom ? t : -1;
        }

        private static void ApplyAbsolute(AsiKeyProject p, AsiKeyTrack tr, float f, AsiKeyPose pose)
        {
            int rotCh = p.MemberCount(tr) * 3;
            int com = tr.kind <= AsiTrackKind.ComRotation ? ComTarget(p, tr) : -1;
            switch (tr.kind)
            {
                case AsiTrackKind.ComHorizontal:
                    if (ValidCom(pose, com))
                        pose.fkPos[com] = AsiKeyTrackEval.EvalPos(tr, 5, rotCh, f, pose.fkPos[com]);
                    break;
                case AsiTrackKind.ComVertical:
                    if (ValidCom(pose, com))
                        pose.fkPos[com] = AsiKeyTrackEval.EvalPos(tr, 2, rotCh, f, pose.fkPos[com]);
                    break;
                case AsiTrackKind.ComRotation:
                    if (ValidCom(pose, com))
                        pose.fk[com] = AsiKeyMath.Norm(AsiKeyTrackEval.EvalRot(tr, 0, f, pose.fk[com]));
                    break;
                case AsiTrackKind.Constraint:
                    if (tr.target >= 0 && tr.target < pose.conWeight.Length)
                        pose.conWeight[tr.target] = AsiKeyTrackEval.EvalPos(tr, 1, 0, f, new Vector3(pose.conWeight[tr.target], 0f, 0f)).x;
                    break;
                case AsiTrackKind.Bone:
                {
                    int b = tr.target;
                    if (b < 0 || b >= pose.count)
                        break;
                    pose.fk[b] = AsiKeyMath.Norm(AsiKeyTrackEval.EvalRot(tr, 0, f, pose.fk[b]));
                    if (p.bones[b].animatePosition)
                        pose.fkPos[b] = AsiKeyTrackEval.EvalPos(tr, 7, rotCh, f, pose.fkPos[b]);
                    break;
                }
                case AsiTrackKind.Chain:
                {
                    if (tr.target < 0 || tr.target >= p.chains.Count)
                        break;
                    AsiKeyChain chain = p.chains[tr.target];
                    for (int m = 0; m < chain.bones.Length; m++)
                    {
                        int b = chain.bones[m];
                        pose.fk[b] = AsiKeyMath.Norm(AsiKeyTrackEval.EvalRot(tr, m, f, pose.fk[b]));
                    }
                    AsiChainState st = EvalChainState(p, chain, tr, f);
                    if (st.valid && chain.bones.Length > 0)
                    {
                        st.refRot = pose.fk[chain.bones[0]];
                        st.hasRef = true;
                    }
                    pose.chains[tr.target] = st;
                    break;
                }
            }
        }

        private static void ApplyDelta(AsiKeyProject p, AsiKeyTrack tr, float f, AsiKeyPose pose, float w, int target, bool overwrite)
        {
            if (tr.kind == AsiTrackKind.LayerWeight)
                return;
            if (tr.kind == AsiTrackKind.Constraint)
            {
                if (tr.target >= 0 && tr.target < pose.conWeight.Length)
                    pose.conWeight[tr.target] += AsiKeyTrackEval.EvalPos(tr, 1, 0, f, Vector3.zero).x * w;
                return;
            }
            int rotCh = p.MemberCount(tr) * 3;
            int members = p.MemberCount(tr);
            for (int m = 0; m < members; m++)
            {
                int b = p.TrackBone(tr, m);
                if (b < 0 || b >= pose.count)
                    continue;
                Quaternion q = AsiKeyMath.Norm(AsiKeyTrackEval.EvalRot(tr, m, f, Quaternion.identity));
                Quaternion delta = overwrite ? OverwriteDelta(CurrentRot(pose, b, target), q, w) : AsiKeyMath.Scale(q, w);
                if (target == 0) pose.fk[b] = pose.fk[b] * delta;
                else if (target == 1) pose.postRot[b] = pose.postRot[b] * delta;
                else pose.actRot[b] = pose.actRot[b] * delta;
            }
            int mask = p.PosMask(tr);
            if (mask == 0)
                return;
            int pb = tr.kind == AsiTrackKind.Bone ? tr.target : ComTarget(p, tr);
            if (pb < 0 || pb >= pose.count)
                return;
            Vector3 abs = AsiKeyTrackEval.EvalPos(tr, mask, rotCh, f, Vector3.zero);
            Vector3 dp = overwrite ? OverwritePos(CurrentPos(pose, pb, target), abs, mask, w) : Masked(abs, mask) * w;
            if (target == 0) pose.fkPos[pb] += dp;
            else if (target == 1) pose.postPos[pb] += dp;
            else pose.actPos[pb] += dp;
        }

        private static Quaternion CurrentRot(AsiKeyPose pose, int b, int target)
        {
            if (target == 1)
                return pose.fk[b] * pose.postRot[b];
            if (target == 2)
                return pose.fk[b] * pose.postRot[b] * pose.actRot[b];
            return pose.fk[b];
        }

        private static Vector3 CurrentPos(AsiKeyPose pose, int b, int target)
        {
            if (target == 1)
                return pose.fkPos[b] + pose.postPos[b];
            if (target == 2)
                return pose.fkPos[b] + pose.postPos[b] + pose.actPos[b];
            return pose.fkPos[b];
        }

        /// <summary>覆写：权重 1 时变成关键帧里的绝对局部旋转，否则从当前值转到它。</summary>
        private static Quaternion OverwriteDelta(Quaternion current, Quaternion absolute, float w)
        {
            Quaternion toward = AsiKeyMath.Scale(AsiKeyMath.Norm(Quaternion.Inverse(current) * absolute), w);
            return toward;
        }

        private static Vector3 Masked(Vector3 v, int mask)
        {
            if ((mask & 1) == 0) v.x = 0f;
            if ((mask & 2) == 0) v.y = 0f;
            if ((mask & 4) == 0) v.z = 0f;
            return v;
        }

        private static Vector3 OverwritePos(Vector3 current, Vector3 absolute, int mask, float w)
        {
            Vector3 d = Vector3.zero;
            if ((mask & 1) != 0) d.x = (absolute.x - current.x) * w;
            if ((mask & 2) != 0) d.y = (absolute.y - current.y) * w;
            if ((mask & 4) != 0) d.z = (absolute.z - current.z) * w;
            return d;
        }

        private static AsiChainTarget TargetOf(AsiKey k)
        {
            AsiChainTarget t;
            t.space = k.ikSpace;
            t.bone = k.ikBone;
            t.obj = k.ikObject;
            t.pos = k.effPos;
            t.rot = AsiKeyMath.Norm(k.effRot);
            return t;
        }

        private static AsiChainState FromKey(AsiKey k)
        {
            AsiChainState s = new AsiChainState();
            s.valid = true;
            s.blend = Mathf.Clamp01(k.ikBlend);
            s.swivel = k.swivel;
            s.pivot = k.pivot;
            s.join = k.joinToPrev;
            s.type = k.type;
            s.a = TargetOf(k);
            s.b = s.a;
            s.mix = 0f;
            return s;
        }

        public static bool Joined(AsiKey a, AsiKey b)
        {
            return b.joinToPrev && a.ikBlend > 0.5f && b.ikBlend > 0.5f && a.ikSpace != AsiIKSpace.Body && a.SameSpace(b);
        }

        public static AsiChainState EvalChainState(AsiKeyProject p, AsiKeyChain chain, AsiKeyTrack tr, float f)
        {
            List<AsiKey> keys = tr.keys;
            int n = keys.Count;
            if (n == 0)
                return default(AsiChainState);
            int i;
            float u;
            int state = AsiKeyTrackEval.Locate(keys, f, out i, out u);
            if (state == AsiKeyTrackEval.Before)
                return FromKey(keys[0]);
            if (state == AsiKeyTrackEval.After)
                return FromKey(keys[n - 1]);

            AsiKey ka = keys[i];
            AsiKey kb = keys[i + 1];
            float eu = tr.mode == AsiInterpMode.TCB ? AsiKeyInterpTCB.EaseU(ka, kb, u) : u;
            AsiChainState s = new AsiChainState();
            s.valid = true;
            s.blend = Mathf.Clamp01(Mathf.Lerp(ka.ikBlend, kb.ikBlend, eu));
            s.swivel = SwivelAt(tr, i, eu, f);
            s.pivot = ka.pivot;
            s.join = ka.joinToPrev;
            s.type = ka.type;

            if (!ka.SameSpace(kb))
            {
                s.a = TargetOf(ka);
                s.b = TargetOf(kb);
                s.mix = eu;
                return s;
            }

            bool has0 = i > 0 && keys[i - 1].SameSpace(ka);
            bool has3 = i + 2 < n && keys[i + 2].SameSpace(kb);
            AsiKey k0 = has0 ? keys[i - 1] : ka;
            AsiKey k3 = has3 ? keys[i + 2] : kb;
            float n0 = has0 ? ka.frame - k0.frame : 0f;
            float n1 = kb.frame - ka.frame;
            float n2 = has3 ? k3.frame - kb.frame : 0f;

            Quaternion rot = AsiKeyInterpTCB.SegQuat(k0.effRot, ka.effRot, kb.effRot, k3.effRot, has0, has3, ka, kb, n0, n1, n2, eu);
            Vector3 pos;
            if (Joined(ka, kb))
            {
                Vector3 piv = p.PivotOf(chain, ka.pivot);
                Vector3 anchor = ka.effPos + ka.effRot * piv;
                pos = anchor - rot * piv;
            }
            else
            {
                pos = AsiKeyInterpTCB.Seg(k0.effPos, ka.effPos, kb.effPos, k3.effPos, has0, has3, ka, kb, n0, n1, n2, eu);
            }
            s.a = TargetOf(ka);
            s.a.pos = pos;
            s.a.rot = AsiKeyMath.Norm(rot);
            s.b = s.a;
            s.mix = 0f;
            return s;
        }

        /// <summary>
        /// 段 i→i+1 内 eu（TCB 缓动后）/ f 处的 swivel，按链轨道的补间方式：TCB 用关键帧的张力 / 连续 / 偏移，
        /// 曲线用自动切线（swivel 没有自己的切线数据）。不看目标空间，相邻关键帧都参与。
        /// </summary>
        private static float SwivelAt(AsiKeyTrack tr, int i, float eu, float f)
        {
            List<AsiKey> keys = tr.keys;
            AsiKey ka = keys[i];
            AsiKey kb = keys[i + 1];
            bool has0 = i > 0;
            bool has3 = i + 2 < keys.Count;
            AsiKey k0 = has0 ? keys[i - 1] : ka;
            AsiKey k3 = has3 ? keys[i + 2] : kb;
            if (tr.mode == AsiInterpMode.Curve)
                return AsiKeyInterpCurve.AutoSeg(k0.swivel, ka.swivel, kb.swivel, k3.swivel, has0, has3, k0.frame, ka.frame, kb.frame, k3.frame, f);
            float n0 = has0 ? ka.frame - k0.frame : 0f;
            float n1 = kb.frame - ka.frame;
            float n2 = has3 ? k3.frame - kb.frame : 0f;
            return AsiKeyInterpTCB.Seg(k0.swivel, ka.swivel, kb.swivel, k3.swivel, has0, has3, ka, kb, n0, n1, n2, eu);
        }

        /// <summary>
        /// 空间坐标系在工程父级空间中的位姿。需要在该链求解前调用，Body 用父控制器当前的世界姿势；
        /// World 是链所属单位的坐标系（角色整体移动时目标跟着走）。
        /// </summary>
        public static void SpaceFrame(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose, AsiKeyContext ctx,
            AsiIKSpace space, int bone, string obj, out Vector3 fpos, out Quaternion frot)
        {
            int root = chain.bones.Length > 0 ? chain.bones[0] : -1;
            int unit = root >= 0 ? p.bones[root].unit : 0;
            fpos = pose.UnitPos(unit);
            frot = pose.UnitRot(unit);
            if (space == AsiIKSpace.Body)
            {
                if (root >= 0)
                    pose.ParentFrame(p, root, out fpos, out frot);
                return;
            }
            if (space == AsiIKSpace.Object)
            {
                if (bone >= 0 && bone < pose.count)
                {
                    fpos = pose.wpos[bone];
                    frot = pose.wrot[bone];
                    return;
                }
                Vector3 op;
                Quaternion or;
                if (ctx != null && !string.IsNullOrEmpty(obj) && ctx.ObjectFrame(obj, out op, out or))
                {
                    fpos = op;
                    frot = or;
                }
            }
        }

        public static void ResolveTarget(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose, AsiKeyContext ctx,
            AsiChainTarget t, out Vector3 pos, out Quaternion rot)
        {
            Vector3 fpos;
            Quaternion frot;
            SpaceFrame(p, chain, pose, ctx, t.space, t.bone, t.obj, out fpos, out frot);
            pos = fpos + frot * t.pos;
            rot = frot * t.rot;
        }

        public static void ResolveState(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose, AsiKeyContext ctx,
            AsiChainState s, out Vector3 pos, out Quaternion rot)
        {
            ResolveTarget(p, chain, pose, ctx, s.a, out pos, out rot);
            if (s.mix <= 1e-5f)
                return;
            Vector3 pb;
            Quaternion rb;
            ResolveTarget(p, chain, pose, ctx, s.b, out pb, out rb);
            pos = Vector3.Lerp(pos, pb, s.mix);
            rot = Quaternion.Slerp(rot, rb, s.mix);
        }

        /// <summary>把角色根空间的末端姿势换算进指定空间。</summary>
        public static AsiChainTarget ToSpace(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose, AsiKeyContext ctx,
            AsiIKSpace space, int bone, string obj, Vector3 rigPos, Quaternion rigRot)
        {
            Vector3 fpos;
            Quaternion frot;
            SpaceFrame(p, chain, pose, ctx, space, bone, obj, out fpos, out frot);
            Quaternion inv = Quaternion.Inverse(frot);
            AsiChainTarget t;
            t.space = space;
            t.bone = bone;
            t.obj = obj ?? "";
            t.pos = inv * (rigPos - fpos);
            t.rot = AsiKeyMath.Norm(inv * rigRot);
            return t;
        }

        // ---------------- 求解步骤（IK 链与约束）的依赖排序 ----------------
        // 步骤编码：链 c 为 c，约束 k 为 ~k（负数）。

        private static readonly List<int> s_inputs = new List<int>();

        /// <summary>步骤读取哪些骨骼的世界姿势。</summary>
        private static void StepInputs(AsiKeyProject p, AsiKeyPose pose, int step, List<int> inputs)
        {
            inputs.Clear();
            if (step >= 0)
            {
                AsiKeyChain chain = p.chains[step];
                if (chain.bones.Length == 0)
                    return;
                AsiChainState s = pose.chains[step];
                inputs.Add(p.LinkParent(chain.bones[0]));
                inputs.Add(p.bones[chain.bones[0]].parent);
                if (s.a.space == AsiIKSpace.Object)
                    inputs.Add(s.a.bone);
                if (s.mix > 0f && s.b.space == AsiIKSpace.Object)
                    inputs.Add(s.b.bone);
                return;
            }
            AsiKeyConstraints.Inputs(p, ~step, pose.frame, inputs);
        }

        /// <summary>骨骼 x 的世界姿势是否受步骤 step 影响。</summary>
        private static bool StepAffects(AsiKeyProject p, int step, int x)
        {
            if (x < 0)
                return false;
            if (step >= 0)
                return p.IsUnderChain(x, step);
            int b = p.constraints[~step].bone;
            return b >= 0 && p.IsUnder(x, b);
        }

        private static bool StepDependsOn(AsiKeyProject p, AsiKeyPose pose, int a, int b)
        {
            StepInputs(p, pose, a, s_inputs);
            for (int k = 0; k < s_inputs.Count; k++)
            {
                if (StepAffects(p, b, s_inputs[k]))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 按依赖排序 IK 链与约束：IK 物体、身体空间父级、约束目标挂在别的步骤影响的骨骼下面时，先解那一步。有环时按下标顺序兜底。
        /// </summary>
        private static void OrderSteps(AsiKeyProject p, AsiKeyPose pose)
        {
            s_remaining.Clear();
            s_chainOrder.Clear();
            for (int c = 0; c < p.chains.Count; c++)
            {
                if (pose.chains[c].valid && pose.chains[c].blend > 1e-4f)
                    s_remaining.Add(c);
            }
            for (int k = 0; k < p.constraints.Count; k++)
            {
                if (AsiKeyConstraints.Affects(p, pose, k))
                    s_remaining.Add(~k);
            }
            while (s_remaining.Count > 0)
            {
                int pick = -1;
                for (int r = 0; r < s_remaining.Count && pick < 0; r++)
                {
                    int c = s_remaining[r];
                    bool ready = true;
                    for (int q = 0; q < s_remaining.Count; q++)
                    {
                        int d = s_remaining[q];
                        if (d != c && StepDependsOn(p, pose, c, d))
                        {
                            ready = false;
                            break;
                        }
                    }
                    if (ready)
                        pick = r;
                }
                if (pick < 0)
                    pick = 0;
                s_chainOrder.Add(s_remaining[pick]);
                s_remaining.RemoveAt(pick);
            }
        }

        public static bool HasCycle(AsiKeyProject p, int chain, int objectBone)
        {
            if (objectBone < 0)
                return false;
            return p.IsUnderChain(objectBone, chain) || AsiKeyConstraints.DependsOnChain(p, objectBone, chain);
        }

        /// <summary>
        /// 从 fk 求出可见姿势：读入单位坐标系 → IK 链与约束按依赖顺序逐步求解（每步之后重算世界姿势）→ IK 之后叠加的层。
        /// </summary>
        public static void Solve(AsiKeyProject p, AsiKeyContext ctx, AsiKeyPose pose)
        {
            int n = pose.count;
            pose.LoadUnits(ctx);
            for (int i = 0; i < n; i++)
            {
                pose.rot[i] = pose.fk[i];
                pose.pos[i] = pose.fkPos[i];
            }
            pose.ComputeWorld(p);

            OrderSteps(p, pose);
            for (int o = 0; o < s_chainOrder.Count; o++)
            {
                int c = s_chainOrder[o];
                if (c < 0)
                {
                    AsiKeyConstraints.Solve(p, ctx, pose, ~c);
                    pose.ComputeWorld(p);
                    continue;
                }
                AsiKeyChain chain = p.chains[c];
                AsiChainState s = pose.chains[c];
                int m = chain.bones.Length;
                if (s_ik.Length < m)
                    s_ik = new Quaternion[Mathf.NextPowerOfTwo(m)];
                Vector3 tpos;
                Quaternion trot;
                ResolveState(p, chain, pose, ctx, s, out tpos, out trot);
                AsiKeyIKSolver.Solve(p, chain, pose, tpos, trot, s.swivel, s_ik);
                for (int k = 0; k < m; k++)
                {
                    int b = chain.bones[k];
                    pose.rot[b] = s.blend >= 0.9999f ? s_ik[k] : Quaternion.Slerp(pose.rot[b], s_ik[k], s.blend);
                }
                pose.ComputeWorld(p);
            }

            bool post = false;
            for (int i = 0; i < n; i++)
            {
                if (pose.postRot[i] != Quaternion.identity || pose.actRot[i] != Quaternion.identity
                    || pose.postPos[i] != Vector3.zero || pose.actPos[i] != Vector3.zero)
                {
                    post = true;
                    pose.rot[i] = pose.rot[i] * pose.postRot[i] * pose.actRot[i];
                    pose.pos[i] = pose.pos[i] + pose.postPos[i] + pose.actPos[i];
                }
            }
            if (post)
                pose.ComputeWorld(p);
        }
    }
}
