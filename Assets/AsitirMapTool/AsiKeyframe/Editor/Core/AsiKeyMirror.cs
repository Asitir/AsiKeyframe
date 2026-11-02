using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>复制下来的姿势，全部骨骼相对所属单位坐标系的世界姿态。</summary>
    public class AsiKeyPoseClip
    {
        public Quaternion[] wrot;
        public Vector3[] wpos;
        public int stamp;
    }

    /// <summary>左右配对、姿势复制与（镜像）粘贴。镜像平面是各单位坐标系的 YZ 平面，配对只在同一单位内进行。</summary>
    public static class AsiKeyMirror
    {
        public static void AutoPair(AsiKeyProject p)
        {
            for (int i = 0; i < p.bones.Count; i++)
            {
                AsiKeyBone b = p.bones[i];
                b.mirror = -1;
                if (b.isCom)
                {
                    b.side = AsiBoneSide.Center;
                    continue;
                }
                if (b.humanBone >= 0)
                {
                    HumanBodyBones hb = (HumanBodyBones)b.humanBone;
                    HumanBodyBones m = AsiKeyBipedMap.Mirror(hb);
                    string n = hb.ToString();
                    b.side = n.StartsWith("Left") ? AsiBoneSide.Left : n.StartsWith("Right") ? AsiBoneSide.Right : AsiBoneSide.Center;
                    if (m != hb)
                        b.mirror = p.BoneByHuman(m, b.unit);
                    continue;
                }
                string mirrorName;
                b.side = AsiKeyBipedMap.GuessSide(b.name, out mirrorName);
                if (b.side != AsiBoneSide.Center)
                {
                    int j = p.BoneByName(mirrorName, b.unit);
                    b.mirror = j != i ? j : -1;
                }
            }
        }

        public static AsiKeyPoseClip Capture(AsiKeyProject p, AsiKeyPose pose)
        {
            AsiKeyPoseClip c = new AsiKeyPoseClip();
            int n = pose.count;
            c.wrot = new Quaternion[n];
            c.wpos = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                int u = p.bones[i].unit;
                Quaternion inv = Quaternion.Inverse(pose.UnitRot(u));
                c.wrot[i] = AsiKeyMath.Norm(inv * pose.wrot[i]);
                c.wpos[i] = inv * (pose.wpos[i] - pose.UnitPos(u));
            }
            c.stamp = pose.stamp;
            return c;
        }

        /// <summary>
        /// 计算粘贴后的姿态。mask 为空表示粘贴全部骨骼。局部结果写入 outRot/outPos，工程空间结果写入 newW/newP，changed 标记被改的骨骼。
        /// 铰链链的中间关节投影到铰链上（IK 生效且不拆分时由 IK 求解，不投影），其后的骨骼仍保持粘贴的朝向。
        /// </summary>
        public static void BuildPaste(AsiKeyProject p, AsiKeyPose pose, AsiKeyPoseClip clip, bool mirror, bool[] mask,
            Quaternion[] outRot, Vector3[] outPos, bool[] changed, Quaternion[] newW, Vector3[] newP, bool split = false)
        {
            for (int oi = 0; oi < pose.order.Length; oi++)
            {
                int i = pose.order[oi];
                AsiKeyBone b = p.bones[i];
                bool target = mask == null || (i < mask.Length && mask[i]);
                int src = i;
                if (mirror && b.mirror >= 0)
                    src = b.mirror;
                int parent = b.parent;
                int anchor = p.AnchorOf(i);
                Quaternion unitRot = pose.UnitRot(b.unit);
                Vector3 unitPos = pose.UnitPos(b.unit);
                Quaternion parentW = parent >= 0 ? newW[parent] : unitRot;
                Vector3 parentP = parent >= 0 ? newP[parent] : unitPos;
                changed[i] = false;
                if (!target || clip.wrot == null || src >= clip.wrot.Length)
                {
                    newW[i] = parentW * pose.rot[i];
                    newP[i] = anchor >= 0 ? newP[anchor] + newW[anchor] * b.anchorPos : parentP + parentW * pose.pos[i];
                    outRot[i] = pose.rot[i];
                    outPos[i] = pose.pos[i];
                    continue;
                }
                AsiKeyBone sb = p.bones[src];
                Quaternion w;
                Vector3 wp;
                if (mirror)
                {
                    Quaternion delta = clip.wrot[src] * Quaternion.Inverse(sb.rigRestRot);
                    w = AsiKeyMath.Norm(AsiKeyMath.MirrorX(delta) * b.rigRestRot);
                    wp = b.rigRestPos + AsiKeyMath.MirrorX(clip.wpos[src] - sb.rigRestPos);
                }
                else
                {
                    w = clip.wrot[src];
                    wp = clip.wpos[src];
                }
                w = AsiKeyMath.Norm(unitRot * w);
                wp = unitPos + unitRot * wp;
                Quaternion local = AsiKeyMath.Norm(Quaternion.Inverse(parentW) * w);
                int hc;
                if (AsiKeyHinge.MidOf(p, i, out hc) && (split || !IKOn(pose, hc)))
                    local = AsiKeyHinge.Project(p, p.chains[hc], local);
                Vector3 localPos = p.Movable(i) ? Quaternion.Inverse(parentW) * (wp - parentP) : pose.pos[i];
                outRot[i] = local;
                outPos[i] = localPos;
                newW[i] = parentW * local;
                newP[i] = anchor >= 0 ? newP[anchor] + newW[anchor] * b.anchorPos : parentP + parentW * localPos;
                changed[i] = true;
            }
        }

        private static bool IKOn(AsiKeyPose pose, int c)
        {
            if (c < 0 || pose.chains == null || c >= pose.chains.Length)
                return false;
            AsiChainState s = pose.chains[c];
            return s.valid && s.blend >= 0.5f;
        }
    }
}
