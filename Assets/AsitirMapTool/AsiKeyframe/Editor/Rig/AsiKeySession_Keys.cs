using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 打关键帧。IK 链按 Biped 语义：
    /// 固定 = IKBlend 1，绑定跟随上一帧（没有上一帧则世界空间，按滑动工作）；
    /// 滑动 = IKBlend 1，不连接，绑定（世界或对象）记在这个关键帧上；自由 = IKBlend 0，身体空间；
    /// 注册 = 沿用当前 IK 状态（已有关键帧保持原类型，新关键帧继承前一个关键帧）。
    /// </summary>
    public partial class AsiKeySession
    {
        private struct Touch
        {
            public AsiKeyTrack track;
            public int layer;
            public AsiKey key;
        }

        private readonly List<Touch> _touched = new List<Touch>();
        private readonly List<int> _keyedChains = new List<int>();
        private readonly AsiKeyPose _basePose = new AsiKeyPose();
        private readonly AsiKeyPose _tmpPose = new AsiKeyPose();
        private bool _baseValid;

        public void SetKey(AsiKeyType type)
        {
            KeyBones(SelectedBones(), type, true);
        }

        private void KeyPending()
        {
            List<int> list = new List<int>(_pendingKey);
            _pendingKey.Clear();
            KeyBones(list, AsiKeyType.Set, true);
        }

        public void KeyBones(List<int> bones, AsiKeyType type, bool record)
        {
            if (!Valid)
                return;
            if (bones == null || bones.Count == 0)
            {
                status = "先在场景里选择控制器";
                return;
            }
            if (record)
                Undo.RecordObject(project, "设置关键帧（" + AsiKeyLabels.KeyType[(int)type] + "）");
            _touched.Clear();
            _keyedChains.Clear();
            _baseValid = false;
            for (int k = 0; k < bones.Count; k++)
            {
                if (bones[k] >= 0 && bones[k] < project.bones.Count)
                    KeyBone(bones[k], type);
            }
            KeyHeldChildren(bones);
            CaptureLinkKeys(bones);
            for (int k = 0; k < _touched.Count; k++)
                AsiKeyEdit.AfterEdit(project, _touched[k].track, _touched[k].layer, _touched[k].key);
            _touched.Clear();
            project.Touch();
            EditorUtility.SetDirty(project);
            _pendingKey.Clear();
            Refresh();
        }

        /// <summary>
        /// 链接约束已经生效时，打关键帧再记一条当前帧的相对位姿，父级不变。
        /// 位置、旋转、注视约束不记。
        /// </summary>
        private void CaptureLinkKeys(List<int> bones)
        {
            if (project.activeLayer != 0)
                return;
            for (int i = 0; i < bones.Count; i++)
            {
                int b = bones[i];
                if (b < 0 || b >= project.bones.Count)
                    continue;
                for (int k = 0; k < project.constraints.Count; k++)
                {
                    AsiKeyConstraint c = project.constraints[k];
                    if (c.kind != AsiConstraintKind.Link || c.bone != b || !c.enabled)
                        continue;
                    if (AsiKeyConstraints.Influence(project, pose, k) < 1f - 1e-3f)
                        continue;
                    if (AsiKeyConstraints.ActiveLink(c, frame) < 0)
                        continue;
                    AsiLinkEntry le = AsiKeyConstraints.EnsureEntryAtFrame(c, frame);
                    if (le == null)
                        continue;
                    Vector3 tp;
                    Quaternion tr;
                    AsiKeyConstraints.TargetFrame(project, ctx, pose, le.bone, le.obj, out tp, out tr);
                    Quaternion inv = Quaternion.Inverse(tr);
                    le.offPos = inv * (pose.wpos[b] - tp);
                    le.offRot = AsiKeyMath.Norm(inv * pose.wrot[b]);
                }
            }
        }

        /// <summary>转脚时保持世界朝向的脚趾随脚的 IK 链一起打关键帧，否则重新求值后会跟着脚转回去。</summary>
        private void KeyHeldChildren(List<int> bones)
        {
            if (_heldChildren.Count == 0 || project.activeLayer != 0)
                return;
            List<int> held = new List<int>(_heldChildren);
            for (int k = 0; k < held.Count; k++)
            {
                int j = held[k];
                if (j < 0 || j >= project.bones.Count || bones.Contains(j))
                    continue;
                int c = project.ChainOf(project.bones[j].parent);
                if (c >= 0 && _keyedChains.Contains(c))
                    KeyBone(j, AsiKeyType.Set);
            }
        }

        private void AddTouch(AsiKeyTrack tr, int layer, AsiKey key)
        {
            for (int k = 0; k < _touched.Count; k++)
            {
                if (_touched[k].track == tr)
                    return;
            }
            Touch t;
            t.track = tr;
            t.layer = layer;
            t.key = key;
            _touched.Add(t);
        }

        private void EnsureBaseline()
        {
            if (_baseValid)
                return;
            AsiKeyEvaluator.EvaluateFK(project, frame, _basePose, project.activeLayer - 1, -1);
            _baseValid = true;
        }

        private float LayerWeight
        {
            get { return Mathf.Max(1e-3f, project.ActiveLayer.weight); }
        }

        private Quaternion StoreRot(int i)
        {
            if (project.activeLayer == 0)
                return pose.fk[i];
            if (!project.ActiveLayer.overwrite)
                return DeltaRot(i);
            float w = Mathf.Max(1e-3f, project.ActiveLayer.weight);
            Quaternion visible;
            Quaternion below;
            if (SplitActive)
            {
                visible = pose.rot[i];
                below = AsiKeyMath.Norm(pose.rot[i] * Quaternion.Inverse(pose.actRot[i]));
            }
            else
            {
                EnsureBaseline();
                visible = pose.fk[i];
                below = _basePose.fk[i];
            }
            return AsiKeyMath.Norm(below * AsiKeyMath.Scale(AsiKeyMath.Norm(Quaternion.Inverse(below) * visible), 1f / w));
        }

        private Vector3 StorePos(int i)
        {
            if (project.activeLayer == 0)
                return pose.fkPos[i];
            if (!project.ActiveLayer.overwrite)
                return DeltaPos(i);
            float w = Mathf.Max(1e-3f, project.ActiveLayer.weight);
            Vector3 visible;
            Vector3 below;
            if (SplitActive)
            {
                visible = pose.pos[i];
                below = pose.pos[i] - pose.actPos[i];
            }
            else
            {
                EnsureBaseline();
                visible = pose.fkPos[i];
                below = _basePose.fkPos[i];
            }
            return below + (visible - below) / w;
        }

        private Quaternion DeltaRot(int i)
        {
            float inv = 1f / LayerWeight;
            if (SplitActive)
                return AsiKeyMath.Scale(pose.actRot[i], inv);
            EnsureBaseline();
            return AsiKeyMath.Scale(AsiKeyMath.Norm(Quaternion.Inverse(_basePose.fk[i]) * pose.fk[i]), inv);
        }

        private Vector3 DeltaPos(int i)
        {
            float inv = 1f / LayerWeight;
            if (SplitActive)
                return pose.actPos[i] * inv;
            EnsureBaseline();
            return (pose.fkPos[i] - _basePose.fkPos[i]) * inv;
        }

        /// <summary>在当前帧给这一层的权重打一个关键帧。</summary>
        public void KeyLayerWeight(int layer, float weight)
        {
            if (!Valid || layer <= 0 || layer >= project.layers.Count)
                return;
            weight = Mathf.Clamp01(weight);
            Undo.RecordObject(project, "层权重关键帧");
            AsiKeyLayer ly = project.layers[layer];
            ly.weight = weight;
            AsiKeyTrack tr = project.GetTrack(layer, AsiTrackKind.LayerWeight, layer);
            bool created;
            AsiKey key = AsiKeyEdit.GetOrInsertKey(project, tr, frame, out created);
            key.pos = new Vector3(weight, 0f, 0f);
            AsiKeyEdit.AfterEdit(project, tr, layer, key);
            project.Touch();
            EditorUtility.SetDirty(project);
            Refresh();
        }

        /// <summary>把选中骨骼在基础层的局部旋转（可移动的还有位置）写进当前叠加层。</summary>
        public void CopyBasePose()
        {
            if (!Valid)
                return;
            if (project.activeLayer <= 0)
            {
                status = "先切到叠加层";
                return;
            }
            List<int> sel = SelectedBones();
            if (sel.Count == 0)
            {
                status = "先选择要拷贝的骨骼";
                return;
            }
            Undo.RecordObject(project, "从基础层拷贝 Pose");
            AsiKeyPose basePose = new AsiKeyPose();
            AsiKeyEvaluator.EvaluateFK(project, frame, basePose, 0, -1);
            AsiKeyPose below = new AsiKeyPose();
            bool ow = project.ActiveLayer.overwrite;
            if (!ow)
                AsiKeyEvaluator.EvaluateFK(project, frame, below, project.activeLayer - 1, -1);
            float w = Mathf.Max(1e-3f, project.ActiveLayer.weight);
            int n = 0;
            for (int i = 0; i < sel.Count; i++)
            {
                int b = sel[i];
                if (b < 0 || b >= project.bones.Count || project.bones[b].fingerTip)
                    continue;
                Quaternion abs = basePose.fk[b];
                Quaternion stored = ow ? abs : AsiKeyMath.Norm(below.fk[b] * AsiKeyMath.Scale(AsiKeyMath.Norm(Quaternion.Inverse(below.fk[b]) * abs), 1f / w));
                Vector3 absPos = basePose.fkPos[b];
                Vector3 storedPos = ow ? absPos : below.fkPos[b] + (absPos - below.fkPos[b]) / w;
                WriteCopiedBone(b, stored, storedPos);
                n++;
            }
            project.Touch();
            EditorUtility.SetDirty(project);
            Refresh();
            status = "已从基础层拷贝 " + n + " 根骨骼的相对变换";
        }

        private void WriteCopiedBone(int b, Quaternion rot, Vector3 pos)
        {
            int layer = project.activeLayer;
            AsiKeyBone bone = project.bones[b];
            bool created;
            if (bone.isCom)
            {
                AsiKeyTrack hr = project.GetTrack(layer, AsiTrackKind.ComHorizontal, b);
                AsiKey kh = AsiKeyEdit.GetOrInsertKey(project, hr, frame, out created);
                kh.pos = pos;
                AsiKeyEdit.AfterEdit(project, hr, layer, kh);
                AsiKeyTrack vr = project.GetTrack(layer, AsiTrackKind.ComVertical, b);
                AsiKey kv = AsiKeyEdit.GetOrInsertKey(project, vr, frame, out created);
                kv.pos = pos;
                AsiKeyEdit.AfterEdit(project, vr, layer, kv);
                AsiKeyTrack rr = project.GetTrack(layer, AsiTrackKind.ComRotation, b);
                AsiKey kr = AsiKeyEdit.GetOrInsertKey(project, rr, frame, out created);
                AsiKeyEdit.EnsureArrays(project, rr, kr);
                AsiKeyEdit.WriteRot(rr, kr, 0, rot);
                AsiKeyEdit.AfterEdit(project, rr, layer, kr);
                return;
            }
            int c = project.ChainOf(b);
            if (c >= 0)
            {
                AsiKeyTrack tr = project.GetTrack(layer, AsiTrackKind.Chain, c);
                AsiKey k = AsiKeyEdit.GetOrInsertKey(project, tr, frame, out created);
                AsiKeyEdit.EnsureArrays(project, tr, k);
                if (created)
                {
                    bool ow = project.ActiveLayer.overwrite;
                    for (int m = 0; m < project.chains[c].bones.Length; m++)
                    {
                        int mb = project.chains[c].bones[m];
                        Quaternion fill = ow ? pose.fk[mb] : Quaternion.identity;
                        AsiKeyEdit.WriteRot(tr, k, m, fill);
                    }
                }
                int member = project.chains[c].MemberIndex(b);
                if (member >= 0)
                    AsiKeyEdit.WriteRot(tr, k, member, rot);
                AsiKeyEdit.AfterEdit(project, tr, layer, k);
                return;
            }
            AsiKeyTrack boneTrack = project.GetTrack(layer, AsiTrackKind.Bone, b);
            AsiKey bk = AsiKeyEdit.GetOrInsertKey(project, boneTrack, frame, out created);
            AsiKeyEdit.EnsureArrays(project, boneTrack, bk);
            AsiKeyEdit.WriteRot(boneTrack, bk, 0, rot);
            if (bone.animatePosition)
                bk.pos = pos;
            AsiKeyEdit.AfterEdit(project, boneTrack, layer, bk);
        }

        /// <summary>IK 链成员按看到的姿势记 FK 旋转，之后切到自由关键帧时肢体不跳。</summary>
        private Quaternion VisibleFK(int b)
        {
            return AsiKeyMath.Norm(pose.rot[b] * Quaternion.Inverse(pose.postRot[b] * pose.actRot[b]));
        }

        private void KeyBone(int i, AsiKeyType type)
        {
            int layer = project.activeLayer;
            AsiKeyBone b = project.bones[i];
            if (b.fingerTip)
            {
                int tipChain = AsiKeyFingers.TipChain(project, i);
                if (tipChain >= 0 && !_keyedChains.Contains(tipChain))
                {
                    _keyedChains.Add(tipChain);
                    KeyChain(tipChain, layer, type);
                }
                return;
            }
            if (b.isCom)
            {
                KeyCom(i, layer);
                return;
            }
            int c = project.ChainOf(i);
            if (c >= 0)
            {
                if (!_keyedChains.Contains(c))
                {
                    _keyedChains.Add(c);
                    KeyChain(c, layer, type);
                }
                return;
            }
            AsiKeyTrack tr = project.GetTrack(layer, AsiTrackKind.Bone, i);
            bool created;
            AsiKey k = AsiKeyEdit.GetOrInsertKey(project, tr, frame, out created);
            AsiKeyEdit.WriteRot(tr, k, 0, layer == 0 ? pose.fk[i] : StoreRot(i));
            if (b.animatePosition)
                k.pos = layer == 0 ? pose.fkPos[i] : StorePos(i);
            AddTouch(tr, layer, k);
        }

        private void KeyCom(int i, int layer)
        {
            if (!comKeyH && !comKeyV && !comKeyR)
            {
                status = "质心的水平/垂直/旋转轨道都没打开";
                return;
            }
            Vector3 pos = layer == 0 ? pose.fkPos[i] : StorePos(i);
            bool created;
            if (comKeyH)
            {
                AsiKeyTrack tr = project.GetTrack(layer, AsiTrackKind.ComHorizontal, i);
                AsiKey k = AsiKeyEdit.GetOrInsertKey(project, tr, frame, out created);
                k.pos = pos;
                AddTouch(tr, layer, k);
            }
            if (comKeyV)
            {
                AsiKeyTrack tr = project.GetTrack(layer, AsiTrackKind.ComVertical, i);
                AsiKey k = AsiKeyEdit.GetOrInsertKey(project, tr, frame, out created);
                k.pos = pos;
                AddTouch(tr, layer, k);
            }
            if (comKeyR)
            {
                AsiKeyTrack tr = project.GetTrack(layer, AsiTrackKind.ComRotation, i);
                AsiKey k = AsiKeyEdit.GetOrInsertKey(project, tr, frame, out created);
                AsiKeyEdit.WriteRot(tr, k, 0, layer == 0 ? pose.fk[i] : StoreRot(i));
                AddTouch(tr, layer, k);
            }
        }

        private static AsiKeyType StateType(AsiKey k)
        {
            if (k.ikBlend < 0.5f)
                return AsiKeyType.Free;
            return k.joinToPrev ? AsiKeyType.Planted : AsiKeyType.Sliding;
        }

        private static void ClearBinding(AsiKey k)
        {
            k.ikSpace = AsiIKSpace.World;
            k.ikBone = -1;
            k.ikObject = "";
        }

        private static void CopyBinding(AsiKey dst, AsiKey src)
        {
            dst.ikSpace = src.ikSpace;
            dst.ikBone = src.ikBone;
            dst.ikObject = src.ikObject ?? "";
        }

        private void KeyChain(int c, int layer, AsiKeyType type)
        {
            AsiKeyChain chain = project.chains[c];
            AsiKeyTrack tr = project.GetTrack(layer, AsiTrackKind.Chain, c);
            bool created;
            AsiKey k = AsiKeyEdit.GetOrInsertKey(project, tr, frame, out created);
            for (int m = 0; m < chain.bones.Length; m++)
            {
                int b = chain.bones[m];
                Quaternion q = layer == 0 ? VisibleFK(b) : StoreRot(b);
                AsiKeyEdit.WriteRot(tr, k, m, q);
            }
            AddTouch(tr, layer, k);
            if (layer != 0)
                return;

            int idx = tr.keys.IndexOf(k);
            AsiKey prev = idx > 0 ? tr.keys[idx - 1] : null;
            AsiKey next = idx + 1 < tr.keys.Count ? tr.keys[idx + 1] : null;
            AsiChainState s = pose.chains[c];
            switch (type)
            {
                case AsiKeyType.Planted:
                    k.type = AsiKeyType.Planted;
                    k.ikBlend = 1f;
                    k.pivot = PivotChoice(c);
                    if (prev == null)
                    {
                        k.joinToPrev = false;
                        ClearBinding(k);
                    }
                    else
                    {
                        k.joinToPrev = true;
                        CopyBinding(k, prev);
                    }
                    break;
                case AsiKeyType.Sliding:
                    k.type = AsiKeyType.Sliding;
                    k.ikBlend = 1f;
                    k.joinToPrev = false;
                    k.pivot = PivotChoice(c);
                    if (created || k.ikSpace == AsiIKSpace.Body)
                        ClearBinding(k);
                    break;
                case AsiKeyType.Free:
                    k.type = AsiKeyType.Free;
                    k.ikBlend = 0f;
                    k.joinToPrev = false;
                    k.ikSpace = AsiIKSpace.Body;
                    k.ikBone = -1;
                    k.ikObject = "";
                    k.pivot = 0;
                    break;
                default:
                    if (created)
                    {
                        AsiKey src = prev ?? next;
                        if (src != null)
                        {
                            k.CopyIKFrom(src);
                            k.type = StateType(src);
                        }
                        else
                        {
                            k.type = AsiKeyType.Free;
                            k.ikBlend = 0f;
                            k.joinToPrev = false;
                            k.ikSpace = AsiIKSpace.Body;
                        }
                    }
                    break;
            }
            if (StableKnee(chain))
            {
                AsiChainState r = s;
                r.refRot = VisibleFK(chain.bones[0]);
                r.hasRef = true;
                r.hasDrag = false;
                pose.chains[c] = r;
                k.swivel = AsiKeyIKSolver.MeasureSwivel(project, chain, pose, 0f);
                pose.chains[c] = s;
            }
            else if (s.valid)
                k.swivel = s.swivel;
            int eff = chain.Effector;
            AsiChainTarget t = AsiKeyEvaluator.ToSpace(project, chain, pose, ctx, k.ikSpace, k.ikBone, k.ikObject, pose.wpos[eff], pose.wrot[eff]);
            k.effPos = t.pos;
            k.effRot = t.rot;
        }

        // ---------------- 删除 / 跳转 ----------------

        /// <summary>选中骨骼在指定层上对应的轨道（质心按水平/垂直/旋转开关）。</summary>
        public void TracksFor(List<int> bones, int layer, List<AsiKeyTrack> result, bool create)
        {
            result.Clear();
            for (int k = 0; k < bones.Count; k++)
            {
                int i = bones[k];
                if (i < 0 || i >= project.bones.Count)
                    continue;
                if (project.bones[i].isCom)
                {
                    if (comKeyH) AddTrack(result, layer, AsiTrackKind.ComHorizontal, i, create);
                    if (comKeyV) AddTrack(result, layer, AsiTrackKind.ComVertical, i, create);
                    if (comKeyR) AddTrack(result, layer, AsiTrackKind.ComRotation, i, create);
                    continue;
                }
                int c = project.ChainOf(i);
                if (c < 0)
                    c = AsiKeyFingers.TipChain(project, i);
                if (c >= 0)
                    AddTrack(result, layer, AsiTrackKind.Chain, c, create);
                else
                    AddTrack(result, layer, AsiTrackKind.Bone, i, create);
            }
            if (create)
                return;
            for (int k = 0; k < project.constraints.Count; k++)
            {
                if (bones.Contains(project.constraints[k].bone))
                    AddTrack(result, layer, AsiTrackKind.Constraint, k, false);
            }
        }

        /// <summary>在当前帧给约束 k 的影响度打关键帧（当前层；叠加层写相对下层的增量）。</summary>
        public void KeyConstraintWeight(int k, float value)
        {
            if (!Valid || k < 0 || k >= project.constraints.Count)
                return;
            Undo.RecordObject(project, "约束影响度关键帧");
            int layer = project.activeLayer;
            float v = value;
            if (layer > 0)
            {
                EnsureBaseline();
                _baseValid = false;
                float below = k < _basePose.conWeight.Length ? _basePose.conWeight[k] : project.constraints[k].weight;
                v = (value - below) / LayerWeight;
            }
            AsiKeyTrack tr = project.GetTrack(layer, AsiTrackKind.Constraint, k);
            bool created;
            AsiKey key = AsiKeyEdit.GetOrInsertKey(project, tr, frame, out created);
            key.pos = new Vector3(v, 0f, 0f);
            AsiKeyEdit.AfterEdit(project, tr, layer, key);
            project.Touch();
            EditorUtility.SetDirty(project);
            Refresh();
        }

        private void AddTrack(List<AsiKeyTrack> result, int layer, AsiTrackKind kind, int target, bool create)
        {
            AsiKeyTrack t = create ? project.GetTrack(layer, kind, target) : project.FindTrack(layer, kind, target);
            if (t != null && !result.Contains(t))
                result.Add(t);
        }

        public void DeleteKeysAtFrame()
        {
            if (!Valid)
                return;
            List<AsiKeyTrack> tracks = new List<AsiKeyTrack>();
            List<int> sel = SelectedBones();
            if (sel.Count == 0)
            {
                status = "先选择控制器";
                return;
            }
            TracksFor(sel, project.activeLayer, tracks, false);
            Undo.RecordObject(project, "删除关键帧");
            int removed = 0;
            for (int t = 0; t < tracks.Count; t++)
            {
                int idx = AsiKeyTrackEval.FindKeyIndex(tracks[t].keys, frame);
                if (idx < 0)
                    continue;
                tracks[t].keys.RemoveAt(idx);
                removed++;
                AsiKeyEdit.AfterEdit(project, tracks[t], project.activeLayer, null);
            }
            status = removed > 0 ? "删除了 " + removed + " 个关键帧" : "当前帧没有关键帧";
            project.Touch();
            EditorUtility.SetDirty(project);
            Refresh();
        }

        public void JumpKey(int dir)
        {
            if (!Valid)
                return;
            List<AsiKeyTrack> tracks = new List<AsiKeyTrack>();
            List<int> sel = SelectedBones();
            if (sel.Count > 0)
                TracksFor(sel, project.activeLayer, tracks, false);
            else
                tracks.AddRange(project.ActiveLayer.tracks);
            float best = dir > 0 ? float.MaxValue : float.MinValue;
            bool found = false;
            for (int t = 0; t < tracks.Count; t++)
            {
                List<AsiKey> keys = tracks[t].keys;
                for (int k = 0; k < keys.Count; k++)
                {
                    float f = keys[k].frame;
                    if (dir > 0 && f > frame + 0.01f && f < best)
                    {
                        best = f;
                        found = true;
                    }
                    else if (dir < 0 && f < frame - 0.01f && f > best)
                    {
                        best = f;
                        found = true;
                    }
                }
            }
            if (found)
                GoTo(best);
        }

        // ---------------- 关键帧 IK 参数编辑 ----------------

        /// <summary>
        /// 固定关键帧的绑定跟着时间上的上一帧：复制它的空间，没有上一帧就绑世界并且不连接（按滑动工作）。
        /// 空间变了时按该帧原来的世界姿势换算。滑动关键帧上的身体空间改成世界。不改滑动关键帧已经记下的相对位置和角度。
        /// </summary>
        public void SyncPlantedBindings(AsiKeyTrack tr)
        {
            if (!Valid || tr == null || tr.kind != AsiTrackKind.Chain || tr.target < 0 || tr.target >= project.chains.Count)
                return;
            AsiKeyChain chain = project.chains[tr.target];
            List<AsiKey> keys = tr.keys;
            for (int i = 0; i < keys.Count; i++)
            {
                AsiKey key = keys[i];
                if (key.type == AsiKeyType.Sliding && key.ikSpace == AsiIKSpace.Body)
                {
                    RewriteBinding(chain, key, AsiIKSpace.World, -1, "");
                    continue;
                }
                if (key.type != AsiKeyType.Planted)
                    continue;
                AsiIKSpace space = AsiIKSpace.World;
                int bone = -1;
                string obj = "";
                bool join = false;
                if (i > 0)
                {
                    AsiKey prev = keys[i - 1];
                    space = prev.ikSpace;
                    bone = space == AsiIKSpace.Object && prev.ikBone >= 0 ? prev.ikBone : -1;
                    obj = space == AsiIKSpace.Object && bone < 0 ? prev.ikObject ?? "" : "";
                    join = true;
                }
                if (!SameBinding(key, space, bone, obj))
                    RewriteBinding(chain, key, space, bone, obj);
                key.joinToPrev = join;
            }
        }

        private static bool SameBinding(AsiKey key, AsiIKSpace space, int bone, string obj)
        {
            if (key.ikSpace != space)
                return false;
            if (space != AsiIKSpace.Object)
                return key.ikBone < 0 && string.IsNullOrEmpty(key.ikObject);
            return key.ikBone == bone && (key.ikObject ?? "") == (obj ?? "");
        }

        /// <summary>把关键帧的目标换到新绑定，该帧上的世界位置和朝向不变。</summary>
        private void RewriteBinding(AsiKeyChain chain, AsiKey key, AsiIKSpace space, int bone, string obj)
        {
            int chainIndex = project.chains.IndexOf(chain);
            if (space == AsiIKSpace.Object && bone >= 0 && AsiKeyEvaluator.HasCycle(project, chainIndex, bone))
            {
                status = "IK 物体挂在本链下面会形成循环，已改用世界空间";
                space = AsiIKSpace.World;
                bone = -1;
                obj = "";
            }
            AsiKeyEvaluator.Evaluate(project, ctx, key.frame, _tmpPose, 0, -1);
            AsiChainTarget src;
            src.space = key.ikSpace;
            src.bone = key.ikBone;
            src.obj = key.ikObject;
            src.pos = key.effPos;
            src.rot = key.effRot;
            Vector3 wp;
            Quaternion wr;
            AsiKeyEvaluator.ResolveTarget(project, chain, _tmpPose, ctx, src, out wp, out wr);
            AsiChainTarget t = AsiKeyEvaluator.ToSpace(project, chain, _tmpPose, ctx, space, bone, obj ?? "", wp, wr);
            key.ikSpace = space;
            key.ikBone = space == AsiIKSpace.Object && bone >= 0 ? bone : -1;
            key.ikObject = space == AsiIKSpace.Object && key.ikBone < 0 ? obj ?? "" : "";
            key.effPos = t.pos;
            key.effRot = t.rot;
        }

        /// <summary>把某个链关键帧的目标换到新空间，保持该帧姿势不跳。后面的固定关键帧跟着改绑定。</summary>
        public void ConvertKeySpace(AsiKeyTrack tr, AsiKey key, AsiIKSpace space, int bone, string obj)
        {
            if (tr.kind != AsiTrackKind.Chain || tr.target < 0 || tr.target >= project.chains.Count)
                return;
            AsiKeyChain chain = project.chains[tr.target];
            if (space == AsiIKSpace.Object && bone >= 0 && AsiKeyEvaluator.HasCycle(project, tr.target, bone))
            {
                status = "IK 物体挂在本链下面会形成循环";
                return;
            }
            Undo.RecordObject(project, "切换 IK 空间");
            AsiKeyEvaluator.Evaluate(project, ctx, key.frame, _tmpPose, 0, -1);
            AsiChainTarget src;
            src.space = key.ikSpace;
            src.bone = key.ikBone;
            src.obj = key.ikObject;
            src.pos = key.effPos;
            src.rot = key.effRot;
            Vector3 wp;
            Quaternion wr;
            AsiKeyEvaluator.ResolveTarget(project, chain, _tmpPose, ctx, src, out wp, out wr);
            AsiChainTarget t = AsiKeyEvaluator.ToSpace(project, chain, _tmpPose, ctx, space, bone, obj ?? "", wp, wr);
            key.ikSpace = space;
            key.ikBone = space == AsiIKSpace.Object ? bone : -1;
            key.ikObject = space == AsiIKSpace.Object && bone < 0 ? obj ?? "" : "";
            key.effPos = t.pos;
            key.effRot = t.rot;
            AsiKeyEdit.AfterEdit(project, tr, 0, key);
            project.Touch();
            EditorUtility.SetDirty(project);
            Refresh();
        }

        /// <summary>把某个链关键帧改成指定类型，目标取该帧当前可见的末端姿势。</summary>
        public void SetKeyType(AsiKeyTrack tr, AsiKey key, AsiKeyType type)
        {
            if (tr.kind != AsiTrackKind.Chain)
            {
                key.type = type;
                return;
            }
            Undo.RecordObject(project, "修改关键帧类型");
            float keep = frame;
            int prevLayer = project.activeLayer;
            project.activeLayer = 0;
            frame = key.frame;
            Evaluate();
            List<int> bones = new List<int>(project.chains[tr.target].bones);
            KeyBones(bones, type, false);
            project.activeLayer = prevLayer;
            GoTo(keep);
        }

        /// <summary>在第 0 帧给单位 unit（-1 为全部）的骨骼打一次绑定姿势的注册关键帧（基础层，质心三条轨道都打）。只在新建骨骼后调用。</summary>
        public void KeyRestPose(int unit = -1)
        {
            if (!Valid || project.bones.Count == 0)
                return;
            float keep = frame;
            int prevLayer = project.activeLayer;
            bool h = comKeyH, v = comKeyV, r = comKeyR;
            project.activeLayer = 0;
            comKeyH = comKeyV = comKeyR = true;
            frame = 0f;
            Evaluate();
            List<int> bones = new List<int>();
            for (int i = 0; i < project.bones.Count; i++)
            {
                if (unit < 0 || project.bones[i].unit == unit)
                    bones.Add(i);
            }
            KeyBones(bones, AsiKeyType.Set, false);
            comKeyH = h;
            comKeyV = v;
            comKeyR = r;
            project.activeLayer = prevLayer;
            GoTo(keep);
        }

        /// <summary>三成员链（稳定膝/肘）：FK 姿势能量出对应的 swivel。</summary>
        private static bool StableKnee(AsiKeyChain chain)
        {
            return chain.bones.Length == 3;
        }

        /// <summary>重算 IKBlend 小于 0.5 的链关键帧的身体空间目标与 swivel（建链、导入后调用）。</summary>
        public void RecomputeFreeTargets(int c)
        {
            AsiKeyTrack tr = project.FindTrack(0, AsiTrackKind.Chain, c);
            if (tr == null)
                return;
            AsiKeyChain chain = project.chains[c];
            int eff = chain.Effector;
            for (int k = 0; k < tr.keys.Count; k++)
            {
                AsiKey key = tr.keys[k];
                if (key.ikBlend >= 0.5f)
                    continue;
                AsiKeyEvaluator.Evaluate(project, ctx, key.frame, _tmpPose, 0, -1);
                AsiChainTarget t = AsiKeyEvaluator.ToSpace(project, chain, _tmpPose, ctx, key.ikSpace, key.ikBone, key.ikObject,
                    _tmpPose.wpos[eff], _tmpPose.wrot[eff]);
                key.effPos = t.pos;
                key.effRot = t.rot;
                if (StableKnee(chain))
                    key.swivel = AsiKeyIKSolver.MeasureSwivel(project, chain, _tmpPose, 0f);
            }
        }
    }
}
