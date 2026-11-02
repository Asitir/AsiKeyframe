using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// K帧变换轴（<see cref="AsiKeyGizmo"/>）的编辑入口。按下时记下姿势，之后每个拖动事件都先恢复按下时的姿势，
    /// 再整体加上从按下到现在的总增量，结果只取决于鼠标位置，没有逐帧反馈累积（不抖、来回拖回到原处）。
    /// 路由按 Biped：小腿/小臂的 X 是特殊旋转、Z 是铰链；IK 生效时转链根或小腿改 swivel，转末端改 IK 目标（绕 IK 轴心，脚趾保持世界朝向）；
    /// 移动只作用于质心、记录位移的骨骼和链末端（FK 时按一次 IK 求解摆肢体），链成员、脚趾、手指的移动换成移动链末端。
    /// </summary>
    public partial class AsiKeySession
    {
        private readonly AsiKeyPose _gzStart = new AsiKeyPose();
        private readonly List<int> _gzBones = new List<int>();
        private readonly List<int> _gzMoved = new List<int>();
        private readonly List<int> _gzHeld = new List<int>();
        private bool _gzActive;
        private bool _gzChanged;
        private bool _gzDirty;
        private float _gzFrame;
        private int _gzStamp;
        private readonly List<AsiKeyConstraints.OffsetSnapshot> _gzOffsets = new List<AsiKeyConstraints.OffsetSnapshot>();

        public bool GizmoActive
        {
            get { return _gzActive; }
        }

        /// <summary>开始一次变换轴拖动。bones 为要变换的控制器，按求值顺序（父级在前）处理。</summary>
        public bool GizmoBegin(List<int> bones)
        {
            _gzActive = false;
            if (!Valid || playing || bones == null || bones.Count == 0)
                return false;
            if (project.structureStamp != _structStamp)
                return false;
            _gzStart.CopyFrom(pose);
            _gzBones.Clear();
            int[] order = pose.order;
            for (int o = 0; o < order.Length; o++)
            {
                if (bones.Contains(order[o]))
                    _gzBones.Add(order[o]);
            }
            if (_gzBones.Count == 0)
                return false;
            _gzMoved.Clear();
            _gzHeld.Clear();
            _gzHeld.AddRange(_heldChildren);
            _gzOffsets.Clear();
            for (int k = 0; k < project.constraints.Count; k++)
            {
                if (_gzBones.Contains(project.constraints[k].bone))
                    _gzOffsets.Add(AsiKeyConstraints.Snapshot(project, k));
            }
            _gzFrame = frame;
            _gzStamp = project.structureStamp;
            _gzDirty = _poseDirty;
            _gzChanged = false;
            _gzActive = true;
            status = "";
            return true;
        }

        /// <summary>
        /// 在各骨骼自己的 Biped 局部坐标里绕第 axis 根轴（0 = X，1 = Y，2 = Z）转 angle 度。
        /// 多选时每根骨骼都绕自己的轴转，子级会叠上父级的转动（例如整根手指一起弯）。
        /// </summary>
        public void GizmoRotateLocal(int axis, float angle)
        {
            if (!GizmoRestore())
                return;
            for (int k = 0; k < _gzBones.Count; k++)
            {
                int i = _gzBones[k];
                Vector3 local = AsiKeyBipedFrame.Axis(project, i, axis);
                Quaternion delta = Quaternion.AngleAxis(angle, (_gzStart.wrot[i] * local).normalized);
                RotateBone(i, delta, Quaternion.AngleAxis(angle, local), false, Vector3.zero);
                SolveBetween(k);
            }
            GizmoCommit();
        }

        /// <summary>
        /// 角色空间的旋转增量（世界参考坐标、自由旋转、屏幕环）。
        /// aroundCenter 为 true 时可位移的骨骼与 IK 末端同时绕 center（角色空间）转。
        /// </summary>
        public void GizmoRotate(Quaternion delta, bool aroundCenter, Vector3 center)
        {
            if (!GizmoRestore())
                return;
            delta = AsiKeyMath.Norm(delta);
            for (int k = 0; k < _gzBones.Count; k++)
            {
                RotateBone(_gzBones[k], delta, Quaternion.identity, aroundCenter, center);
                SolveBetween(k);
            }
            GizmoCommit();
        }

        /// <summary>
        /// 层级选择的旋转（任何参考坐标）：把 active 的角色空间增量换成它 Biped 局部坐标里的轴角，
        /// 每根骨骼都绕自己局部坐标里的同一根轴转同样角度，子级叠上父级的转动（整条层级一起卷曲）。
        /// </summary>
        public void GizmoRotateRelative(int active, Quaternion delta)
        {
            if (!GizmoRestore())
                return;
            Quaternion fa = _gzStart.wrot[active] * AsiKeyBipedFrame.Local(project, active);
            Quaternion local = AsiKeyMath.Norm(Quaternion.Inverse(fa) * AsiKeyMath.Norm(delta) * fa);
            for (int k = 0; k < _gzBones.Count; k++)
            {
                int i = _gzBones[k];
                Quaternion f = AsiKeyBipedFrame.Local(project, i);
                Quaternion ld = AsiKeyMath.Norm(f * local * Quaternion.Inverse(f));
                Quaternion d = AsiKeyMath.Norm(_gzStart.wrot[i] * ld * Quaternion.Inverse(_gzStart.wrot[i]));
                RotateBone(i, d, ld, false, Vector3.zero);
                SolveBetween(k);
            }
            GizmoCommit();
        }

        /// <summary>角色空间的位移总量。链成员、脚趾、手指按 <see cref="MoveTarget"/> 换成移动链末端，同一末端只移一次。</summary>
        public void GizmoMove(Vector3 delta)
        {
            if (!GizmoRestore())
                return;
            _gzMoved.Clear();
            for (int k = 0; k < _gzBones.Count; k++)
            {
                int t = MoveTarget(_gzBones[k]);
                if (_gzMoved.Contains(t))
                    continue;
                _gzMoved.Add(t);
                MoveBone(t, delta);
                SolveBetween(k);
            }
            GizmoCommit();
        }

        /// <summary>松开鼠标：保留结果，开着自动关键点时随后打注册关键帧。</summary>
        public void GizmoEnd()
        {
            if (!_gzActive)
                return;
            _gzActive = false;
            if (_gzChanged && autoKey)
            {
                List<int> keyed = _gzMoved.Count > 0 ? _gzMoved : _gzBones;
                for (int k = 0; k < keyed.Count; k++)
                    _pendingKey.Add(keyed[k]);
            }
        }

        /// <summary>Esc / 右键：回到按下时的姿势。</summary>
        public void GizmoCancel()
        {
            if (!_gzActive)
                return;
            _gzActive = false;
            if (!Valid || _gzStamp != project.structureStamp)
                return;
            RestoreOffsets();
            pose.CopyFrom(_gzStart);
            RestoreHeld();
            _editedChains.Clear();
            WriteAll();
            _poseDirty = _gzDirty;
            InvalidateTrajectories();
        }

        /// <summary>变换轴中心（角色空间），恒在骨骼上；IK 末端绕 IK 轴心转，但变换轴不跟到轴心上。</summary>
        public Vector3 GizmoPivot(int bone)
        {
            return pose.wpos[bone];
        }

        /// <summary>移动工具能否作用于这根骨骼：质心、记录位移的骨骼、IK 链末端，以及等价于移动链末端的骨骼（见 <see cref="MoveTarget"/>）。</summary>
        public bool GizmoCanMove(int bone)
        {
            if (bone < 0 || bone >= project.bones.Count)
                return false;
            int t = MoveTarget(bone);
            if (project.Movable(t))
                return true;
            int c = project.ChainOf(t);
            return c >= 0 && !SplitActive && project.chains[c].Effector == t;
        }

        /// <summary>
        /// 移动实际作用的骨骼：IK 链的其它成员（大腿、小腿、大臂、小臂）和挂在链末端下面的骨骼（脚趾、手指）都等价于移动链末端；
        /// 其余骨骼是自己。分层编辑时 IK 不参与，也不转。
        /// </summary>
        public int MoveTarget(int bone)
        {
            if (bone < 0 || bone >= project.bones.Count || SplitActive || project.Movable(bone))
                return bone;
            int c = project.ChainOf(bone);
            if (c >= 0)
                return project.chains[c].Effector;
            int guard = project.bones.Count;
            for (int a = project.LinkParent(bone); a >= 0 && guard-- > 0; a = project.LinkParent(a))
            {
                int ac = project.ChainOf(a);
                if (ac >= 0)
                    return project.chains[ac].Effector == a ? a : bone;
                if (project.Movable(a))
                    return bone;
            }
            return bone;
        }

        private bool GizmoRestore()
        {
            if (!_gzActive)
                return false;
            if (!Valid || _gzStamp != project.structureStamp || !Mathf.Approximately(frame, _gzFrame))
            {
                _gzActive = false;
                return false;
            }
            RestoreOffsets();
            pose.CopyFrom(_gzStart);
            RestoreHeld();
            _editedChains.Clear();
            _slideFK.Clear();
            return true;
        }

        private void RestoreOffsets()
        {
            for (int k = 0; k < _gzOffsets.Count; k++)
                AsiKeyConstraints.Restore(project, _gzOffsets[k]);
        }

        private void RestoreHeld()
        {
            _heldChildren.Clear();
            for (int k = 0; k < _gzHeld.Count; k++)
                _heldChildren.Add(_gzHeld[k]);
        }

        /// <summary>
        /// 被约束骨骼：影响度为 1 时改写约束偏移并返回 true，影响度在 0..1 之间时提示并返回 true，不受约束时返回 false。
        /// </summary>
        private bool GizmoConstraint(int i, bool rot, Quaternion wantRot, Vector3 wantPos)
        {
            if (project.constraints.Count == 0)
                return false;
            int k;
            AsiKeyConstraints.EditRoute route = AsiKeyConstraints.Route(project, _gzStart, i, rot, out k);
            if (route == AsiKeyConstraints.EditRoute.Free)
                return false;
            if (route == AsiKeyConstraints.EditRoute.Blocked)
            {
                status = BlockedConstraintStatus(i, k);
                return true;
            }
            Undo.RecordObject(project, "改约束偏移");
            if (rot)
                AsiKeyConstraints.RewriteRot(project, ctx, pose, k, wantRot);
            else
                AsiKeyConstraints.RewritePos(project, ctx, pose, k, wantPos);
            project.Touch();
            EditorUtility.SetDirty(project);
            return true;
        }

        private void GizmoCommit()
        {
            EndSlideFK();
            SolveEdited();
            WriteAll();
            _poseDirty = true;
            _gzChanged = true;
            InvalidateTrajectories();
        }

        /// <summary>后面的骨骼要按前面骨骼改过之后的父级姿势换算。</summary>
        private void SolveBetween(int k)
        {
            if (k < _gzBones.Count - 1)
                AsiKeyEvaluator.Solve(project, ctx, pose);
        }

        private void ParentNow(int i, out Vector3 pos, out Quaternion rot)
        {
            pose.ParentFrame(project, i, out pos, out rot);
        }

        /// <param name="delta">相对按下时世界旋转的增量（角色空间）。</param>
        /// <param name="localDelta">局部参考坐标下的增量（控制器局部空间），不是局部模式时为 identity。</param>
        private void RotateBone(int i, Quaternion delta, Quaternion localDelta, bool aroundCenter, Vector3 center)
        {
            if (project.bones[i].fingerTip)
            {
                if (AsiKeyFingers.TipChain(project, i) >= 0 && !Listed(_gzBones, project.bones[i].parent))
                    OrbitFingerChain(i, delta, _gzStart);
                return;
            }
            AsiKeyBone b = project.bones[i];
            Quaternion wantRot = localDelta != Quaternion.identity
                ? AsiKeyMath.Norm(_gzStart.wrot[i] * localDelta)
                : AsiKeyMath.Norm(delta * _gzStart.wrot[i]);
            if (GizmoConstraint(i, true, wantRot, Vector3.zero))
            {
                if (aroundCenter)
                    GizmoConstraint(i, false, Quaternion.identity, center + delta * (_gzStart.wpos[i] - center));
                return;
            }
            bool slide = SlideFK(i);
            int hc;
            int c = project.ChainOf(i);
            // 手指不是膝/肘。多选时若走铰链，中节会改写链根，把已经转过的近节盖掉。
            bool finger = AsiKeyFingers.IsFingerChain(project, c);
            if (!finger && AsiKeyHinge.MidOf(project, i, out hc))
            {
                RotateMid(hc, delta, _gzStart);
                return;
            }
            if (c >= 0 && !SplitActive && !slide && !(finger && _gzBones.Count > 1))
            {
                AsiKeyChain chain = project.chains[c];
                AsiChainState s = _gzStart.chains[c];
                bool ikOn = s.valid && s.blend >= 0.5f;
                bool baseLayer = project.activeLayer == 0;
                int m = chain.MemberIndex(i);
                if (ikOn)
                {
                    if (!baseLayer)
                    {
                        status = "叠加层不改 IK；关闭该层的「保持底层 IK」可以在 IK 之后叠加";
                        return;
                    }
                    if (m == chain.bones.Length - 1)
                    {
                        Quaternion wr = AsiKeyMath.Norm(delta * _gzStart.wrot[i]);
                        Vector3 piv = project.PivotOf(chain, PivotChoice(c));
                        Vector3 anchor = _gzStart.wpos[i] + _gzStart.wrot[i] * piv;
                        if (aroundCenter)
                            anchor = center + delta * (anchor - center);
                        SetChainTarget(c, anchor - wr * piv, wr);
                        HoldChildren(c, wr, _gzStart);
                        return;
                    }
                    if (m == 0 && chain.bones.Length >= 3)
                    {
                        RotateRootIK(c, delta, _gzStart);
                        return;
                    }
                    if (chain.bones.Length >= 3)
                    {
                        RotateSwivel(c, delta);
                        return;
                    }
                    status = "IK 生效中，" + b.name + " 由 IK 驱动";
                    return;
                }
            }

            Vector3 parentPos;
            Quaternion parentRot;
            ParentNow(i, out parentPos, out parentRot);
            if (localDelta != Quaternion.identity)
                SetVisibleRot(i, AsiKeyMath.Norm(_gzStart.rot[i] * localDelta));
            else
                SetVisibleRot(i, AsiKeyMath.Norm(Quaternion.Inverse(parentRot) * delta * _gzStart.wrot[i]));
            KeepLinkedEffector(i, _gzStart);
            if (aroundCenter && project.Movable(i))
            {
                Vector3 wp = center + delta * (_gzStart.wpos[i] - center);
                SetVisiblePos(i, Quaternion.Inverse(parentRot) * (wp - parentPos));
            }
        }

        /// <summary>多于三节的链：中间成员的旋转增量绕「根→目标」轴的扭转分量加到 swivel。</summary>
        private void RotateSwivel(int c, Quaternion delta)
        {
            AsiKeyChain chain = project.chains[c];
            AsiChainState s = pose.chains[c];
            Vector3 tpos;
            Quaternion trot;
            AsiKeyEvaluator.ResolveState(project, chain, _gzStart, ctx, _gzStart.chains[c], out tpos, out trot);
            Vector3 axis = tpos - _gzStart.wpos[chain.Root];
            if (axis.sqrMagnitude < 1e-10f)
                return;
            s.swivel = WrapAngle(_gzStart.chains[c].swivel + AsiKeyHinge.TwistAngle(delta, axis.normalized));
            pose.chains[c] = s;
            if (!_editedChains.Contains(c))
                _editedChains.Add(c);
        }

        private void MoveBone(int i, Vector3 delta)
        {
            AsiKeyBone b = project.bones[i];
            if (GizmoConstraint(i, false, Quaternion.identity, _gzStart.wpos[i] + delta))
                return;
            int c = project.ChainOf(i);
            if (c >= 0 && !SplitActive)
            {
                AsiKeyChain chain = project.chains[c];
                if (chain.Effector != i)
                {
                    status = b.name + " 是 IK 链成员，拖动链末端来摆肢体";
                    return;
                }
                AsiChainState s = _gzStart.chains[c];
                if (s.valid && s.blend >= 0.5f)
                {
                    if (project.activeLayer != 0)
                    {
                        status = "叠加层不改 IK 目标；关闭该层的「保持底层 IK」可以在 IK 之后叠加";
                        return;
                    }
                    Vector3 tpos;
                    Quaternion trot;
                    AsiKeyEvaluator.ResolveState(project, chain, _gzStart, ctx, s, out tpos, out trot);
                    SetChainTarget(c, tpos + delta, trot);
                    return;
                }
                IKDrag(c, _gzStart.wpos[i] + delta, _gzStart.wrot[i]);
                return;
            }
            if (!project.Movable(i))
            {
                status = b.name + " 不记录位移（可在骨骼页勾选「记录位移」）";
                return;
            }
            Vector3 parentPos;
            Quaternion parentRot;
            ParentNow(i, out parentPos, out parentRot);
            SetVisiblePos(i, Quaternion.Inverse(parentRot) * (_gzStart.wpos[i] + delta - parentPos));
        }
    }
}
