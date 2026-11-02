using System;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>HumanBodyBones 到 Max Biped 部位的命名、分组与左右侧。</summary>
    public static class AsiKeyBipedMap
    {
        public enum Group { Pelvis, Spine, Neck, Head, Face, Clavicle, UpperLimb, LowerLimb, Hand, Finger, Thigh, Calf, Foot, Toe }

        public struct Entry
        {
            public HumanBodyBones bone;
            public string name;
            public AsiBoneSide side;
            public Group group;

            public Entry(HumanBodyBones bone, string name, AsiBoneSide side, Group group)
            {
                this.bone = bone;
                this.name = name;
                this.side = side;
                this.group = group;
            }
        }

        private const AsiBoneSide C = AsiBoneSide.Center;
        private const AsiBoneSide L = AsiBoneSide.Left;
        private const AsiBoneSide R = AsiBoneSide.Right;

        /// <summary>生成顺序：父级总在子级之前。</summary>
        public static readonly Entry[] Order =
        {
            new Entry(HumanBodyBones.Hips, "Bip Pelvis", C, Group.Pelvis),
            new Entry(HumanBodyBones.Spine, "Bip Spine", C, Group.Spine),
            new Entry(HumanBodyBones.Chest, "Bip Spine1", C, Group.Spine),
            new Entry(HumanBodyBones.UpperChest, "Bip Spine2", C, Group.Spine),
            new Entry(HumanBodyBones.Neck, "Bip Neck", C, Group.Neck),
            new Entry(HumanBodyBones.Head, "Bip Head", C, Group.Head),
            new Entry(HumanBodyBones.Jaw, "Bip Jaw", C, Group.Face),
            new Entry(HumanBodyBones.LeftEye, "Bip L Eye", L, Group.Face),
            new Entry(HumanBodyBones.RightEye, "Bip R Eye", R, Group.Face),

            new Entry(HumanBodyBones.LeftUpperLeg, "Bip L Thigh", L, Group.Thigh),
            new Entry(HumanBodyBones.LeftLowerLeg, "Bip L Calf", L, Group.Calf),
            new Entry(HumanBodyBones.LeftFoot, "Bip L Foot", L, Group.Foot),
            new Entry(HumanBodyBones.LeftToes, "Bip L Toe0", L, Group.Toe),
            new Entry(HumanBodyBones.RightUpperLeg, "Bip R Thigh", R, Group.Thigh),
            new Entry(HumanBodyBones.RightLowerLeg, "Bip R Calf", R, Group.Calf),
            new Entry(HumanBodyBones.RightFoot, "Bip R Foot", R, Group.Foot),
            new Entry(HumanBodyBones.RightToes, "Bip R Toe0", R, Group.Toe),

            new Entry(HumanBodyBones.LeftShoulder, "Bip L Clavicle", L, Group.Clavicle),
            new Entry(HumanBodyBones.LeftUpperArm, "Bip L UpperArm", L, Group.UpperLimb),
            new Entry(HumanBodyBones.LeftLowerArm, "Bip L Forearm", L, Group.LowerLimb),
            new Entry(HumanBodyBones.LeftHand, "Bip L Hand", L, Group.Hand),
            new Entry(HumanBodyBones.LeftThumbProximal, "Bip L Finger0", L, Group.Finger),
            new Entry(HumanBodyBones.LeftThumbIntermediate, "Bip L Finger01", L, Group.Finger),
            new Entry(HumanBodyBones.LeftThumbDistal, "Bip L Finger02", L, Group.Finger),
            new Entry(HumanBodyBones.LeftIndexProximal, "Bip L Finger1", L, Group.Finger),
            new Entry(HumanBodyBones.LeftIndexIntermediate, "Bip L Finger11", L, Group.Finger),
            new Entry(HumanBodyBones.LeftIndexDistal, "Bip L Finger12", L, Group.Finger),
            new Entry(HumanBodyBones.LeftMiddleProximal, "Bip L Finger2", L, Group.Finger),
            new Entry(HumanBodyBones.LeftMiddleIntermediate, "Bip L Finger21", L, Group.Finger),
            new Entry(HumanBodyBones.LeftMiddleDistal, "Bip L Finger22", L, Group.Finger),
            new Entry(HumanBodyBones.LeftRingProximal, "Bip L Finger3", L, Group.Finger),
            new Entry(HumanBodyBones.LeftRingIntermediate, "Bip L Finger31", L, Group.Finger),
            new Entry(HumanBodyBones.LeftRingDistal, "Bip L Finger32", L, Group.Finger),
            new Entry(HumanBodyBones.LeftLittleProximal, "Bip L Finger4", L, Group.Finger),
            new Entry(HumanBodyBones.LeftLittleIntermediate, "Bip L Finger41", L, Group.Finger),
            new Entry(HumanBodyBones.LeftLittleDistal, "Bip L Finger42", L, Group.Finger),

            new Entry(HumanBodyBones.RightShoulder, "Bip R Clavicle", R, Group.Clavicle),
            new Entry(HumanBodyBones.RightUpperArm, "Bip R UpperArm", R, Group.UpperLimb),
            new Entry(HumanBodyBones.RightLowerArm, "Bip R Forearm", R, Group.LowerLimb),
            new Entry(HumanBodyBones.RightHand, "Bip R Hand", R, Group.Hand),
            new Entry(HumanBodyBones.RightThumbProximal, "Bip R Finger0", R, Group.Finger),
            new Entry(HumanBodyBones.RightThumbIntermediate, "Bip R Finger01", R, Group.Finger),
            new Entry(HumanBodyBones.RightThumbDistal, "Bip R Finger02", R, Group.Finger),
            new Entry(HumanBodyBones.RightIndexProximal, "Bip R Finger1", R, Group.Finger),
            new Entry(HumanBodyBones.RightIndexIntermediate, "Bip R Finger11", R, Group.Finger),
            new Entry(HumanBodyBones.RightIndexDistal, "Bip R Finger12", R, Group.Finger),
            new Entry(HumanBodyBones.RightMiddleProximal, "Bip R Finger2", R, Group.Finger),
            new Entry(HumanBodyBones.RightMiddleIntermediate, "Bip R Finger21", R, Group.Finger),
            new Entry(HumanBodyBones.RightMiddleDistal, "Bip R Finger22", R, Group.Finger),
            new Entry(HumanBodyBones.RightRingProximal, "Bip R Finger3", R, Group.Finger),
            new Entry(HumanBodyBones.RightRingIntermediate, "Bip R Finger31", R, Group.Finger),
            new Entry(HumanBodyBones.RightRingDistal, "Bip R Finger32", R, Group.Finger),
            new Entry(HumanBodyBones.RightLittleProximal, "Bip R Finger4", R, Group.Finger),
            new Entry(HumanBodyBones.RightLittleIntermediate, "Bip R Finger41", R, Group.Finger),
            new Entry(HumanBodyBones.RightLittleDistal, "Bip R Finger42", R, Group.Finger),
        };

        public static int IndexOf(HumanBodyBones hb)
        {
            for (int i = 0; i < Order.Length; i++)
            {
                if (Order[i].bone == hb)
                    return i;
            }
            return -1;
        }

        public static Group GroupOf(int humanBone)
        {
            if (humanBone < 0)
                return Group.Spine;
            int i = IndexOf((HumanBodyBones)humanBone);
            return i >= 0 ? Order[i].group : Group.Spine;
        }

        /// <summary>按部位显隐的分组名，下标即 <see cref="PartOf"/> 的返回值。</summary>
        public static readonly string[] PartNames = { "手指", "脚趾", "面部（眼、下巴）", "手臂", "腿", "躯干与头颈" };

        /// <summary>人形骨骼所属的显隐部位；质心和非人形骨骼返回 -1。</summary>
        public static int PartOf(AsiKeyBone b)
        {
            if (b.isCom || b.humanBone < 0 || IndexOf((HumanBodyBones)b.humanBone) < 0)
                return -1;
            switch (GroupOf(b.humanBone))
            {
                case Group.Finger: return 0;
                case Group.Toe: return 1;
                case Group.Face: return 2;
                case Group.Clavicle:
                case Group.UpperLimb:
                case Group.LowerLimb:
                case Group.Hand:
                    return 3;
                case Group.Thigh:
                case Group.Calf:
                case Group.Foot:
                    return 4;
                default:
                    return 5;
            }
        }

        public static HumanBodyBones Mirror(HumanBodyBones hb)
        {
            string n = hb.ToString();
            string m;
            if (n.StartsWith("Left", StringComparison.Ordinal))
                m = "Right" + n.Substring(4);
            else if (n.StartsWith("Right", StringComparison.Ordinal))
                m = "Left" + n.Substring(5);
            else
                return hb;
            HumanBodyBones result;
            return Enum.TryParse(m, out result) ? result : hb;
        }

        private static readonly string[][] NamePairs =
        {
            new[] { "Left", "Right" },
            new[] { "left", "right" },
            new[] { "LEFT", "RIGHT" },
            new[] { "_L_", "_R_" },
            new[] { "_L", "_R" },
            new[] { ".L", ".R" },
            new[] { "_l", "_r" },
            new[] { ".l", ".r" },
            new[] { "L_", "R_" },
            new[] { "l_", "r_" },
            new[] { " L ", " R " },
            new[] { "L ", "R " },
            new[] { "-L", "-R" },
        };

        /// <summary>按常见命名规则猜左右侧与对侧名字，猜不到返回 Center 和空串。</summary>
        public static AsiBoneSide GuessSide(string name, out string mirrorName)
        {
            mirrorName = "";
            if (string.IsNullOrEmpty(name))
                return AsiBoneSide.Center;
            for (int i = 0; i < NamePairs.Length; i++)
            {
                string l = NamePairs[i][0];
                string r = NamePairs[i][1];
                bool suffixPair = l.Length == 2 && (l[0] == '_' || l[0] == '.' || l[0] == '-');
                bool prefixPair = l.Length == 2 && (l[1] == '_' || l[1] == ' ');
                if (suffixPair)
                {
                    if (name.EndsWith(l, StringComparison.Ordinal))
                    {
                        mirrorName = name.Substring(0, name.Length - l.Length) + r;
                        return AsiBoneSide.Left;
                    }
                    if (name.EndsWith(r, StringComparison.Ordinal))
                    {
                        mirrorName = name.Substring(0, name.Length - r.Length) + l;
                        return AsiBoneSide.Right;
                    }
                    continue;
                }
                if (prefixPair)
                {
                    if (name.StartsWith(l, StringComparison.Ordinal))
                    {
                        mirrorName = r + name.Substring(l.Length);
                        return AsiBoneSide.Left;
                    }
                    if (name.StartsWith(r, StringComparison.Ordinal))
                    {
                        mirrorName = l + name.Substring(r.Length);
                        return AsiBoneSide.Right;
                    }
                    continue;
                }
                int li = name.IndexOf(l, StringComparison.Ordinal);
                if (li >= 0)
                {
                    mirrorName = name.Substring(0, li) + r + name.Substring(li + l.Length);
                    return AsiBoneSide.Left;
                }
                int ri = name.IndexOf(r, StringComparison.Ordinal);
                if (ri >= 0)
                {
                    mirrorName = name.Substring(0, ri) + l + name.Substring(ri + r.Length);
                    return AsiBoneSide.Right;
                }
            }
            return AsiBoneSide.Center;
        }
    }
}
