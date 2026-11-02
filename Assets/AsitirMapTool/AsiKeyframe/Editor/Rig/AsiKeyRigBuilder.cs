using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 骨骼定义的生成与结构编辑。读取模型姿势前，调用方要先停掉预览，让模型回到场景里的原始姿势。
    /// </summary>
    public static class AsiKeyRigBuilder
    {
        public const string ComName = "Bip COM";

        public static void RigSpace(Transform root, Transform t, out Vector3 pos, out Quaternion rot)
        {
            Quaternion inv = Quaternion.Inverse(root.rotation);
            pos = inv * (t.position - root.position);
            rot = AsiKeyMath.Norm(inv * t.rotation);
        }

        private static void SetLocalRest(AsiKeyProject p, AsiKeyBone b)
        {
            b.hasOrig = false;
            if (b.parent < 0)
            {
                b.restPos = b.rigRestPos;
                b.restRot = b.rigRestRot;
                return;
            }
            AsiKeyBone pb = p.bones[b.parent];
            Quaternion inv = Quaternion.Inverse(pb.rigRestRot);
            b.restPos = inv * (b.rigRestPos - pb.rigRestPos);
            b.restRot = AsiKeyMath.Norm(inv * b.rigRestRot);
        }

        public static void ResetAnimation(AsiKeyProject p)
        {
            p.layers.Clear();
            p.layers.Add(new AsiKeyLayer());
            p.activeLayer = 0;
        }

        public static bool IsHumanoid(Animator animator)
        {
            return animator != null && animator.isHuman && animator.avatar != null && animator.avatar.isHuman;
        }

        /// <summary>
        /// 重新生成整套骨骼，清空动画。按单位 0（或当前单位作用域）的类型生成；多单位工程里由 <see cref="AsiKeyUnits"/> 在临时工程上调用再合并。
        /// </summary>
        public static string Build(AsiKeyProject p, Animator animator)
        {
            p.bones.Clear();
            p.chains.Clear();
            ResetAnimation(p);
            string error = p.humanoid ? BuildHumanoid(p, animator) : BuildGeneric(p, animator);
            if (!string.IsNullOrEmpty(error))
                return error;
            DetachSpineFromPelvis(p);
            DetachBipedOrientation(p);
            ComputeAxes(p, animator.transform);
            float unit = UnitSize(p);
            for (int i = 0; i < p.bones.Count; i++)
                p.bones[i].shape = DefaultShape(p, i, unit);
            if (p.humanoid)
            {
                BuildHumanChains(p, unit);
                AsiKeyFingers.EnsureChains(p);
                AsiKeyFingers.AddTipPivots(p);
                AsiKeyFingers.AlignShapeFrames(p);
            }
            AsiKeyMirror.AutoPair(p);
            p.TouchStructure();
            return "";
        }

        private static string BuildHumanoid(AsiKeyProject p, Animator animator)
        {
            Transform root = animator.transform;
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            Transform spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            if (hips == null || spine == null)
                return "Avatar 缺少 Hips 或 Spine";

            Vector3 hipsPos, spinePos;
            Quaternion hipsRot, spineRot;
            RigSpace(root, hips, out hipsPos, out hipsRot);
            RigSpace(root, spine, out spinePos, out spineRot);

            AsiKeyBone com = new AsiKeyBone();
            com.name = ComName;
            com.path = "";
            com.isCom = true;
            com.parent = -1;
            com.side = AsiBoneSide.Center;
            com.rigRestPos = Vector3.Lerp(hipsPos, spinePos, Mathf.Clamp01(p.UnitComRatio(p.CurrentUnit)));
            com.rigRestRot = Quaternion.identity;
            SetLocalRest(p, com);
            p.bones.Add(com);

            Dictionary<Transform, int> map = new Dictionary<Transform, int>();
            for (int e = 0; e < AsiKeyBipedMap.Order.Length; e++)
            {
                AsiKeyBipedMap.Entry entry = AsiKeyBipedMap.Order[e];
                Transform t = animator.GetBoneTransform(entry.bone);
                if (t == null || map.ContainsKey(t))
                    continue;
                AsiKeyBone b = new AsiKeyBone();
                b.name = p.UniqueBoneName(entry.name);
                b.path = AnimationUtility.CalculateTransformPath(t, root);
                b.humanBone = (int)entry.bone;
                b.side = entry.side;
                b.parent = entry.bone == HumanBodyBones.Hips ? 0 : FindParentControl(map, t, root);
                if (b.parent < 0)
                    b.parent = 0;
                RigSpace(root, t, out b.rigRestPos, out b.rigRestRot);
                SetLocalRest(p, b);
                map[t] = p.bones.Count;
                p.bones.Add(b);
            }
            return "";
        }

        private static int FindParentControl(Dictionary<Transform, int> map, Transform t, Transform root)
        {
            Transform a = t.parent;
            while (a != null && a != root)
            {
                int idx;
                if (map.TryGetValue(a, out idx))
                    return idx;
                a = a.parent;
            }
            return -1;
        }

        private static string BuildGeneric(AsiKeyProject p, Animator animator)
        {
            Transform root = animator.transform;
            HashSet<Transform> set = new HashSet<Transform>();
            SkinnedMeshRenderer[] smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int s = 0; s < smrs.Length; s++)
            {
                Transform[] bs = smrs[s].bones;
                for (int i = 0; i < bs.Length; i++)
                {
                    if (bs[i] != null && bs[i] != root && bs[i].IsChildOf(root))
                        set.Add(bs[i]);
                }
                if (smrs[s].rootBone != null && smrs[s].rootBone != root && smrs[s].rootBone.IsChildOf(root))
                    set.Add(smrs[s].rootBone);
            }
            if (set.Count == 0)
            {
                Transform[] all = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != root && all[i].GetComponents<Component>().Length == 1)
                        set.Add(all[i]);
                }
            }
            if (set.Count == 0)
                return "没有找到骨骼（SkinnedMeshRenderer 的 bones 为空）";

            Dictionary<Transform, int> map = new Dictionary<Transform, int>();
            Stack<Transform> stack = new Stack<Transform>();
            for (int c = root.childCount - 1; c >= 0; c--)
                stack.Push(root.GetChild(c));
            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                if (set.Contains(t))
                {
                    AsiKeyBone b = new AsiKeyBone();
                    b.name = p.UniqueBoneName(t.name);
                    b.path = AnimationUtility.CalculateTransformPath(t, root);
                    b.parent = FindParentControl(map, t, root);
                    b.animatePosition = b.parent < 0;
                    RigSpace(root, t, out b.rigRestPos, out b.rigRestRot);
                    SetLocalRest(p, b);
                    map[t] = p.bones.Count;
                    p.bones.Add(b);
                }
                for (int c = t.childCount - 1; c >= 0; c--)
                    stack.Push(t.GetChild(c));
            }
            return "";
        }

        // ---------------- 骨骼方向与外形 ----------------

        private static int HumanNext(AsiKeyProject p, int i)
        {
            if (p.bones[i].humanBone < 0)
                return -1;
            HumanBodyBones hb = (HumanBodyBones)p.bones[i].humanBone;
            HumanBodyBones[] list;
            switch (hb)
            {
                case HumanBodyBones.Hips: list = new[] { HumanBodyBones.Spine }; break;
                case HumanBodyBones.Spine: list = new[] { HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head }; break;
                case HumanBodyBones.Chest: list = new[] { HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head }; break;
                case HumanBodyBones.UpperChest: list = new[] { HumanBodyBones.Neck, HumanBodyBones.Head }; break;
                case HumanBodyBones.Neck: list = new[] { HumanBodyBones.Head }; break;
                case HumanBodyBones.LeftShoulder: list = new[] { HumanBodyBones.LeftUpperArm }; break;
                case HumanBodyBones.RightShoulder: list = new[] { HumanBodyBones.RightUpperArm }; break;
                case HumanBodyBones.LeftUpperArm: list = new[] { HumanBodyBones.LeftLowerArm }; break;
                case HumanBodyBones.RightUpperArm: list = new[] { HumanBodyBones.RightLowerArm }; break;
                case HumanBodyBones.LeftLowerArm: list = new[] { HumanBodyBones.LeftHand }; break;
                case HumanBodyBones.RightLowerArm: list = new[] { HumanBodyBones.RightHand }; break;
                case HumanBodyBones.LeftHand: list = new[] { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftRingProximal }; break;
                case HumanBodyBones.RightHand: list = new[] { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightRingProximal }; break;
                case HumanBodyBones.LeftUpperLeg: list = new[] { HumanBodyBones.LeftLowerLeg }; break;
                case HumanBodyBones.RightUpperLeg: list = new[] { HumanBodyBones.RightLowerLeg }; break;
                case HumanBodyBones.LeftLowerLeg: list = new[] { HumanBodyBones.LeftFoot }; break;
                case HumanBodyBones.RightLowerLeg: list = new[] { HumanBodyBones.RightFoot }; break;
                case HumanBodyBones.LeftFoot: list = new[] { HumanBodyBones.LeftToes }; break;
                case HumanBodyBones.RightFoot: list = new[] { HumanBodyBones.RightToes }; break;
                default:
                {
                    string n = hb.ToString();
                    HumanBodyBones next;
                    if (n.EndsWith("Proximal") && Enum.TryParse(n.Replace("Proximal", "Intermediate"), out next))
                        list = new[] { next };
                    else if (n.EndsWith("Intermediate") && Enum.TryParse(n.Replace("Intermediate", "Distal"), out next))
                        list = new[] { next };
                    else
                        list = new HumanBodyBones[0];
                    break;
                }
            }
            for (int k = 0; k < list.Length; k++)
            {
                int j = p.BoneByHuman(list[k]);
                if (j >= 0)
                    return j;
            }
            return -1;
        }

        private static int SubtreeSize(AsiKeyProject p, int i)
        {
            int n = 0;
            for (int j = 0; j < p.bones.Count; j++)
            {
                if (j != i && p.IsUnder(j, i))
                    n++;
            }
            return n;
        }

        private static int GenericNext(AsiKeyProject p, int i)
        {
            int best = -1;
            int bestSize = -1;
            for (int j = 0; j < p.bones.Count; j++)
            {
                if (p.LinkParent(j) != i)
                    continue;
                int size = SubtreeSize(p, j);
                if (size > bestSize)
                {
                    best = j;
                    bestSize = size;
                }
            }
            return best;
        }

        public static float UnitSize(AsiKeyProject p)
        {
            int l = p.BoneByHuman(HumanBodyBones.LeftUpperLeg);
            int r = p.BoneByHuman(HumanBodyBones.RightUpperLeg);
            if (l >= 0 && r >= 0)
            {
                float d = (p.bones[l].rigRestPos - p.bones[r].rigRestPos).magnitude;
                if (d > 1e-3f)
                    return d;
            }
            List<float> lengths = new List<float>();
            for (int i = 0; i < p.bones.Count; i++)
            {
                if (!p.bones[i].isCom && p.bones[i].boneLength > 1e-4f)
                    lengths.Add(p.bones[i].boneLength);
            }
            if (lengths.Count == 0)
                return 0.2f;
            lengths.Sort();
            return Mathf.Max(0.01f, lengths[lengths.Count / 2]);
        }

        /// <summary>按主子骨计算每根骨骼的方向、长度和外形基准朝向。</summary>
        public static void ComputeAxes(AsiKeyProject p, Transform root)
        {
            float unit = 0.2f;
            int l = p.BoneByHuman(HumanBodyBones.LeftUpperLeg);
            int r = p.BoneByHuman(HumanBodyBones.RightUpperLeg);
            if (l >= 0 && r >= 0)
                unit = Mathf.Max(0.02f, (p.bones[l].rigRestPos - p.bones[r].rigRestPos).magnitude);
            int[] order = p.BuildOrder();
            for (int o = 0; o < order.Length; o++)
                ComputeAxis(p, order[o], root, unit);
        }

        public static void ComputeAxis(AsiKeyProject p, int i, Transform root, float unit)
        {
            AsiKeyBone b = p.bones[i];
            Vector3 dir = Vector3.zero;
            float len = 0f;
            AsiKeyBipedMap.Group group = AsiKeyBipedMap.GroupOf(b.humanBone);
            if (b.isCom)
            {
                dir = Vector3.up;
                len = unit * 0.3f;
            }
            else
            {
                int next = p.humanoid && b.humanBone >= 0 ? HumanNext(p, i) : GenericNext(p, i);
                if (next >= 0)
                {
                    dir = p.bones[next].rigRestPos - b.rigRestPos;
                    len = dir.magnitude;
                }
                if (len < 1e-5f && root != null && !string.IsNullOrEmpty(b.path))
                {
                    Transform t = root.Find(b.path);
                    if (t != null && t.childCount > 0)
                    {
                        Transform c = t.GetChild(0);
                        Vector3 cp;
                        Quaternion cr;
                        RigSpace(root, c, out cp, out cr);
                        dir = cp - b.rigRestPos;
                        len = dir.magnitude;
                    }
                }
                if (len < 1e-5f)
                    LeafDirection(p, i, unit, group, out dir, out len);
            }
            if (len < 1e-5f)
            {
                dir = Vector3.up;
                len = unit * 0.3f;
            }
            dir /= Mathf.Max(1e-6f, dir.magnitude);
            Quaternion inv = Quaternion.Inverse(b.rigRestRot);
            b.axis = inv * dir;
            b.boneLength = len;

            Vector3 hint;
            switch (group)
            {
                case AsiKeyBipedMap.Group.Hand:
                case AsiKeyBipedMap.Group.Finger:
                case AsiKeyBipedMap.Group.Foot:
                case AsiKeyBipedMap.Group.Toe:
                    hint = Vector3.up;
                    break;
                default:
                    hint = Vector3.forward;
                    break;
            }
            if (Mathf.Abs(Vector3.Dot(hint, dir)) > 0.9f)
                hint = hint == Vector3.up ? Vector3.forward : Vector3.up;
            b.frame = Quaternion.LookRotation(b.axis, inv * hint);
        }

        private static void LeafDirection(AsiKeyProject p, int i, float unit, AsiKeyBipedMap.Group group, out Vector3 dir, out float len)
        {
            AsiKeyBone b = p.bones[i];
            Vector3 parentDir = Vector3.up;
            float parentLen = unit * 0.3f;
            int link = p.LinkParent(i);
            if (link >= 0 && !p.bones[link].isCom)
            {
                AsiKeyBone pb = p.bones[link];
                parentDir = b.rigRestPos - pb.rigRestPos;
                parentLen = parentDir.magnitude;
                if (parentLen > 1e-5f)
                    parentDir /= parentLen;
                else
                    parentDir = Vector3.up;
            }
            switch (group)
            {
                case AsiKeyBipedMap.Group.Head:
                    dir = Vector3.up;
                    len = Mathf.Max(unit * 1.0f, parentLen * 1.5f);
                    break;
                case AsiKeyBipedMap.Group.Toe:
                {
                    Vector3 f = parentDir;
                    f.y = 0f;
                    dir = f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
                    len = unit * 0.35f;
                    break;
                }
                case AsiKeyBipedMap.Group.Foot:
                    dir = Vector3.forward;
                    len = unit * 0.5f;
                    break;
                case AsiKeyBipedMap.Group.Face:
                    dir = Vector3.forward;
                    len = b.humanBone == (int)HumanBodyBones.Jaw ? unit * 0.3f : unit * 0.08f;
                    break;
                case AsiKeyBipedMap.Group.Hand:
                    dir = parentDir;
                    len = unit * 0.4f;
                    break;
                case AsiKeyBipedMap.Group.Finger:
                    dir = parentDir;
                    len = parentLen * 0.8f;
                    break;
                default:
                    dir = parentDir;
                    len = Mathf.Max(parentLen * 0.6f, unit * 0.1f);
                    break;
            }
        }

        public static AsiKeyShape DefaultShape(AsiKeyProject p, int i, float unit)
        {
            AsiKeyBone b = p.bones[i];
            AsiKeyShape s = new AsiKeyShape();
            s.color = b.isCom ? AsiKeyLabels.ComColor : AsiKeyLabels.SideColor(b.side);
            float len = b.boneLength;
            if (b.isCom)
            {
                s.kind = AsiShapeKind.Ring;
                s.radiusX = unit * 0.8f;
                s.radiusY = unit * 0.55f;
                s.width = unit * 1.2f;
                s.height = unit * 0.8f;
                s.length = unit * 0.2f;
                return s;
            }
            if (!p.humanoid || b.humanBone < 0)
            {
                float w = Mathf.Clamp(len * 0.22f, unit * 0.08f, unit * 0.6f);
                s.width = w;
                s.height = w;
                s.length = len;
                s.radiusX = w;
                s.radiusY = w;
                return s;
            }
            float wf = 0.3f, hf = 0.3f, lf = 1f;
            switch (AsiKeyBipedMap.GroupOf(b.humanBone))
            {
                case AsiKeyBipedMap.Group.Pelvis:
                    wf = 1.4f; hf = 0.7f;
                    s.length = unit * 0.5f;
                    s.offset = new Vector3(0f, 0f, -unit * 0.25f);
                    lf = -1f;
                    break;
                case AsiKeyBipedMap.Group.Spine: wf = 1.1f; hf = 0.65f; lf = 0.95f; break;
                case AsiKeyBipedMap.Group.Neck: wf = 0.3f; hf = 0.3f; break;
                case AsiKeyBipedMap.Group.Head: wf = 0.8f; hf = 0.9f; break;
                case AsiKeyBipedMap.Group.Face:
                    if (b.humanBone == (int)HumanBodyBones.Jaw)
                    {
                        wf = 0.4f; hf = 0.15f;
                    }
                    else
                    {
                        s.kind = AsiShapeKind.Ring;
                        wf = 0.12f; hf = 0.12f;
                    }
                    break;
                case AsiKeyBipedMap.Group.Clavicle: wf = 0.18f; hf = 0.18f; break;
                case AsiKeyBipedMap.Group.UpperLimb: wf = 0.28f; hf = 0.28f; break;
                case AsiKeyBipedMap.Group.LowerLimb: wf = 0.24f; hf = 0.24f; break;
                case AsiKeyBipedMap.Group.Hand: wf = 0.4f; hf = 0.1f; break;
                case AsiKeyBipedMap.Group.Finger: wf = 0.07f; hf = 0.07f; break;
                case AsiKeyBipedMap.Group.Thigh: wf = 0.4f; hf = 0.4f; break;
                case AsiKeyBipedMap.Group.Calf: wf = 0.32f; hf = 0.32f; break;
                case AsiKeyBipedMap.Group.Foot: wf = 0.38f; hf = 0.12f; break;
                case AsiKeyBipedMap.Group.Toe: wf = 0.38f; hf = 0.1f; break;
            }
            s.width = unit * wf;
            s.height = unit * hf;
            if (lf > 0f)
                s.length = len * lf;
            s.radiusX = s.width * 0.75f;
            s.radiusY = s.height * 0.75f;
            return s;
        }

        // ---------------- IK 链 ----------------

        private static void BuildHumanChains(AsiKeyProject p, float unit)
        {
            AddHumanChain(p, "左腿", AsiBoneSide.Left, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, Vector3.forward, unit, true);
            AddHumanChain(p, "右腿", AsiBoneSide.Right, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, Vector3.forward, unit, true);
            AddHumanChain(p, "左臂", AsiBoneSide.Left, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, Vector3.back, unit, false);
            AddHumanChain(p, "右臂", AsiBoneSide.Right, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, Vector3.back, unit, false);
        }

        private static void AddHumanChain(AsiKeyProject p, string name, AsiBoneSide side, HumanBodyBones a, HumanBodyBones b, HumanBodyBones c,
            Vector3 pole, float unit, bool leg)
        {
            int ia = p.BoneByHuman(a);
            int ib = p.BoneByHuman(b);
            int ic = p.BoneByHuman(c);
            if (ia < 0 || ib < 0 || ic < 0)
                return;
            if (p.LinkParent(ib) != ia || p.LinkParent(ic) != ib)
                return;
            AsiKeyChain chain = new AsiKeyChain();
            chain.name = name;
            chain.bones = new[] { ia, ib, ic };
            chain.side = side;
            chain.auto = true;
            chain.poleHint = pole;
            InitHinge(chain);
            if (leg)
                FootPivots(p, chain, unit, side);
            else
                HandPivots(p, chain, unit, side);
            p.chains.Add(chain);
        }

        private static void FootPivots(AsiKeyProject p, AsiKeyChain chain, float unit, AsiBoneSide side)
        {
            AsiKeyBone foot = p.bones[chain.Effector];
            int toeIdx = p.BoneByHuman(side == AsiBoneSide.Left ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes);
            Vector3 ankle = foot.rigRestPos;
            Vector3 toe = toeIdx >= 0 ? p.bones[toeIdx].rigRestPos : ankle + Vector3.forward * unit * 0.6f;
            Vector3 fwd = toe - ankle;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
            float sole = ankle.y > 0.02f && toe.y > -0.05f ? 0f : Mathf.Min(toe.y, ankle.y);
            Vector3 heel = new Vector3(ankle.x, sole, ankle.z) - fwd * unit * 0.12f;
            Vector3 ball = new Vector3(toe.x, sole, toe.z);
            Vector3 tip = ball + fwd * unit * 0.35f;
            Quaternion inv = Quaternion.Inverse(foot.rigRestRot);
            chain.pivots = new[] { Vector3.zero, inv * (heel - ankle), inv * (ball - ankle), inv * (tip - ankle) };
            chain.pivotNames = new[] { "末端原点", "脚跟", "脚掌", "脚尖" };
        }

        private static void HandPivots(AsiKeyProject p, AsiKeyChain chain, float unit, AsiBoneSide side)
        {
            AsiKeyBone hand = p.bones[chain.Effector];
            bool left = side == AsiBoneSide.Left;
            int mp = p.BoneByHuman(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            int md = p.BoneByHuman(left ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal);
            Vector3 wrist = hand.rigRestPos;
            Vector3 knuckle = mp >= 0 ? p.bones[mp].rigRestPos : wrist + hand.rigRestRot * hand.axis * unit * 0.4f;
            Vector3 dir = (knuckle - wrist).sqrMagnitude > 1e-8f ? (knuckle - wrist).normalized : hand.rigRestRot * hand.axis;
            Vector3 tipPos = md >= 0 ? p.bones[md].rigRestPos + dir * p.bones[md].boneLength : knuckle + dir * unit * 0.4f;
            Vector3 palm = Vector3.Lerp(wrist, knuckle, 0.5f);
            Quaternion inv = Quaternion.Inverse(hand.rigRestRot);
            chain.pivots = new[] { Vector3.zero, inv * (palm - wrist), inv * (tipPos - wrist) };
            chain.pivotNames = new[] { "末端原点", "掌心", "指尖" };
        }

        // ---------------- 结构编辑 ----------------

        private static void ForEachTrack(AsiKeyProject p, Action<int, AsiKeyTrack> fn)
        {
            for (int l = 0; l < p.layers.Count; l++)
            {
                List<AsiKeyTrack> tracks = p.layers[l].tracks;
                for (int t = 0; t < tracks.Count; t++)
                    fn(l, tracks[t]);
            }
        }

        /// <summary>骨骼 j 的父级从 A 换成 B，B 相对 A 的静止变换为 (P, R)。把 j 的静止姿势与基础层绝对关键帧换算到新父级下。</summary>
        private static void ReparentKeys(AsiKeyProject p, int j, Vector3 P, Quaternion R)
        {
            Quaternion inv = Quaternion.Inverse(R);
            AsiKeyBone b = p.bones[j];
            b.restPos = inv * (b.restPos - P);
            b.restRot = AsiKeyMath.Norm(inv * b.restRot);
            b.origPos = inv * (b.origPos - P);
            b.origRot = AsiKeyMath.Norm(inv * b.origRot);
            if (p.layers.Count == 0)
                return;
            List<AsiKeyTrack> tracks = p.layers[0].tracks;
            for (int t = 0; t < tracks.Count; t++)
            {
                AsiKeyTrack tr = tracks[t];
                if (tr.kind == AsiTrackKind.Bone && tr.target == j)
                {
                    for (int k = 0; k < tr.keys.Count; k++)
                    {
                        AsiKey key = tr.keys[k];
                        AsiKeyEdit.EnsureArrays(p, tr, key);
                        key.rot[0] = AsiKeyMath.Norm(inv * key.rot[0]);
                        if (b.animatePosition)
                            key.pos = inv * (key.pos - P);
                    }
                    AsiKeyEdit.RebuildEulerFromRot(p, tr);
                    AsiKeyEdit.RecomputeTangents(p, tr);
                }
                else if (tr.kind == AsiTrackKind.Chain && tr.target >= 0 && tr.target < p.chains.Count && p.chains[tr.target].Root == j)
                {
                    for (int k = 0; k < tr.keys.Count; k++)
                    {
                        AsiKey key = tr.keys[k];
                        AsiKeyEdit.EnsureArrays(p, tr, key);
                        key.rot[0] = AsiKeyMath.Norm(inv * key.rot[0]);
                        if (key.ikSpace == AsiIKSpace.Body)
                        {
                            key.effPos = inv * (key.effPos - P);
                            key.effRot = AsiKeyMath.Norm(inv * key.effRot);
                        }
                    }
                    AsiKeyEdit.RebuildEulerFromRot(p, tr);
                    AsiKeyEdit.RecomputeTangents(p, tr);
                }
            }
        }

        public static string AddBone(AsiKeyProject p, Transform root, Animator animator, Transform t)
        {
            if (t == null || root == null)
                return "没有选择骨骼";
            if (t == root || !t.IsChildOf(root))
                return "只能添加角色内部的骨骼";
            string path = AnimationUtility.CalculateTransformPath(t, root);
            if (p.BoneByPath(path) >= 0)
                return "这根骨骼已经有控制器了";
            int unitIndex = p.CurrentUnit;

            int parent = -1;
            Transform a = t.parent;
            while (a != null && a != root)
            {
                parent = p.BoneByPath(AnimationUtility.CalculateTransformPath(a, root));
                if (parent >= 0)
                    break;
                a = a.parent;
            }

            List<int> children = new List<int>();
            List<int> anchoredChildren = new List<int>();
            for (int j = 0; j < p.bones.Count; j++)
            {
                AsiKeyBone bj = p.bones[j];
                if (bj.isCom || bj.unit != unitIndex)
                    continue;
                bool child = bj.parent == parent;
                bool anchoredChild = parent >= 0 && bj.anchored && bj.anchor == parent;
                if (!child && !anchoredChild)
                    continue;
                Transform tj = root.Find(bj.path);
                if (tj == null || tj == t || !tj.IsChildOf(t))
                    continue;
                if (child)
                    children.Add(j);
                else
                    anchoredChildren.Add(j);
            }
            int parentChain = parent >= 0 ? p.ChainOf(parent) : -1;
            for (int k = 0; k < children.Count + anchoredChildren.Count; k++)
            {
                int j = k < children.Count ? children[k] : anchoredChildren[k - children.Count];
                if (parentChain >= 0 && p.chains[parentChain].Contains(j))
                    return "这根骨骼位于 IK 链 \"" + p.chains[parentChain].name + "\" 中间，请先删除该链";
            }

            AsiKeyBone b = new AsiKeyBone();
            b.path = path;
            b.parent = parent;
            b.unit = unitIndex;
            int human = -1;
            if (animator != null && animator.isHuman)
            {
                for (int h = 0; h < (int)HumanBodyBones.LastBone; h++)
                {
                    if (animator.GetBoneTransform((HumanBodyBones)h) == t)
                    {
                        human = h;
                        break;
                    }
                }
            }
            b.humanBone = human >= 0 && p.BoneByHuman((HumanBodyBones)human) < 0 ? human : -1;
            string baseName = t.name;
            if (b.humanBone >= 0)
            {
                int e = AsiKeyBipedMap.IndexOf((HumanBodyBones)b.humanBone);
                if (e >= 0)
                    baseName = AsiKeyBipedMap.Order[e].name;
            }
            b.name = p.UniqueBoneName(baseName);
            b.animatePosition = parent < 0;
            RigSpace(root, t, out b.rigRestPos, out b.rigRestRot);
            SetLocalRest(p, b);
            int index = p.bones.Count;
            p.bones.Add(b);

            for (int k = 0; k < children.Count; k++)
            {
                int j = children[k];
                ReparentKeys(p, j, b.restPos, b.restRot);
                p.bones[j].parent = index;
            }
            for (int k = 0; k < anchoredChildren.Count; k++)
            {
                AsiKeyBone bj = p.bones[anchoredChildren[k]];
                bj.anchor = index;
                bj.anchorPos = Quaternion.Inverse(b.rigRestRot) * (bj.rigRestPos - b.rigRestPos);
            }

            float unit = UnitSize(p);
            ComputeAxis(p, index, root, unit);
            if (parent >= 0)
                ComputeAxis(p, parent, root, unit);
            b.shape = DefaultShape(p, index, unit);
            AsiKeyMirror.AutoPair(p);
            p.TouchStructure();
            return "";
        }

        public static string RemoveBone(AsiKeyProject p, int i, Transform root)
        {
            if (i < 0 || i >= p.bones.Count)
                return "无效骨骼";
            AsiKeyBone b = p.bones[i];
            if (b.isCom)
                return "质心不能删除";
            int chain = p.ChainOf(i);
            if (chain >= 0)
                return "这根骨骼属于 IK 链 \"" + p.chains[chain].name + "\"，请先删除该链";

            Quaternion invR = Quaternion.Inverse(b.restRot);
            Vector3 invP = -(invR * b.restPos);
            for (int j = 0; j < p.bones.Count; j++)
            {
                if (p.bones[j].parent != i)
                    continue;
                ReparentKeys(p, j, invP, invR);
                p.bones[j].parent = b.parent;
            }
            int nextAnchor = b.anchored ? b.anchor : b.parent;
            for (int j = 0; j < p.bones.Count; j++)
            {
                AsiKeyBone bj = p.bones[j];
                if (!bj.anchored || bj.anchor != i)
                    continue;
                if (nextAnchor < 0 || nextAnchor == bj.parent || nextAnchor == i)
                {
                    bj.anchored = false;
                    bj.anchor = -1;
                    continue;
                }
                AsiKeyBone na = p.bones[nextAnchor];
                bj.anchor = nextAnchor;
                bj.anchorPos = Quaternion.Inverse(na.rigRestRot) * (bj.rigRestPos - na.rigRestPos);
            }

            for (int l = 0; l < p.layers.Count; l++)
            {
                List<AsiKeyTrack> tracks = p.layers[l].tracks;
                for (int t = tracks.Count - 1; t >= 0; t--)
                {
                    if (tracks[t].kind == AsiTrackKind.Bone && tracks[t].target == i)
                        tracks.RemoveAt(t);
                }
            }
            ForEachTrack(p, (l, tr) =>
            {
                if (tr.kind != AsiTrackKind.Chain)
                    return;
                for (int k = 0; k < tr.keys.Count; k++)
                {
                    AsiKey key = tr.keys[k];
                    if (key.ikSpace == AsiIKSpace.Object && key.ikBone == i)
                    {
                        key.ikSpace = AsiIKSpace.World;
                        key.effPos = b.rigRestPos + b.rigRestRot * key.effPos;
                        key.effRot = AsiKeyMath.Norm(b.rigRestRot * key.effRot);
                        key.ikBone = -1;
                    }
                }
            });

            p.bones.RemoveAt(i);
            for (int j = 0; j < p.bones.Count; j++)
            {
                AsiKeyBone bj = p.bones[j];
                bj.parent = Remap(bj.parent, i);
                bj.mirror = bj.mirror == i ? -1 : Remap(bj.mirror, i);
                bj.anchor = Remap(bj.anchor, i);
                if (bj.anchor < 0)
                    bj.anchored = false;
            }
            for (int c = 0; c < p.chains.Count; c++)
            {
                int[] bs = p.chains[c].bones;
                for (int k = 0; k < bs.Length; k++)
                    bs[k] = Remap(bs[k], i);
            }
            ForEachTrack(p, (l, tr) =>
            {
                if (tr.kind != AsiTrackKind.Chain && tr.kind != AsiTrackKind.Constraint)
                    tr.target = Remap(tr.target, i);
                for (int k = 0; k < tr.keys.Count; k++)
                    tr.keys[k].ikBone = Remap(tr.keys[k].ikBone, i);
            });
            AsiKeyConstraints.RemapBones(p, x => Remap(x, i));
            if (root != null && b.parent >= 0)
                ComputeAxis(p, b.parent > i ? b.parent - 1 : b.parent, root, UnitSize(p));
            p.TouchStructure();
            return "";
        }

        private static int Remap(int index, int removed)
        {
            if (index < 0)
                return index;
            if (index == removed)
                return -1;
            return index > removed ? index - 1 : index;
        }

        /// <summary>链成员的关节必须依次相连（<see cref="AsiKeyProject.LinkParent"/>）。传入的骨骼顺序不限。</summary>
        public static string CreateChain(AsiKeyProject p, List<int> selection, string name)
        {
            if (selection.Count < 1)
                return "至少选择一根骨骼";
            List<int> list = new List<int>(selection);
            for (int k = list.Count - 1; k >= 0; k--)
            {
                int i = list[k];
                if (i >= 0 && i < p.bones.Count && p.bones[i].fingerTip)
                    list.RemoveAt(k);
            }
            if (list.Count < 1)
                return "指尖轴心不能加入 IK 链";
            list.Sort((x, y) => p.LinkDepth(x).CompareTo(p.LinkDepth(y)));
            for (int k = 0; k < list.Count; k++)
            {
                AsiKeyBone b = p.bones[list[k]];
                if (b.isCom)
                    return "质心不能加入 IK 链";
                if (b.animatePosition)
                    return b.name + " 带位移动画，不能加入 IK 链";
                int c = p.ChainOf(list[k]);
                if (c >= 0)
                    return b.name + " 已属于 IK 链 \"" + p.chains[c].name + "\"";
                if (k > 0 && p.LinkParent(list[k]) != list[k - 1])
                    return "所选骨骼不是一条连续的父子链（" + p.bones[list[k - 1]].name + " → " + b.name + "）";
            }
            if (list.Count > 64)
                return "链太长";

            AsiKeyChain chain = new AsiKeyChain();
            chain.bones = list.ToArray();
            chain.side = p.bones[chain.Effector].side;
            chain.name = string.IsNullOrEmpty(name) ? "链 " + p.bones[chain.Effector].name : name;
            chain.poleHint = GuessPole(p, chain);
            InitHinge(chain);
            int chainIndex = p.chains.Count;
            p.chains.Add(chain);

            for (int l = 0; l < p.layers.Count; l++)
            {
                List<AsiKeyTrack> tracks = p.layers[l].tracks;
                AsiKeyTrack[] member = new AsiKeyTrack[list.Count];
                bool any = false;
                AsiKeyTrack first = null;
                for (int m = 0; m < list.Count; m++)
                {
                    for (int t = 0; t < tracks.Count; t++)
                    {
                        if (tracks[t].kind == AsiTrackKind.Bone && tracks[t].target == list[m])
                        {
                            member[m] = tracks[t];
                            if (tracks[t].keys.Count > 0)
                            {
                                any = true;
                                if (first == null)
                                    first = tracks[t];
                            }
                        }
                    }
                }
                if (!any)
                {
                    for (int m = 0; m < list.Count; m++)
                    {
                        if (member[m] != null)
                            tracks.Remove(member[m]);
                    }
                    continue;
                }
                List<float> frames = new List<float>();
                for (int m = 0; m < list.Count; m++)
                {
                    if (member[m] == null)
                        continue;
                    for (int k = 0; k < member[m].keys.Count; k++)
                    {
                        float f = member[m].keys[k].frame;
                        bool found = false;
                        for (int q = 0; q < frames.Count; q++)
                        {
                            if (Mathf.Abs(frames[q] - f) < 0.01f)
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                            frames.Add(f);
                    }
                }
                frames.Sort();
                AsiKeyTrack ct = new AsiKeyTrack();
                ct.kind = AsiTrackKind.Chain;
                ct.target = chainIndex;
                ct.mode = first.mode;
                ct.rotOrder = first.rotOrder;
                for (int fi = 0; fi < frames.Count; fi++)
                {
                    AsiKey key = new AsiKey();
                    key.frame = frames[fi];
                    key.type = AsiKeyType.Free;
                    AsiKeyEdit.EnsureArrays(p, ct, key);
                    for (int m = 0; m < list.Count; m++)
                    {
                        Quaternion fallback = l == 0 ? p.bones[list[m]].restRot : Quaternion.identity;
                        key.rot[m] = member[m] != null ? AsiKeyMath.Norm(AsiKeyTrackEval.EvalRot(member[m], 0, key.frame, fallback)) : fallback;
                        if (member[m] != null)
                        {
                            int ki = AsiKeyTrackEval.FindKeyIndex(member[m].keys, key.frame);
                            if (ki >= 0)
                            {
                                AsiKey src = member[m].keys[ki];
                                key.tension = src.tension;
                                key.continuity = src.continuity;
                                key.bias = src.bias;
                                key.easeTo = src.easeTo;
                                key.easeFrom = src.easeFrom;
                            }
                        }
                    }
                    ct.keys.Add(key);
                }
                for (int m = 0; m < list.Count; m++)
                {
                    if (member[m] != null)
                        tracks.Remove(member[m]);
                }
                tracks.Add(ct);
                AsiKeyEdit.RebuildEulerFromRot(p, ct);
                AsiKeyEdit.RecomputeTangents(p, ct);
            }
            p.TouchStructure();
            return "";
        }

        private static void InitHinge(AsiKeyChain chain)
        {
            chain.hinge = chain.bones.Length == 3;
            chain.bendMin = 0f;
            chain.bendMax = 160f;
        }

        /// <summary>把基础层该链关键帧里的中间关节旋转投影到铰链上（去掉扭转与侧弯、夹到弯曲范围），返回改动的帧数。</summary>
        public static int FixHingeKeys(AsiKeyProject p, int c)
        {
            if (c < 0 || c >= p.chains.Count || p.layers.Count == 0)
                return 0;
            AsiKeyChain chain = p.chains[c];
            if (!AsiKeyHinge.Active(p, chain))
                return 0;
            AsiKeyTrack tr = p.FindTrack(0, AsiTrackKind.Chain, c);
            if (tr == null)
                return 0;
            int count = 0;
            for (int k = 0; k < tr.keys.Count; k++)
            {
                AsiKey key = tr.keys[k];
                AsiKeyEdit.EnsureArrays(p, tr, key);
                Quaternion q = key.rot[1];
                Quaternion fixedRot = AsiKeyHinge.Project(p, chain, q);
                if (Quaternion.Angle(q, fixedRot) < 0.01f)
                    continue;
                key.rot[1] = fixedRot;
                count++;
            }
            if (count > 0)
            {
                AsiKeyEdit.RebuildEulerFromRot(p, tr);
                AsiKeyEdit.RecomputeTangents(p, tr);
                p.Touch();
            }
            return count;
        }

        public static Vector3 GuessPole(AsiKeyProject p, AsiKeyChain chain)
        {
            if (chain.bones.Length >= 3)
            {
                Vector3 a = p.bones[chain.Root].rigRestPos;
                Vector3 c = p.bones[chain.Effector].rigRestPos;
                Vector3 dir = c - a;
                if (dir.sqrMagnitude > 1e-8f)
                {
                    dir.Normalize();
                    Vector3 sum = Vector3.zero;
                    for (int k = 1; k < chain.bones.Length - 1; k++)
                    {
                        Vector3 off = p.bones[chain.bones[k]].rigRestPos - a;
                        sum += off - Vector3.Dot(off, dir) * dir;
                    }
                    if (sum.sqrMagnitude > 1e-8f)
                        return sum.normalized;
                }
            }
            return Vector3.forward;
        }

        /// <summary>删链：链轨道拆回各成员的骨骼轨道，保留成员旋转，丢弃 IK 参数。</summary>
        public static void DeleteChain(AsiKeyProject p, int c)
        {
            if (c < 0 || c >= p.chains.Count)
                return;
            AsiKeyChain chain = p.chains[c];
            for (int l = 0; l < p.layers.Count; l++)
            {
                List<AsiKeyTrack> tracks = p.layers[l].tracks;
                for (int t = tracks.Count - 1; t >= 0; t--)
                {
                    AsiKeyTrack ct = tracks[t];
                    if (ct.kind != AsiTrackKind.Chain || ct.target != c)
                        continue;
                    tracks.RemoveAt(t);
                    if (ct.keys.Count == 0)
                        continue;
                    for (int m = 0; m < chain.bones.Length; m++)
                    {
                        AsiKeyTrack bt = new AsiKeyTrack();
                        bt.kind = AsiTrackKind.Bone;
                        bt.target = chain.bones[m];
                        bt.mode = ct.mode;
                        bt.rotOrder = ct.rotOrder;
                        for (int k = 0; k < ct.keys.Count; k++)
                        {
                            AsiKey src = ct.keys[k];
                            AsiKey key = new AsiKey();
                            key.frame = src.frame;
                            key.type = AsiKeyType.Set;
                            key.tension = src.tension;
                            key.continuity = src.continuity;
                            key.bias = src.bias;
                            key.easeTo = src.easeTo;
                            key.easeFrom = src.easeFrom;
                            key.rot = new[] { m < src.rot.Length ? src.rot[m] : Quaternion.identity };
                            key.euler = new[] { m < src.euler.Length ? src.euler[m] : Vector3.zero };
                            key.tan = new AsiTangent[6];
                            for (int a = 0; a < 3; a++)
                            {
                                int si = m * 3 + a;
                                key.tan[a] = src.tan != null && si < src.tan.Length ? src.tan[si] : AsiTangent.Default(AsiTangentType.Auto);
                            }
                            for (int a = 3; a < 6; a++)
                                key.tan[a] = AsiTangent.Default(AsiTangentType.Auto);
                            bt.keys.Add(key);
                        }
                        tracks.Add(bt);
                        AsiKeyEdit.RecomputeTangents(p, bt);
                    }
                }
            }
            p.chains.RemoveAt(c);
            ForEachTrack(p, (l, tr) =>
            {
                if (tr.kind == AsiTrackKind.Chain && tr.target > c)
                    tr.target--;
            });
            p.TouchStructure();
        }

        /// <summary>Biped 结构：腰椎的旋转父级是质心，关节位置挂在盆骨上，转盆骨不带动上身。生成骨骼时调用（还没有关键帧）。</summary>
        private static void DetachSpineFromPelvis(AsiKeyProject p)
        {
            if (!p.humanoid)
                return;
            int com = p.ComIndex;
            int hips = p.BoneByHuman(HumanBodyBones.Hips);
            int spine = p.BoneByHuman(HumanBodyBones.Spine);
            if (com < 0 || hips < 0 || spine < 0)
                return;
            if (p.bones[spine].parent != hips || p.bones[hips].parent != com)
                return;
            RotateWithCom(p, spine, com);
        }

        /// <summary>骨骼 i 的旋转父级改为质心，关节位置仍挂在原父骨骼上。</summary>
        private static void RotateWithCom(AsiKeyProject p, int i, int com)
        {
            AsiKeyBone b = p.bones[i];
            int old = b.parent;
            AsiKeyBone ob = p.bones[old];
            b.parent = com;
            SetLocalRest(p, b);
            b.anchored = true;
            b.anchor = old;
            b.anchorPos = Quaternion.Inverse(ob.rigRestRot) * (b.rigRestPos - ob.rigRestPos);
        }

        private static readonly HumanBodyBones[] IndependentBones =
        {
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
            HumanBodyBones.Head,
        };

        /// <summary>
        /// Biped 独立朝向：大腿、脚、上臂、头的旋转父级是质心，关节位置挂在原父骨骼上，
        /// 转盆骨、小腿、锁骨、脖子只带动它们的位置，不改朝向。生成骨骼时调用（还没有关键帧）。
        /// </summary>
        private static void DetachBipedOrientation(AsiKeyProject p)
        {
            if (!p.humanoid)
                return;
            int com = p.ComIndex;
            if (com < 0)
                return;
            for (int h = 0; h < IndependentBones.Length; h++)
            {
                int i = p.BoneByHuman(IndependentBones[h]);
                if (i < 0)
                    continue;
                AsiKeyBone b = p.bones[i];
                if (b.parent < 0 || b.parent == com || b.anchored || b.animatePosition)
                    continue;
                RotateWithCom(p, i, com);
            }
        }

        /// <summary>移动单位 u 的质心在 Hips 与 Spine 之间的位置，补偿质心位移关键帧，保持盆骨世界位置不变。</summary>
        public static void SetComRatio(AsiKeyProject p, int u, float ratio)
        {
            ratio = Mathf.Clamp01(ratio);
            int com = p.ComOf(u);
            int hips = p.BoneByHuman(HumanBodyBones.Hips, u);
            int spine = p.BoneByHuman(HumanBodyBones.Spine, u);
            if (p.ValidUnit(u))
                p.units[u].comRatio = ratio;
            if (com < 0 || hips < 0 || spine < 0)
                return;
            AsiKeyBone c = p.bones[com];
            Vector3 oldCom = c.rigRestPos;
            Vector3 newCom = Vector3.Lerp(p.bones[hips].rigRestPos, p.bones[spine].rigRestPos, ratio);
            Vector3 shift = oldCom - newCom;
            c.rigRestPos = newCom;
            c.restPos = newCom;
            for (int j = 0; j < p.bones.Count; j++)
            {
                if (p.bones[j].parent == com)
                    p.bones[j].restPos += Quaternion.Inverse(c.restRot) * shift;
            }
            if (p.layers.Count > 0)
            {
                AsiKeyTrack rotTrack = p.FindTrack(0, AsiTrackKind.ComRotation, com);
                List<AsiKeyTrack> tracks = p.layers[0].tracks;
                for (int t = 0; t < tracks.Count; t++)
                {
                    AsiKeyTrack tr = tracks[t];
                    if ((tr.kind != AsiTrackKind.ComHorizontal && tr.kind != AsiTrackKind.ComVertical)
                        || AsiKeyEvaluator.ComTarget(p, tr) != com)
                        continue;
                    for (int k = 0; k < tr.keys.Count; k++)
                    {
                        AsiKey key = tr.keys[k];
                        Quaternion r = rotTrack != null && rotTrack.keys.Count > 0
                            ? AsiKeyMath.Norm(AsiKeyTrackEval.EvalRot(rotTrack, 0, key.frame, c.restRot))
                            : c.restRot;
                        Vector3 delta = r * (Quaternion.Inverse(c.restRot) * shift);
                        if (tr.kind == AsiTrackKind.ComHorizontal)
                        {
                            key.pos.x -= delta.x;
                            key.pos.z -= delta.z;
                        }
                        else
                        {
                            key.pos.y -= delta.y;
                        }
                    }
                    AsiKeyEdit.RecomputeTangents(p, tr);
                }
            }
            p.TouchStructure();
        }

        public static string SetAnimatePosition(AsiKeyProject p, int i, bool on)
        {
            AsiKeyBone b = p.bones[i];
            if (b.isCom || b.animatePosition == on)
                return "";
            if (on && p.ChainOf(i) >= 0)
                return "IK 链成员不能记录位移";
            b.animatePosition = on;
            for (int l = 0; l < p.layers.Count; l++)
            {
                AsiKeyTrack tr = p.FindTrack(l, AsiTrackKind.Bone, i);
                if (tr == null)
                    continue;
                for (int k = 0; k < tr.keys.Count; k++)
                    tr.keys[k].pos = l == 0 ? b.restPos : Vector3.zero;
                for (int k = 0; k < tr.keys.Count; k++)
                    AsiKeyEdit.EnsureArrays(p, tr, tr.keys[k]);
                AsiKeyEdit.RecomputeTangents(p, tr);
            }
            p.TouchStructure();
            return "";
        }

        // ---------------- 场景控制器 ----------------

        public static string RigRootName(AsiKeyProject p)
        {
            string n = !string.IsNullOrEmpty(p.rootName) ? p.rootName : p.name;
            return "[AsiKeyRig] " + n;
        }
    }
}
