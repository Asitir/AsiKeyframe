using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>控制器拖动检测：FK 旋转、质心/根骨位移、IK 末端拖拽（链成员、脚趾、手指的位移转给链末端）、根成员转膝盖方向、缩放调外形大小、姿势粘贴。</summary>
    public partial class AsiKeySession
    {
        private struct DragStart
        {
            public Vector3 local;
            public Vector3 rig;
        }

        private struct MoveStart
        {
            public bool ik;
            public Vector3 pos;
            public Quaternion rot;
        }

        private readonly List<int> _edited = new List<int>();
        private readonly List<int> _editedChains = new List<int>();
        private readonly Dictionary<int, DragStart> _dragStart = new Dictionary<int, DragStart>();
        private readonly Dictionary<int, MoveStart> _moveStart = new Dictionary<int, MoveStart>();
        private readonly List<int> _redirected = new List<int>();
        private readonly List<int> _redirectSrc = new List<int>();
        /// <summary>转脚时保持世界朝向的子级（脚趾）。重新求值时清空；打脚的关键帧时一起打。</summary>
        private readonly HashSet<int> _heldChildren = new HashSet<int>();
        /// <summary>本次编辑里按 FK 转的滑动段链 → 编辑前的链状态（见 <see cref="SlideFK"/>）。</summary>
        private readonly Dictionary<int, AsiChainState> _slideFK = new Dictionary<int, AsiChainState>();
        private bool _dragging;
        private bool _hasLeadDelta;
        private Vector3 _leadDelta;
        private Quaternion[] _ikBuf = new Quaternion[64];

        private static bool SameRot(Quaternion a, Quaternion b)
        {
            if (a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w)
                return true;
            return Mathf.Abs(Quaternion.Dot(a, b)) > 1f - 1e-7f;
        }

        private static bool SamePos(Vector3 a, Vector3 b)
        {
            return (a - b).sqrMagnitude < 1e-12f;
        }

        private void DetectEdits()
        {
            _dragging = GUIUtility.hotControl != 0;
            _edited.Clear();
            int[] order = pose.order;
            for (int o = 0; o < order.Length; o++)
            {
                int i = order[o];
                Transform c = controls[i];
                if (c == null)
                    continue;
                if (!SameRot(c.localRotation, _writtenRot[i]) || !SamePos(c.localPosition, _writtenPos[i]))
                    _edited.Add(i);
            }
            if (_edited.Count == 0)
            {
                if (!_dragging)
                {
                    _dragStart.Clear();
                    _moveStart.Clear();
                }
                return;
            }
            _editedChains.Clear();
            _redirected.Clear();
            _redirectSrc.Clear();
            _hasLeadDelta = LeadDelta(out _leadDelta);
            for (int k = 0; k < _edited.Count; k++)
            {
                int i = _edited[k];
                Transform c = controls[i];
                Quaternion lr = c.localRotation;
                Vector3 lp = c.localPosition;
                ApplyEdit(i, lr, lp, !SameRot(lr, _writtenRot[i]), !SamePos(lp, _writtenPos[i]));
            }
            if (!_dragging)
            {
                _dragStart.Clear();
                _moveStart.Clear();
            }
            EndSlideFK();
            SolveEdited();
            WriteAll();
            _poseDirty = true;
            if (autoKey)
            {
                for (int k = 0; k < _edited.Count; k++)
                {
                    if (!_redirectSrc.Contains(_edited[k]))
                        _pendingKey.Add(_edited[k]);
                }
                for (int k = 0; k < _redirected.Count; k++)
                    _pendingKey.Add(_redirected[k]);
            }
            InvalidateTrajectories();
        }

        private void SolveEdited()
        {
            AsiKeyEvaluator.Solve(project, ctx, pose);
            if (SplitActive)
                return;
            for (int k = 0; k < _editedChains.Count; k++)
            {
                int c = _editedChains[k];
                AsiChainState s = pose.chains[c];
                if (!s.valid || s.blend < 0.5f)
                    continue;
                int[] members = project.chains[c].bones;
                for (int m = 0; m < members.Length; m++)
                {
                    int b = members[m];
                    pose.fk[b] = AsiKeyMath.Norm(pose.rot[b] * Quaternion.Inverse(pose.postRot[b] * pose.actRot[b]));
                }
            }
        }

        private void ParentWorld(int i, out Vector3 pos, out Quaternion rot)
        {
            pose.ParentFrame(project, i, out pos, out rot);
        }

        /// <summary>
        /// 被约束骨骼的编辑：约束影响度为 1 时改写约束偏移（骨骼停在拖到的位置），影响度在 0..1 之间时拒绝。
        /// 处理过的通道从 rotChanged / posChanged 里去掉，剩下的照常改关键帧数据。
        /// </summary>
        private void ConstraintEdit(int i, Quaternion lr, Vector3 lp, ref bool rotChanged, ref bool posChanged)
        {
            Vector3 parentPos;
            Quaternion parentRot;
            ParentWorld(i, out parentPos, out parentRot);
            int k;
            if (rotChanged)
            {
                AsiKeyConstraints.EditRoute route = AsiKeyConstraints.Route(project, pose, i, true, out k);
                if (route == AsiKeyConstraints.EditRoute.Blocked)
                {
                    status = BlockedConstraintStatus(i, k);
                    rotChanged = false;
                }
                else if (route == AsiKeyConstraints.EditRoute.Offset)
                {
                    Undo.RecordObject(project, "改约束偏移");
                    AsiKeyConstraints.RewriteRot(project, ctx, pose, k, AsiKeyMath.Norm(parentRot * lr));
                    project.Touch();
                    EditorUtility.SetDirty(project);
                    rotChanged = false;
                }
            }
            if (posChanged)
            {
                AsiKeyConstraints.EditRoute route = AsiKeyConstraints.Route(project, pose, i, false, out k);
                if (route == AsiKeyConstraints.EditRoute.Blocked)
                {
                    status = BlockedConstraintStatus(i, k);
                    posChanged = false;
                }
                else if (route == AsiKeyConstraints.EditRoute.Offset)
                {
                    Undo.RecordObject(project, "改约束偏移");
                    AsiKeyConstraints.RewritePos(project, ctx, pose, k, DraggedPos(i, lp, parentPos, parentRot, false));
                    project.Touch();
                    EditorUtility.SetDirty(project);
                    posChanged = false;
                }
            }
        }

        private DragStart DragRecord(int i)
        {
            DragStart d;
            if (_dragStart.TryGetValue(i, out d))
                return d;
            d.local = _writtenPos[i];
            d.rig = pose.wpos[i];
            if (_dragging)
                _dragStart[i] = d;
            return d;
        }

        /// <summary>
        /// 移动工具拖动中 Unity 每次按「按下时的局部位置 + 当前父级下的总位移」写回控制器，
        /// 父级被 IK 带动后局部位移会被转错方向，所以用当前父级还原总位移，再加到按下时的角色空间位置上。
        /// 同时改了旋转的是旋转工具绕中心转，那时 Unity 写的是世界位置，直接换算。
        /// </summary>
        private Vector3 DraggedPos(int i, Vector3 lp, Vector3 parentPos, Quaternion parentRot, bool rotChanged)
        {
            if (rotChanged)
                return parentPos + parentRot * lp;
            DragStart d = DragRecord(i);
            return d.rig + (_hasLeadDelta ? _leadDelta : parentRot * (lp - d.local));
        }

        /// <summary>
        /// 多选拖动时 Unity 让其余物体沿用第一个物体的实际世界位移（含其父级被 IK 带动的漂移），
        /// 所以统一用第一个被拖物体还原出的总位移。
        /// </summary>
        private bool LeadDelta(out Vector3 delta)
        {
            delta = Vector3.zero;
            if (!_dragging || _edited.Count < 2)
                return false;
            Transform[] sel = Selection.transforms;
            for (int k = 0; k < sel.Length; k++)
            {
                int i = ControlIndex(sel[k]);
                if (i < 0 || !_edited.Contains(i))
                    continue;
                Vector3 lp = controls[i].localPosition;
                if (SamePos(lp, _writtenPos[i]) || !SameRot(controls[i].localRotation, _writtenRot[i]))
                    return false;
                Vector3 parentPos;
                Quaternion parentRot;
                ParentWorld(i, out parentPos, out parentRot);
                delta = parentRot * (lp - DragRecord(i).local);
                return true;
            }
            return false;
        }

        private void ApplyEdit(int i, Quaternion lr, Vector3 lp, bool rotChanged, bool posChanged)
        {
            AsiKeyBone b = project.bones[i];
            if (b.fingerTip && rotChanged)
            {
                if (AsiKeyFingers.TipChain(project, i) >= 0 && !Listed(SelectedBones(), b.parent))
                {
                    Vector3 parentPos;
                    Quaternion parentRot;
                    ParentWorld(i, out parentPos, out parentRot);
                    Quaternion newW = AsiKeyMath.Norm(parentRot * lr);
                    Quaternion delta = AsiKeyMath.Norm(newW * Quaternion.Inverse(pose.wrot[i]));
                    OrbitFingerChain(i, delta, pose);
                }
                return;
            }
            if (project.constraints.Count > 0)
            {
                ConstraintEdit(i, lr, lp, ref rotChanged, ref posChanged);
                if (!rotChanged && !posChanged)
                    return;
            }
            if (posChanged && !rotChanged)
            {
                int t = MoveTarget(i);
                if (t != i)
                {
                    RedirectMove(i, t, lp);
                    return;
                }
            }
            bool post = SplitActive;
            bool baseLayer = project.activeLayer == 0;
            int c = project.ChainOf(i);
            if (rotChanged && SlideFK(i))
                posChanged = false;
            else if (c >= 0 && !post)
            {
                AsiKeyChain chain = project.chains[c];
                AsiChainState s = pose.chains[c];
                bool ikOn = s.valid && s.blend >= 0.5f;
                int m = chain.MemberIndex(i);
                Vector3 parentPos;
                Quaternion parentRot;
                ParentWorld(i, out parentPos, out parentRot);
                Quaternion wr = AsiKeyMath.Norm(parentRot * lr);
                Vector3 wp = posChanged ? DraggedPos(i, lp, parentPos, parentRot, rotChanged) : pose.wpos[i];
                if (m == chain.bones.Length - 1)
                {
                    if (ikOn)
                    {
                        if (!baseLayer)
                        {
                            status = "叠加层不改 IK 目标；关闭该层的「保持底层 IK」可以在 IK 之后叠加";
                            return;
                        }
                        if (!posChanged)
                        {
                            Vector3 piv = project.PivotOf(chain, PivotChoice(c));
                            if (piv.sqrMagnitude > 1e-12f)
                            {
                                Vector3 anchor = pose.wpos[i] + pose.wrot[i] * piv;
                                wp = anchor - wr * piv;
                            }
                        }
                        SetChainTarget(c, wp, wr);
                        if (rotChanged)
                            HoldChildren(c, wr, pose);
                        return;
                    }
                    if (posChanged)
                    {
                        IKDrag(c, wp, wr);
                        return;
                    }
                }
                else if (ikOn)
                {
                    if (chain.bones.Length >= 3 && baseLayer && rotChanged)
                    {
                        if (m == 0)
                            RotateRootIK(c, AsiKeyMath.Norm(wr * Quaternion.Inverse(pose.wrot[i])), pose);
                        else
                            SwivelFromMid(c, i, lr);
                        return;
                    }
                    status = "IK 生效中，" + b.name + " 由 IK 驱动；在基础层转动链根或小腿/小臂可调整膝/肘朝向";
                    return;
                }
            }
            if (rotChanged)
            {
                int hc;
                if (AsiKeyHinge.MidOf(project, i, out hc) && !AsiKeyFingers.IsFingerChain(project, hc))
                {
                    Vector3 parentPos;
                    Quaternion parentRot;
                    ParentWorld(i, out parentPos, out parentRot);
                    RotateMid(hc, AsiKeyMath.Norm(parentRot * lr * Quaternion.Inverse(pose.wrot[i])), pose, true);
                }
                else
                {
                    SetVisibleRot(i, lr);
                    KeepLinkedEffector(i, pose);
                }
            }
            if (posChanged)
            {
                if (Movable(i))
                {
                    Vector3 parentPos;
                    Quaternion parentRot;
                    ParentWorld(i, out parentPos, out parentRot);
                    SetVisiblePos(i, Quaternion.Inverse(parentRot) * (DraggedPos(i, lp, parentPos, parentRot, rotChanged) - parentPos));
                }
                else
                    status = b.name + " 不记录位移（可在骨骼页勾选「记录位移」）";
            }
        }

        /// <summary>
        /// 拖动链成员、脚趾或手指的位置等价于拖动链末端 t：取 i 的总位移加到 t 按下时的位置（IK 时是 IK 目标）上。
        /// 末端自己也被拖、或同一末端已经移过时跳过。
        /// </summary>
        private void RedirectMove(int i, int t, Vector3 lp)
        {
            _redirectSrc.Add(i);
            if (_edited.Contains(t) || _redirected.Contains(t))
                return;
            _redirected.Add(t);
            int c = project.ChainOf(t);
            if (c < 0)
                return;
            Vector3 parentPos;
            Quaternion parentRot;
            ParentWorld(i, out parentPos, out parentRot);
            Vector3 delta = DraggedPos(i, lp, parentPos, parentRot, false) - DragRecord(i).rig;
            MoveStart st = MoveRecord(t, c);
            if (st.ik)
            {
                if (project.activeLayer != 0)
                {
                    status = "叠加层不改 IK 目标；关闭该层的「保持底层 IK」可以在 IK 之后叠加";
                    return;
                }
                SetChainTarget(c, st.pos + delta, st.rot);
            }
            else
                IKDrag(c, st.pos + delta, st.rot);
        }

        private MoveStart MoveRecord(int t, int c)
        {
            MoveStart m;
            if (_moveStart.TryGetValue(t, out m))
                return m;
            AsiChainState s = pose.chains[c];
            m.ik = s.valid && s.blend >= 0.5f;
            if (m.ik)
                AsiKeyEvaluator.ResolveState(project, project.chains[c], pose, ctx, s, out m.pos, out m.rot);
            else
            {
                m.pos = pose.wpos[t];
                m.rot = pose.wrot[t];
            }
            if (_dragging)
                _moveStart[t] = m;
            return m;
        }

        /// <summary>
        /// 固定/滑动段里转脚：脚的子级（脚趾）保持 from 里的世界朝向，像钉在世界里一样不跟着转，
        /// 并记进 <see cref="_heldChildren"/>，打脚的关键帧时一起打上。
        /// </summary>
        private void HoldChildren(int c, Quaternion effRot, AsiKeyPose from)
        {
            AsiKeyChain chain = project.chains[c];
            if (!project.UsesChildPivot(chain))
                return;
            int e = chain.Effector;
            for (int j = 0; j < project.bones.Count; j++)
            {
                if (project.bones[j].parent != e || project.ChainOf(j) >= 0)
                    continue;
                SetVisibleRot(j, AsiKeyMath.Norm(Quaternion.Inverse(effRot) * from.wrot[j]));
                _heldChildren.Add(j);
            }
        }

        /// <summary>
        /// 滑动段（IK 生效、不连接上一帧）在基础层转链的非末端成员时像 FK 一样转：成员 FK 先换成看到的姿势，链状态暂时关掉，
        /// 之后照 FK 路由编辑，<see cref="EndSlideFK"/> 再把末端落到的位姿写回 IK 目标。链已经这样处理时末端也走 FK。固定段不走这里。
        /// 转小臂时手掌的世界朝向另由 <see cref="KeepLinkedEffector"/> 保持，和脚掌一样不跟着转。
        /// </summary>
        private bool SlideFK(int i)
        {
            if (SplitActive || project.activeLayer != 0)
                return false;
            int c = project.ChainOf(i);
            if (c < 0)
                return false;
            if (_slideFK.ContainsKey(c))
                return true;
            AsiKeyChain chain = project.chains[c];
            if (chain.Effector == i)
                return false;
            AsiChainState s = pose.chains[c];
            if (!s.valid || s.blend < 0.5f || s.join)
                return false;
            for (int m = 0; m < chain.bones.Length; m++)
                pose.fk[chain.bones[m]] = VisibleFK(chain.bones[m]);
            _slideFK[c] = s;
            AsiChainState off = s;
            off.valid = false;
            pose.chains[c] = off;
            return true;
        }

        /// <summary>按 FK 求解，把末端位姿写回 IK 目标；零平面参考换成新的链根 FK，三成员链重新量 swivel，IK 重算出同一姿势。</summary>
        private void EndSlideFK()
        {
            if (_slideFK.Count == 0)
                return;
            AsiKeyEvaluator.Solve(project, ctx, pose);
            foreach (KeyValuePair<int, AsiChainState> kv in _slideFK)
            {
                int c = kv.Key;
                AsiChainState s = kv.Value;
                AsiKeyChain chain = project.chains[c];
                int eff = chain.Effector;
                AsiChainTarget t = AsiKeyEvaluator.ToSpace(project, chain, pose, ctx, s.a.space, s.a.bone, s.a.obj, pose.wpos[eff], pose.wrot[eff]);
                s.a = t;
                s.b = t;
                s.mix = 0f;
                s.refRot = pose.fk[chain.bones[0]];
                s.hasRef = true;
                s.hasDrag = false;
                pose.chains[c] = s;
                if (StableKnee(chain))
                {
                    s.swivel = AsiKeyIKSolver.MeasureSwivel(project, chain, pose, s.swivel);
                    pose.chains[c] = s;
                }
                if (!_editedChains.Contains(c))
                    _editedChains.Add(c);
            }
            _slideFK.Clear();
        }

        /// <summary>SetVisibleRot / SetVisiblePos / SetChainTarget 只改姿势数据，调用后用它求解并写回模型。</summary>
        public void CommitEdits()
        {
            if (!Valid)
                return;
            SolveEdited();
            WriteAll();
            _poseDirty = true;
            InvalidateTrajectories();
        }

        public void SetVisibleRot(int i, Quaternion vis)
        {
            if (SplitActive)
                pose.actRot[i] = AsiKeyMath.Norm(pose.actRot[i] * Quaternion.Inverse(pose.rot[i]) * vis);
            else
                pose.fk[i] = AsiKeyMath.Norm(vis * Quaternion.Inverse(pose.postRot[i] * pose.actRot[i]));
        }

        public void SetVisiblePos(int i, Vector3 vis)
        {
            if (SplitActive)
                pose.actPos[i] += vis - pose.pos[i];
            else
                pose.fkPos[i] = vis - pose.postPos[i] - pose.actPos[i];
        }

        private static bool Listed(IList<int> list, int bone)
        {
            if (list == null)
                return false;
            for (int k = 0; k < list.Count; k++)
            {
                if (list[k] == bone)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 单独选中指尖轴心时，整根手指 IK 链绕该点转：末节绕它公转，链重新求解，指尖位置不动。
        /// 末节也在选择里时不调用这里，末节按普通方式转。
        /// </summary>
        private void OrbitFingerChain(int tip, Quaternion delta, AsiKeyPose from)
        {
            int parent = project.bones[tip].parent;
            int c = AsiKeyFingers.TipChain(project, tip);
            if (c < 0 || parent < 0)
                return;
            MatchChain(c, from);
            AsiChainState s = from.chains[c];
            bool ikOn = s.valid && s.blend >= 0.5f && !SplitActive;
            if (ikOn && project.activeLayer != 0)
            {
                status = "叠加层不改 IK 目标；关闭该层的「保持底层 IK」可以在 IK 之后叠加";
                return;
            }
            Quaternion wr = AsiKeyMath.Norm(delta * from.wrot[parent]);
            Vector3 piv = Quaternion.Inverse(from.wrot[parent]) * (from.wpos[tip] - from.wpos[parent]);
            Vector3 wp = from.wpos[tip] - wr * piv;
            if (ikOn)
                SetChainTarget(c, wp, wr);
            else
                IKDrag(c, wp, wr);
        }

        /// <summary>把这条链的成员姿势恢复成 from，避免同一次拖动里先转过的别的骨骼污染求解起点。</summary>
        private void MatchChain(int c, AsiKeyPose from)
        {
            if (ReferenceEquals(from, pose))
                return;
            int[] members = project.chains[c].bones;
            for (int m = 0; m < members.Length; m++)
            {
                int b = members[m];
                pose.fk[b] = from.fk[b];
                pose.fkPos[b] = from.fkPos[b];
                pose.rot[b] = from.rot[b];
                pose.pos[b] = from.pos[b];
                pose.wrot[b] = from.wrot[b];
                pose.wpos[b] = from.wpos[b];
                pose.postRot[b] = from.postRot[b];
                pose.postPos[b] = from.postPos[b];
                pose.actRot[b] = from.actRot[b];
                pose.actPos[b] = from.actPos[b];
            }
            pose.chains[c] = from.chains[c];
        }

        /// <summary>把 IK 目标改到角色空间的 (wp, wr)，按当前目标所在空间换算后存入链状态。</summary>
        public void SetChainTarget(int c, Vector3 wp, Quaternion wr)
        {
            AsiKeyChain chain = project.chains[c];
            AsiChainState s = pose.chains[c];
            if (!s.valid)
            {
                s = new AsiChainState();
                s.valid = true;
                s.blend = 1f;
                s.a.space = AsiIKSpace.World;
                s.a.bone = -1;
                s.a.obj = "";
                if (chain.bones.Length > 0)
                {
                    s.refRot = pose.fk[chain.bones[0]];
                    s.hasRef = true;
                }
            }
            if (!s.hasDrag)
            {
                pose.chains[c] = s;
                AsiKeyIKSolver.CaptureDrag(project, chain, pose, ref s);
            }
            AsiChainTarget t = AsiKeyEvaluator.ToSpace(project, chain, pose, ctx, s.a.space, s.a.bone, s.a.obj, wp, wr);
            s.a = t;
            s.b = t;
            s.mix = 0f;
            pose.chains[c] = s;
            if (!_editedChains.Contains(c))
                _editedChains.Add(c);
        }

        /// <summary>
        /// FK 状态下拖动末端：用一次 IK 求解得到成员旋转，写回 FK。
        /// 稳定膝/肘链先从当前姿势量出 swivel 再求解，膝/肘保持在原来的弯曲平面里，结果只取决于末端位置。
        /// </summary>
        private void IKDrag(int c, Vector3 wp, Quaternion wr)
        {
            AsiKeyChain chain = project.chains[c];
            int n = chain.bones.Length;
            if (_ikBuf.Length < n)
                _ikBuf = new Quaternion[Mathf.NextPowerOfTwo(n)];
            AsiChainState s = pose.chains[c];
            float swivel = s.valid ? s.swivel : 0f;
            if (n == 3)
                swivel = AsiKeyIKSolver.MeasureSwivel(project, chain, pose, swivel);
            AsiKeyIKSolver.Solve(project, chain, pose, wp, wr, swivel, _ikBuf);
            if (AsiKeyHinge.Active(project, chain) && !AsiKeyFingers.IsFingerChain(project, c))
                _ikBuf[1] = AsiKeyHinge.Project(project, chain, _ikBuf[1]);
            for (int m = 0; m < n; m++)
                SetVisibleRot(chain.bones[m], _ikBuf[m]);
            if (s.valid)
            {
                AsiChainTarget t = AsiKeyEvaluator.ToSpace(project, chain, pose, ctx, s.a.space, s.a.bone, s.a.obj, wp, wr);
                s.a = t;
                s.b = t;
                s.mix = 0f;
                s.swivel = swivel;
                pose.chains[c] = s;
            }
        }

        /// <summary>
        /// IK 生效时转动链根骨骼：delta 是相对 from 姿势的世界旋转增量，中间关节绕「根→目标」轴转过的角度加到 swivel 上。
        /// </summary>
        private void RotateRootIK(int c, Quaternion delta, AsiKeyPose from)
        {
            AsiKeyChain chain = project.chains[c];
            AsiChainState s = pose.chains[c];
            Vector3 tpos;
            Quaternion trot;
            AsiKeyEvaluator.ResolveState(project, chain, from, ctx, from.chains[c], out tpos, out trot);
            Vector3 root = from.wpos[chain.Root];
            Vector3 axis = tpos - root;
            if (axis.sqrMagnitude < 1e-10f)
                return;
            axis.Normalize();
            Vector3 oldMid = from.wpos[chain.bones[1]] - root;
            Vector3 a = Vector3.ProjectOnPlane(oldMid, axis);
            Vector3 b = Vector3.ProjectOnPlane(delta * oldMid, axis);
            if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f)
                return;
            s.swivel = WrapAngle(from.chains[c].swivel + Vector3.SignedAngle(a, b, axis));
            pose.chains[c] = s;
            if (!_editedChains.Contains(c))
                _editedChains.Add(c);
        }

        private static float WrapAngle(float a)
        {
            return Mathf.Repeat(a + 180f, 360f) - 180f;
        }

        /// <summary>
        /// Biped 小腿/小臂（开启铰链锁的三成员链中间关节）的旋转。delta 是相对 from 姿势的世界旋转增量：
        /// 绕骨骼 X 的分量是特殊旋转——整条肢体绕「根→末端」轴转，末端不动；绕铰链轴（Z）的分量是弯曲；其余分量丢弃。
        /// IK 下特殊旋转加到 swivel，弯曲由 IK 决定；FK 下转链根并弯中间关节（夹到弯曲范围），自由关键帧也能横向摆膝/肘。
        /// 滑动段里转小臂后再保持手掌的世界朝向（脚掌是独立朝向，转小腿本来就不改朝向）。
        /// limbAxis 为 true 时特殊旋转量取 delta 绕「根→末端」轴的扭转（Unity 旋转工具，与 IK 下转小腿一致），否则绕骨骼 X（变换轴的 X 环）。
        /// </summary>
        private void RotateMid(int c, Quaternion delta, AsiKeyPose from, bool limbAxis = false)
        {
            AsiKeyChain chain = project.chains[c];
            int root = chain.bones[0];
            int mid = chain.bones[1];
            int eff = chain.bones[2];
            AsiChainState s = pose.chains[c];
            bool ikOn = s.valid && s.blend >= 0.5f && !SplitActive;

            Vector3 end = from.wpos[eff] - from.wpos[root];
            if (ikOn)
            {
                Vector3 tpos;
                Quaternion trot;
                AsiKeyEvaluator.ResolveState(project, chain, from, ctx, from.chains[c], out tpos, out trot);
                end = tpos - from.wpos[root];
            }
            Vector3 x = (from.wrot[mid] * AsiKeyBipedFrame.Axis(project, mid, 0)).normalized;
            float restBend;
            Vector3 hinge = AsiKeyHinge.Axis(project, chain, out restBend);
            Vector3 z = (from.wrot[root] * hinge).normalized;
            float bend = AsiKeyHinge.TwistAngle(delta, z);
            float special = 0f;
            if (end.sqrMagnitude > 1e-10f)
            {
                end.Normalize();
                // 量特殊旋转的轴要垂直于铰链轴，否则纯弯曲会带出特殊旋转、纯特殊旋转会带出弯曲。
                Vector3 m = limbAxis ? end : x;
                m -= Vector3.Dot(m, z) * z;
                if (m.sqrMagnitude > 1e-8f)
                {
                    m.Normalize();
                    special = AsiKeyHinge.TwistAngle(delta, m);
                    if (Vector3.Dot(m, end) < 0f)
                        special = -special;
                }
            }

            if (ikOn)
            {
                if (project.activeLayer != 0)
                {
                    status = "叠加层不改 IK 的膝/肘朝向；关闭该层的「保持底层 IK」可以在 IK 之后叠加";
                    return;
                }
                if (Mathf.Abs(bend) > 0.05f && Mathf.Abs(special) < 0.05f)
                    status = "IK 生效中，" + project.bones[mid].name + " 的弯曲由末端位置决定；绕 X 转可以改膝/肘朝向";
                if (Mathf.Abs(special) < 1e-4f)
                    return;
                s.swivel = WrapAngle(from.chains[c].swivel + special);
                pose.chains[c] = s;
                if (!_editedChains.Contains(c))
                    _editedChains.Add(c);
                return;
            }

            if (Mathf.Abs(special) > 1e-4f)
            {
                Quaternion parentRot = pose.ParentRot(project, root);
                Quaternion rootW = Quaternion.AngleAxis(special, end) * from.wrot[root];
                SetVisibleRot(root, AsiKeyMath.Norm(Quaternion.Inverse(parentRot) * rootW));
            }
            bool clamped;
            Quaternion local = AsiKeyHinge.Project(project, chain, AsiKeyMath.Norm(Quaternion.AngleAxis(bend, hinge) * from.rot[mid]), out clamped);
            if (clamped)
                status = project.bones[mid].name + " 已到弯曲极限（可在 IK 链页调整弯曲范围）";
            SetVisibleRot(mid, local);
            KeepLinkedEffector(mid, from);
        }

        /// <summary>
        /// 滑动段按 FK 转过「末端的旋转父级」之后，把末端世界朝向设回 from。
        /// 手掌的旋转父级是小臂，不处理的话转小臂会带动手掌；脚掌的旋转父级是质心，不在链里，这里直接跳过。
        /// 只在被转的就是末端父级时生效，所以转大臂时手仍跟着整条手臂走。手指链不处理。
        /// </summary>
        private void KeepLinkedEffector(int edited, AsiKeyPose from)
        {
            int c = project.ChainOf(edited);
            if (c < 0 || !_slideFK.ContainsKey(c) || AsiKeyFingers.IsFingerChain(project, c))
                return;
            int eff = project.chains[c].Effector;
            if (eff < 0 || eff >= project.bones.Count || project.bones[eff].parent != edited)
                return;
            Quaternion parentW = VisibleWorld(edited);
            SetVisibleRot(eff, AsiKeyMath.Norm(Quaternion.Inverse(parentW) * from.wrot[eff]));
        }

        /// <summary>按当前 fk（含叠加层）从单位根累乘的世界旋转。刚改过 fk、世界姿势还没重算时用。</summary>
        private Quaternion VisibleWorld(int i)
        {
            Quaternion local = AsiKeyMath.Norm(pose.fk[i] * pose.postRot[i] * pose.actRot[i]);
            int parent = project.bones[i].parent;
            if (parent < 0)
                return AsiKeyMath.Norm(pose.UnitRot(project.bones[i].unit) * local);
            return AsiKeyMath.Norm(VisibleWorld(parent) * local);
        }

        /// <summary>
        /// IK 生效时转动中间关节（小腿/小臂）：取世界旋转增量绕「根→目标」轴的扭转分量累加到 swivel，
        /// 整条肢体绕这根轴摆动，末端不动；其余分量由 IK 决定，丢弃。
        /// </summary>
        private void SwivelFromMid(int c, int i, Quaternion lr)
        {
            AsiKeyChain chain = project.chains[c];
            AsiChainState s = pose.chains[c];
            Vector3 tpos;
            Quaternion trot;
            AsiKeyEvaluator.ResolveState(project, chain, pose, ctx, s, out tpos, out trot);
            Vector3 axis = tpos - pose.wpos[chain.Root];
            if (axis.sqrMagnitude < 1e-10f)
                return;
            axis.Normalize();
            Vector3 parentPos;
            Quaternion parentRot;
            ParentWorld(i, out parentPos, out parentRot);
            Quaternion dq = AsiKeyMath.Norm(parentRot * lr) * Quaternion.Inverse(pose.wrot[i]);
            float twist = AsiKeyHinge.TwistAngle(dq, axis);
            if (Mathf.Abs(twist) < 1e-4f)
                return;
            s.swivel = WrapAngle(s.swivel + twist);
            pose.chains[c] = s;
            if (!_editedChains.Contains(c))
                _editedChains.Add(c);
        }

        public void SetSwivel(int c, float swivel)
        {
            if (c < 0 || c >= pose.chainCount)
                return;
            AsiChainState s = pose.chains[c];
            if (!s.valid)
                return;
            _editedChains.Clear();
            s.swivel = swivel;
            pose.chains[c] = s;
            _editedChains.Add(c);
            SolveEdited();
            WriteAll();
            _poseDirty = true;
            if (autoKey)
                _pendingKey.Add(project.chains[c].Effector);
        }

        // ---------------- 缩放调外形 ----------------

        private void DetectScale()
        {
            if (GUIUtility.hotControl != 0)
                return;
            for (int i = 0; i < controls.Length; i++)
            {
                Transform c = controls[i];
                if (c == null)
                    continue;
                Vector3 s = c.localScale;
                if (s == Vector3.one)
                    continue;
                Undo.RecordObject(project, "调整外形大小");
                AsiKeyBone b = project.bones[i];
                AsiKeyShape sh = b.shape;
                Quaternion r = b.frame * Quaternion.Euler(sh.euler);
                Vector3 abs = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                float fx = AxisFactor(r * Vector3.right, abs);
                float fy = AxisFactor(r * Vector3.up, abs);
                float fz = AxisFactor(r * Vector3.forward, abs);
                sh.width = Mathf.Max(1e-4f, sh.width * fx);
                sh.radiusX = Mathf.Max(1e-4f, sh.radiusX * fx);
                sh.height = Mathf.Max(1e-4f, sh.height * fy);
                sh.radiusY = Mathf.Max(1e-4f, sh.radiusY * fy);
                sh.length = sh.length * fz;
                sh.offset = new Vector3(sh.offset.x * fx, sh.offset.y * fy, sh.offset.z * fz);
                sh.meshScale = Vector3.Scale(sh.meshScale, new Vector3(fx, fy, fz));
                c.localScale = Vector3.one;
                project.Touch();
                EditorUtility.SetDirty(project);
                SceneView.RepaintAll();
                RaiseRepaint();
            }
        }

        private static float AxisFactor(Vector3 dir, Vector3 scale)
        {
            Vector3 d = dir.normalized;
            return d.x * d.x * scale.x + d.y * d.y * scale.y + d.z * d.z * scale.z;
        }

        // ---------------- 姿势复制粘贴 ----------------

        public void CopyPose()
        {
            if (!Valid)
                return;
            s_clipboard = AsiKeyMirror.Capture(project, pose);
            status = "已复制当前姿势";
        }

        public void PastePose(bool mirror, AsiKeyPoseClip clip = null)
        {
            if (!Valid)
                return;
            if (clip == null)
                clip = s_clipboard;
            if (clip == null || clip.wrot == null || clip.wrot.Length != project.bones.Count)
            {
                status = "剪贴板为空，或姿势来自骨骼结构不同的工程";
                return;
            }
            int n = project.bones.Count;
            List<int> sel = SelectedBones();
            bool[] mask = null;
            if (sel.Count > 0)
            {
                mask = new bool[n];
                for (int k = 0; k < sel.Count; k++)
                    mask[sel[k]] = true;
            }
            Quaternion[] outRot = new Quaternion[n];
            Vector3[] outPos = new Vector3[n];
            bool[] changed = new bool[n];
            Quaternion[] newW = new Quaternion[n];
            Vector3[] newP = new Vector3[n];
            AsiKeyMirror.BuildPaste(project, pose, clip, mirror, mask, outRot, outPos, changed, newW, newP, true);
            List<int> keyed = ApplyPose(changed, outRot, outPos);
            if (string.IsNullOrEmpty(status))
                status = mirror ? "已镜像粘贴姿势" : "已粘贴姿势";
            if (autoKey && keyed.Count > 0)
                KeyBones(keyed, AsiKeyType.Set, true);
        }

        /// <summary>把当前姿势左右镜像（不经过剪贴板）。</summary>
        public void MirrorCurrentPose()
        {
            if (!Valid)
                return;
            PastePose(true, AsiKeyMirror.Capture(project, pose));
        }
    }
}
