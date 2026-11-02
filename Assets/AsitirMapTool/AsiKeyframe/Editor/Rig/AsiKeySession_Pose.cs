using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 整身姿势操作：选中角色全部骨骼、重置到原始姿势 / 设为原始姿势、姿势库的复制与粘贴，以及它们共用的姿势写入 <see cref="ApplyPose"/>。
    /// </summary>
    public partial class AsiKeySession
    {
        // ---------------- 选择 ----------------

        /// <summary>选中「当前角色」（选中骨骼所在的角色单位）的全部骨骼，包括隐藏的骨骼。</summary>
        public void SelectCharacterBones()
        {
            if (!Valid)
                return;
            List<int> units = SelectedCharacterUnits();
            if (units.Count == 0)
            {
                status = "先选中角色的任意一根骨骼";
                return;
            }
            List<int> list = new List<int>();
            for (int i = 0; i < project.bones.Count; i++)
            {
                if (units.Contains(project.bones[i].unit))
                    list.Add(i);
            }
            SelectBones(list, false);
            status = "已选中 " + list.Count + " 根骨骼";
            RaiseRepaint();
        }

        private List<int> SelectedCharacterUnits()
        {
            List<int> units = new List<int>();
            List<int> sel = SelectedBones();
            for (int k = 0; k < sel.Count; k++)
            {
                int u = project.bones[sel[k]].unit;
                if (project.IsCharacter(u) && !units.Contains(u))
                    units.Add(u);
            }
            return units;
        }

        // ---------------- 原始姿势 ----------------

        /// <summary>选中骨骼按相对父级的局部姿态复原到原始姿势；质心只复原旋转，其余记录位移的骨骼连位置一起复原。</summary>
        public void ResetSelected()
        {
            if (!Valid)
                return;
            List<int> sel = SelectedBones();
            if (sel.Count == 0)
            {
                status = "先选择要重置的骨骼";
                return;
            }
            int n = project.bones.Count;
            bool[] changed = new bool[n];
            Quaternion[] outRot = new Quaternion[n];
            Vector3[] outPos = new Vector3[n];
            bool hasCom = false;
            for (int k = 0; k < sel.Count; k++)
            {
                int i = sel[k];
                AsiKeyBone b = project.bones[i];
                changed[i] = true;
                outRot[i] = b.hasOrig ? b.origRot : b.restRot;
                outPos[i] = Movable(i) && !b.isCom ? (b.hasOrig ? b.origPos : b.restPos) : pose.pos[i];
                hasCom |= b.isCom;
            }
            List<int> keyed = ApplyPose(changed, outRot, outPos);
            if (string.IsNullOrEmpty(status))
                status = "已重置 " + keyed.Count + " 根骨骼" + (hasCom ? "（质心只重置旋转）" : "");
            if (autoKey && keyed.Count > 0)
                KeyBones(keyed, AsiKeyType.Set, true);
        }

        /// <summary>把选中骨骼当前看到的局部姿态存为原始姿势（「重置选中骨骼」的目标）。</summary>
        public void SetOriginalPose()
        {
            if (!Valid)
                return;
            List<int> sel = SelectedBones();
            if (sel.Count == 0)
            {
                status = "先选择要设为原始姿势的骨骼";
                return;
            }
            Undo.RecordObject(project, "设为原始姿势");
            for (int k = 0; k < sel.Count; k++)
            {
                AsiKeyBone b = project.bones[sel[k]];
                b.hasOrig = true;
                b.origRot = AsiKeyMath.Norm(pose.rot[sel[k]]);
                b.origPos = pose.pos[sel[k]];
            }
            EditorUtility.SetDirty(project);
            status = "已把 " + sel.Count + " 根骨骼的当前姿态设为原始姿势";
        }

        // ---------------- 写入姿势 ----------------

        /// <summary>
        /// 把 changed 标记的骨骼的可见局部姿态改成 outRot（记录位移的骨骼还有 outPos），返回改了的骨骼（求值顺序）。
        /// IK 生效的链只要有成员被改，就像 FK 一样写入成员再把末端落到的位姿写回 IK 目标（<see cref="SlideFK"/> 同一套），
        /// 叠加层不改 IK，这些成员跳过。
        /// </summary>
        private List<int> ApplyPose(bool[] changed, Quaternion[] outRot, Vector3[] outPos)
        {
            _editedChains.Clear();
            _slideFK.Clear();
            status = "";
            bool post = SplitActive;
            bool baseLayer = project.activeLayer == 0;
            bool blocked = false;
            for (int c = 0; c < project.chains.Count && !post; c++)
            {
                AsiChainState s = pose.chains[c];
                if (!s.valid || s.blend < 0.5f)
                    continue;
                int[] members = project.chains[c].bones;
                bool any = false;
                for (int m = 0; m < members.Length; m++)
                    any |= changed[members[m]];
                if (!any)
                    continue;
                if (!baseLayer)
                {
                    blocked = true;
                    continue;
                }
                for (int m = 0; m < members.Length; m++)
                    pose.fk[members[m]] = VisibleFK(members[m]);
                _slideFK[c] = s;
                s.valid = false;
                pose.chains[c] = s;
            }
            List<int> keyed = new List<int>();
            int[] order = pose.order;
            for (int o = 0; o < order.Length; o++)
            {
                int i = order[o];
                if (!changed[i])
                    continue;
                int c = project.ChainOf(i);
                if (c >= 0 && !post && pose.chains[c].valid && pose.chains[c].blend >= 0.5f)
                    continue;
                keyed.Add(i);
                SetVisibleRot(i, AsiKeyMath.Norm(outRot[i]));
                if (Movable(i))
                    SetVisiblePos(i, outPos[i]);
            }
            EndSlideFK();
            SolveEdited();
            WriteAll();
            _poseDirty = true;
            InvalidateTrajectories();
            if (blocked)
                status = "叠加层不改 IK，IK 链成员没有改；关闭该层的「保持底层 IK」可以在 IK 之后叠加";
            return keyed;
        }

        // ---------------- 姿势库 ----------------

        /// <summary>
        /// 复制当前角色的姿势（全身 = 单位全部骨骼；局部 = 选中的骨骼，只取第一个选中骨骼所在单位的），连同缩略图。失败时返回 null 并写 status。
        /// </summary>
        public AsiKeyPoseEntry CapturePose(bool fullBody)
        {
            if (!Valid)
                return null;
            List<int> sel = SelectedBones();
            int u = sel.Count > 0 ? project.bones[sel[0]].unit : SoleCharacter();
            if (!project.IsCharacter(u))
            {
                status = "先选中角色的骨骼";
                return null;
            }
            if (!fullBody && sel.Count == 0)
            {
                status = "先选择要复制的骨骼";
                return null;
            }
            AsiKeyPoseEntry e = new AsiKeyPoseEntry();
            e.fullBody = fullBody;
            e.unitName = project.UnitName(u);
            e.created = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            Quaternion inv = Quaternion.Inverse(pose.UnitRot(u));
            Vector3 origin = pose.UnitPos(u);
            List<int> bones = project.UnitBones(u);
            Quaternion[] rot = new Quaternion[bones.Count];
            Vector3[] pos = new Vector3[bones.Count];
            bool[] focus = new bool[bones.Count];
            for (int k = 0; k < bones.Count; k++)
            {
                int i = bones[k];
                AsiKeyBone b = project.bones[i];
                AsiKeyPoseBone pb = new AsiKeyPoseBone();
                pb.name = b.name;
                pb.path = b.path;
                pb.humanBone = b.humanBone;
                pb.isCom = b.isCom;
                pb.copied = fullBody || sel.Contains(i);
                pb.rot = AsiKeyMath.Norm(inv * pose.wrot[i]);
                pb.pos = inv * (pose.wpos[i] - origin);
                e.bones.Add(pb);
                rot[k] = pb.rot;
                pos[k] = pb.pos;
                focus[k] = pb.copied;
            }
            e.thumbnail = AsiKeyPoseThumb.Render(project, bones, rot, pos, focus);
            return e;
        }

        private int SoleCharacter()
        {
            int found = -1;
            for (int u = 0; u < project.units.Count; u++)
            {
                if (!project.IsCharacter(u))
                    continue;
                if (found >= 0)
                    return -1;
                found = u;
            }
            return found;
        }

        /// <summary>
        /// 粘贴姿势库里的姿势。目标角色 = 选中骨骼所在的角色单位，没有选中时按复制时的角色名（只有一个角色时就是它）。
        /// 全身姿势粘贴到整个角色；局部姿势有选中时只粘贴选中骨骼，没有选中时粘贴到复制时的骨骼（镜像时是对侧骨骼）。
        /// 朝向相对父级（父级没被粘贴时姿态跟着当前父级走）；mirror 时每根骨骼取对侧骨骼的姿态镜像；质心按 comH / comV / comR 选择性粘贴。
        /// </summary>
        public bool PasteLibraryPose(AsiKeyPoseEntry e, bool mirror, bool comH, bool comV, bool comR)
        {
            if (!Valid || e == null || e.bones.Count == 0)
                return false;
            List<int> sel = SelectedBones();
            List<int> units = SelectedCharacterUnits();
            if (units.Count == 0)
            {
                int u = -1;
                for (int k = 0; k < project.units.Count && u < 0; k++)
                {
                    if (project.IsCharacter(k) && project.units[k].name == e.unitName)
                        u = k;
                }
                if (u < 0)
                    u = SoleCharacter();
                if (u < 0)
                {
                    status = "先选中要粘贴到的角色的骨骼";
                    return false;
                }
                units.Add(u);
            }
            int n = project.bones.Count;
            int[] src = new int[n];
            for (int i = 0; i < n; i++)
                src[i] = -1;
            for (int k = 0; k < units.Count; k++)
                MatchPoseBones(e, units[k], src);

            bool[] has = new bool[n];
            Quaternion[] cw = new Quaternion[n];
            Vector3[] cp = new Vector3[n];
            bool[] target = new bool[n];
            bool anyTarget = false;
            for (int i = 0; i < n; i++)
            {
                AsiKeyBone b = project.bones[i];
                if (!units.Contains(b.unit))
                    continue;
                int j = mirror && b.mirror >= 0 && b.mirror < n ? b.mirror : i;
                int s = src[j];
                if (s < 0)
                    continue;
                AsiKeyPoseBone sb = e.bones[s];
                has[i] = true;
                if (mirror)
                {
                    AsiKeyBone jb = project.bones[j];
                    cw[i] = AsiKeyMath.Norm(AsiKeyMath.MirrorX(sb.rot * Quaternion.Inverse(jb.rigRestRot)) * b.rigRestRot);
                    cp[i] = b.rigRestPos + AsiKeyMath.MirrorX(sb.pos - jb.rigRestPos);
                }
                else
                {
                    cw[i] = sb.rot;
                    cp[i] = sb.pos;
                }
                bool pick;
                if (e.fullBody)
                    pick = true;
                else if (sel.Count > 0)
                    pick = sel.Contains(i) && sb.copied;
                else
                    pick = sb.copied;
                if (pick && b.isCom && !comH && !comV && !comR)
                    pick = false;
                target[i] = pick;
                anyTarget |= pick;
            }
            if (!anyTarget)
            {
                status = sel.Count > 0 && !e.fullBody ? "选中的骨骼在这个局部姿势里没有对应（" + (mirror ? "对侧" : "同名") + "）骨骼" : "这个姿势里没有能对应到目标角色的骨骼";
                return false;
            }

            bool[] changed = new bool[n];
            Quaternion[] outRot = new Quaternion[n];
            Vector3[] outPos = new Vector3[n];
            Quaternion[] newW = new Quaternion[n];
            Vector3[] newP = new Vector3[n];
            int[] order = pose.order;
            for (int o = 0; o < order.Length; o++)
            {
                int i = order[o];
                AsiKeyBone b = project.bones[i];
                int parent = b.parent;
                int anchor = project.AnchorOf(i);
                Quaternion unitRot = pose.UnitRot(b.unit);
                Vector3 unitPos = pose.UnitPos(b.unit);
                Quaternion parentW = parent >= 0 ? newW[parent] : unitRot;
                Vector3 parentP = parent >= 0 ? newP[parent] : unitPos;
                Quaternion local = pose.rot[i];
                Vector3 lp = pose.pos[i];
                if (target[i])
                {
                    // 父级的姿态（单位坐标系）：父级在姿势里有就用姿势里的，否则用当前已算出的。
                    bool parentClip = parent >= 0 && has[parent];
                    Quaternion pw = parent < 0 ? Quaternion.identity : parentClip ? cw[parent] : AsiKeyMath.Norm(Quaternion.Inverse(unitRot) * parentW);
                    Vector3 pp = parent < 0 ? Vector3.zero : parentClip ? cp[parent] : Quaternion.Inverse(unitRot) * (parentP - unitPos);
                    Quaternion relRot = AsiKeyMath.Norm(Quaternion.Inverse(pw) * cw[i]);
                    Vector3 relPos = Quaternion.Inverse(pw) * (cp[i] - pp);
                    if (b.isCom)
                    {
                        if (comR)
                            local = relRot;
                        if (comH)
                        {
                            lp.x = relPos.x;
                            lp.z = relPos.z;
                        }
                        if (comV)
                            lp.y = relPos.y;
                    }
                    else
                    {
                        local = relRot;
                        int hc;
                        if (AsiKeyHinge.MidOf(project, i, out hc))
                            local = AsiKeyHinge.Project(project, project.chains[hc], local);
                        if (Movable(i))
                            lp = relPos;
                    }
                    changed[i] = true;
                    outRot[i] = local;
                    outPos[i] = lp;
                }
                newW[i] = AsiKeyMath.Norm(parentW * local);
                newP[i] = anchor >= 0 ? newP[anchor] + newW[anchor] * b.anchorPos : parentP + parentW * lp;
            }
            List<int> keyed = ApplyPose(changed, outRot, outPos);
            if (string.IsNullOrEmpty(status))
                status = "已粘贴「" + e.name + "」" + (mirror ? "（镜像）" : "") + "：" + keyed.Count + " 根骨骼";
            if (autoKey && keyed.Count > 0)
                KeyBones(keyed, AsiKeyType.Set, true);
            return true;
        }

        /// <summary>单位 u 的骨骼对应到姿势里的骨骼：质心对质心，人形骨骼按 humanBone，其余按路径，再按名字。</summary>
        private void MatchPoseBones(AsiKeyPoseEntry e, int u, int[] src)
        {
            for (int i = 0; i < project.bones.Count; i++)
            {
                AsiKeyBone b = project.bones[i];
                if (b.unit != u)
                    continue;
                int found = -1;
                for (int k = 0; k < e.bones.Count && found < 0; k++)
                {
                    AsiKeyPoseBone pb = e.bones[k];
                    if (b.isCom || pb.isCom)
                    {
                        if (b.isCom && pb.isCom)
                            found = k;
                        continue;
                    }
                    if (b.humanBone >= 0 || pb.humanBone >= 0)
                    {
                        if (b.humanBone == pb.humanBone)
                            found = k;
                        continue;
                    }
                    if (!string.IsNullOrEmpty(b.path) && b.path == pb.path)
                        found = k;
                }
                for (int k = 0; k < e.bones.Count && found < 0; k++)
                {
                    AsiKeyPoseBone pb = e.bones[k];
                    if (!pb.isCom && !b.isCom && pb.humanBone < 0 && b.humanBone < 0 && pb.name == b.name)
                        found = k;
                }
                src[i] = found;
            }
        }
    }
}
