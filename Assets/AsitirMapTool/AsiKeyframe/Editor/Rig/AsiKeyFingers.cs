using System.Collections.Generic;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>一只手上的一根手指。关节下标为 -1 表示模型上没有这节。不新增骨骼，只用人形映射里已有的控制器。</summary>
    public struct AsiFingerRef
    {
        public int unit;
        public int side;
        public int id;
        public int hand;
        public int proximal;
        public int middle;
        public int distal;
    }

    /// <summary>绑定姿势下的掌心坐标系，单位空间。palm 朝掌心，towardThumb 在掌面里指向拇指一侧。</summary>
    public struct AsiHandAxes
    {
        public bool ok;
        public Vector3 palm;
        public Vector3 towardThumb;
    }

    /// <summary>
    /// 手指张开、卷曲、错落，以及可选的每指 IK。
    /// 拖手掌上的手柄直接改各指节的旋转（关键帧），不是约束。
    /// 卷曲绕每节自己的原局部轴；食指到小指的张开绕「指尖沿小指→食指移动」的那根原轴。
    /// 每根有末节的手指在生成时多一根不显示的指尖轴心，单独选中它时整条 IK 链绕它转。
    /// </summary>
    public static class AsiKeyFingers
    {
        public const int Count = 5;

        public static readonly string[] Names = { "拇指", "食指", "中指", "无名指", "小指" };

        /// <summary>相对中指散开的权重。正值朝拇指一侧，负值朝小指一侧，中指为 0。</summary>
        private static readonly float[] Fan = { 1.15f, 0.55f, 0f, -0.55f, -1.15f };

        private static readonly HumanBodyBones[] JointMap =
        {
            HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
            HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
            HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
            HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
            HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,

            HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
            HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
            HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
            HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
            HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
        };

        private static readonly List<Vector3> s_pts = new List<Vector3>();
        private static readonly int[] s_joints = new int[3];

        public static string Label(int side, int id)
        {
            return (side == 0 ? "左" : "右") + Names[id];
        }

        public static float FanWeight(int id)
        {
            return id >= 0 && id < Fan.Length ? Fan[id] : 0f;
        }

        public static bool Any(AsiKeyProject p)
        {
            if (p == null)
                return false;
            for (int u = 0; u < p.units.Count; u++)
            {
                if (!p.UnitIsHumanoid(u))
                    continue;
                if (HandHasFinger(p, u, 0) || HandHasFinger(p, u, 1))
                    return true;
            }
            return false;
        }

        public static bool HandHasFinger(AsiKeyProject p, int unit, int side)
        {
            for (int id = 0; id < Count; id++)
            {
                AsiFingerRef f;
                if (TryFinger(p, unit, side, id, out f))
                    return true;
            }
            return false;
        }

        public static bool TryFinger(AsiKeyProject p, int unit, int side, int id, out AsiFingerRef f)
        {
            f = new AsiFingerRef();
            f.unit = unit;
            f.side = side;
            f.id = id;
            f.hand = p.BoneByHuman(side == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand, unit);
            f.proximal = JointBone(p, unit, side, id, 0);
            f.middle = JointBone(p, unit, side, id, 1);
            f.distal = JointBone(p, unit, side, id, 2);
            return f.proximal >= 0 || f.middle >= 0 || f.distal >= 0;
        }

        public static int JointAt(AsiFingerRef f, int index)
        {
            return index == 0 ? f.proximal : index == 1 ? f.middle : f.distal;
        }

        /// <summary>从近节起连续相连的关节，写进内部缓冲，返回节数。</summary>
        public static int ChainJoints(AsiKeyProject p, AsiFingerRef f)
        {
            int n = 0;
            int prev = -1;
            for (int i = 0; i < 3; i++)
            {
                int j = JointAt(f, i);
                if (j < 0)
                    continue;
                if (n > 0 && p.LinkParent(j) != prev)
                    break;
                s_joints[n++] = j;
                prev = j;
            }
            return n;
        }

        public static int ChainJointAt(int index)
        {
            return s_joints[index];
        }

        /// <summary>选中包含这只手，或选中了它的某一节时，收集这只手上要一起调的手指。</summary>
        public static void Collect(AsiKeyProject p, List<int> selected, int unit, int side, List<AsiFingerRef> dst)
        {
            dst.Clear();
            int hand = p.BoneByHuman(side == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand, unit);
            bool whole = selected != null && hand >= 0 && selected.Contains(hand);
            for (int id = 0; id < Count; id++)
            {
                AsiFingerRef f;
                if (!TryFinger(p, unit, side, id, out f))
                    continue;
                if (whole || Hits(selected, f))
                    dst.Add(f);
            }
        }

        /// <summary>掌心朝向。点几乎共线时仍给出一个朝下或朝身体的法线。</summary>
        public static bool TryAxes(AsiKeyProject p, int unit, int side, out AsiHandAxes axes)
        {
            axes = new AsiHandAxes();
            s_pts.Clear();
            int hand = p.BoneByHuman(side == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand, unit);
            if (hand >= 0)
                s_pts.Add(p.bones[hand].rigRestPos);
            bool any = false;
            Vector3 forward = Vector3.zero;
            for (int id = 0; id < Count; id++)
            {
                AsiFingerRef f;
                if (!TryFinger(p, unit, side, id, out f))
                    continue;
                any = true;
                for (int k = 0; k < 3; k++)
                {
                    int j = JointAt(f, k);
                    if (j >= 0)
                        s_pts.Add(p.bones[j].rigRestPos);
                }
                int root = SpreadJoint(f);
                if (root >= 0)
                    forward += BoneDir(p.bones[root]);
            }
            if (!any)
                return false;
            if (forward.sqrMagnitude < 1e-8f)
                forward = Vector3.forward;
            forward.Normalize();

            Vector3 n = Vector3.zero;
            if (s_pts.Count >= 3)
            {
                Vector3 c = Vector3.zero;
                for (int i = 0; i < s_pts.Count; i++)
                    c += s_pts[i];
                c /= s_pts.Count;
                for (int i = 0; i < s_pts.Count; i++)
                {
                    Vector3 a = s_pts[i] - c;
                    Vector3 b = s_pts[(i + 1) % s_pts.Count] - c;
                    n += Vector3.Cross(a, b);
                }
            }
            if (n.sqrMagnitude < 1e-10f)
            {
                n = Vector3.Cross(forward, Vector3.up);
                if (n.sqrMagnitude < 1e-8f)
                    n = Vector3.Cross(forward, Vector3.right);
            }
            n.Normalize();

            int hips = p.BoneByHuman(HumanBodyBones.Hips, unit);
            Vector3 origin = hips >= 0 ? p.bones[hips].rigRestPos : Vector3.zero;
            Vector3 at = s_pts.Count > 0 ? s_pts[0] : Vector3.zero;
            Vector3 toBody = origin - at;
            if (toBody.sqrMagnitude > 1e-8f)
                toBody.Normalize();
            else
                toBody = Vector3.zero;
            Vector3 score = Vector3.down + toBody * 0.65f;
            if (score.sqrMagnitude < 1e-8f)
                score = Vector3.down;
            if (Vector3.Dot(n, score) < 0f)
                n = -n;

            Vector3 across = Across(p, unit, side, n, forward);
            axes.ok = true;
            axes.palm = n;
            axes.towardThumb = across;
            return true;
        }

        public static int SpreadJoint(AsiFingerRef f)
        {
            if (f.proximal >= 0)
                return f.proximal;
            if (f.middle >= 0)
                return f.middle;
            return f.distal;
        }

        /// <summary>食指 -1，中指 -1/3，无名指 +1/3，小指 +1。拇指不参与错落。</summary>
        public static float StaggerWeight(int id)
        {
            if (id <= 0 || id >= Count)
                return 0f;
            return (id - 1) / 1.5f - 1f;
        }

        /// <summary>
        /// 这节骨骼在模型里的原局部轴（±X/±Y/±Z，不是掌心估算，也不是 Biped 显示标架）。
        /// 沿骨的那根是骨骼方向；卷曲是下一根原轴；张开是剩下的那根，符号使 along、curl、spread 成右手系。
        /// </summary>
        public static void OriginalAxes(AsiKeyBone b, out Vector3 along, out Vector3 curl, out Vector3 spread)
        {
            Vector3 dir = b.axis.sqrMagnitude > 1e-10f ? b.axis : Vector3.forward;
            int alongI = 0;
            float best = -1f;
            float sign = 1f;
            for (int i = 0; i < 3; i++)
            {
                float d = Vector3.Dot(dir, Unit(i));
                if (Mathf.Abs(d) <= best)
                    continue;
                best = Mathf.Abs(d);
                alongI = i;
                sign = d >= 0f ? 1f : -1f;
            }
            along = Unit(alongI) * sign;
            curl = Unit((alongI + 2) % 3);
            spread = Snap(Vector3.Cross(along, curl));
        }

        /// <summary>
        /// 把卷曲轴的符号翻到「正角度弯向掌心」。掌心只用来定符号，轴本身仍是这节的原局部轴。
        /// closeDir 在静止姿势的单位空间里，指向掌心。
        /// </summary>
        public static Vector3 OrientedCurl(AsiKeyBone b, Vector3 closeDir)
        {
            Vector3 along;
            Vector3 curl;
            Vector3 spread;
            OriginalAxes(b, out along, out curl, out spread);
            if (closeDir.sqrMagnitude < 1e-8f)
                return curl;
            Vector3 motion = b.rigRestRot * Vector3.Cross(curl, along);
            if (Vector3.Dot(motion, closeDir) < 0f)
                curl = -curl;
            return curl;
        }

        /// <summary>这只手静止姿势下指向掌心的方向（单位空间）。由指尖方向和拇指相对中指的位置叉乘得到。</summary>
        public static bool CloseDirection(AsiKeyProject p, int unit, int side, out Vector3 close)
        {
            close = Vector3.zero;
            Vector3 fwd = Vector3.zero;
            int n = 0;
            for (int id = 1; id < Count; id++)
            {
                int j = JointBone(p, unit, side, id, 0);
                if (j < 0)
                    continue;
                AsiKeyBone b = p.bones[j];
                Vector3 d = b.rigRestRot * (b.axis.sqrMagnitude > 1e-10f ? b.axis : Vector3.forward);
                if (d.sqrMagnitude < 1e-10f)
                    continue;
                fwd += d.normalized;
                n++;
            }
            int thumb = JointBone(p, unit, side, 0, 0);
            int middle = JointBone(p, unit, side, 2, 0);
            if (middle < 0)
                middle = JointBone(p, unit, side, 1, 0);
            if (n == 0 || thumb < 0 || middle < 0)
                return false;
            Vector3 sideDir = p.bones[thumb].rigRestPos - p.bones[middle].rigRestPos;
            if (fwd.sqrMagnitude < 1e-8f || sideDir.sqrMagnitude < 1e-8f)
                return false;
            close = Vector3.Cross(sideDir.normalized, fwd.normalized);
            return close.sqrMagnitude > 1e-8f;
        }

        /// <summary>
        /// 张开轴，在这节自己的原局部坐标里。
        /// 食指到小指：绕哪根轴转，指尖就沿着小指→食指的方向动。拇指仍用自己的原局部张开轴，正值离开中指。
        /// </summary>
        public static Vector3 SpreadAxis(AsiKeyProject p, int unit, int side, AsiFingerRef f, int bone)
        {
            AsiKeyBone b = p.bones[bone];
            Vector3 along;
            Vector3 curl;
            Vector3 spread;
            OriginalAxes(b, out along, out curl, out spread);
            if (f.id == 0)
            {
                int middle = JointBone(p, unit, side, 2, 0);
                if (middle < 0)
                    middle = JointBone(p, unit, side, 1, 0);
                int thumb = JointBone(p, unit, side, 0, 0);
                if (middle >= 0 && thumb >= 0)
                {
                    Vector3 sideRef = p.bones[thumb].rigRestPos - p.bones[middle].rigRestPos;
                    Vector3 motion = b.rigRestRot * Vector3.Cross(spread, along);
                    if (Vector3.Dot(motion, sideRef) < 0f)
                        spread = -spread;
                }
                return spread;
            }
            int little = JointBone(p, unit, side, 4, 0);
            int index = JointBone(p, unit, side, 1, 0);
            if (little < 0 || index < 0)
                return spread;
            Vector3 across = p.bones[index].rigRestPos - p.bones[little].rigRestPos;
            Vector3 want = Quaternion.Inverse(b.rigRestRot) * across;
            Vector3 hint = Vector3.Cross(along, want);
            if (hint.sqrMagnitude < 1e-8f)
                return spread;
            Vector3 snapped = Snap(hint);
            if (Mathf.Abs(Vector3.Dot(snapped, along)) > 0.9f)
                return spread;
            return snapped;
        }

        /// <summary>bone 是带手指的左手或右手时返回 true。</summary>
        public static bool TryHand(AsiKeyProject p, int bone, out int unit, out int side)
        {
            unit = 0;
            side = 0;
            if (p == null || bone < 0 || bone >= p.bones.Count)
                return false;
            int hb = p.bones[bone].humanBone;
            if (hb == (int)HumanBodyBones.LeftHand)
                side = 0;
            else if (hb == (int)HumanBodyBones.RightHand)
                side = 1;
            else
                return false;
            unit = p.bones[bone].unit;
            return HandHasFinger(p, unit, side);
        }

        /// <summary>这只手上、不在 IK 链里的全部指节。</summary>
        public static void CollectJoints(AsiKeyProject p, int hand, List<int> dst)
        {
            dst.Clear();
            int unit;
            int side;
            if (!TryHand(p, hand, out unit, out side))
                return;
            for (int id = 0; id < Count; id++)
            {
                AsiFingerRef f;
                if (!TryFinger(p, unit, side, id, out f))
                    continue;
                if (FindChain(p, f) >= 0)
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    int j = JointAt(f, k);
                    if (j >= 0)
                        dst.Add(j);
                }
            }
        }

        /// <summary>绕这节自己的原局部轴转过的增量。右乘到当前局部旋转上，就是直接改这节的姿势。</summary>
        public static Quaternion LocalDelta(AsiKeyProject p, int unit, int side, AsiFingerRef f, int bone, float curlDeg, float spreadDeg, float staggerDeg)
        {
            float curl = curlDeg;
            if (f.id != 0)
                curl += staggerDeg * StaggerWeight(f.id);
            float spread = bone == SpreadJoint(f) ? spreadDeg * FanWeight(f.id) : 0f;
            AsiKeyBone b = p.bones[bone];
            Vector3 curlAxis;
            OriginalAxes(b, out _, out curlAxis, out _);
            Vector3 closeDir;
            if (CloseDirection(p, unit, side, out closeDir))
                curlAxis = OrientedCurl(b, closeDir);
            Vector3 spreadAxis = SpreadAxis(p, unit, side, f, bone);
            return Quaternion.AngleAxis(spread, spreadAxis) * Quaternion.AngleAxis(curl, curlAxis);
        }

        /// <summary>把曲张增量写进各指节当前的 fk。正在 IK 的手指跳过。改过的骨骼下标写进 touched。</summary>
        public static void OffsetPose(AsiKeyProject p, AsiKeyPose pose, int hand, float curlDeg, float spreadDeg, float staggerDeg, List<int> touched)
        {
            if (touched != null)
                touched.Clear();
            int unit;
            int side;
            if (pose == null || pose.fk == null || !TryHand(p, hand, out unit, out side))
                return;
            for (int id = 0; id < Count; id++)
            {
                AsiFingerRef f;
                if (!TryFinger(p, unit, side, id, out f))
                    continue;
                if (DrivenByIk(p, pose, f))
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    int j = JointAt(f, k);
                    if (j < 0 || j >= pose.fk.Length)
                        continue;
                    Quaternion delta = LocalDelta(p, unit, side, f, j, curlDeg, spreadDeg, staggerDeg);
                    if (Quaternion.Angle(delta, Quaternion.identity) < 1e-3f)
                        continue;
                    pose.fk[j] = AsiKeyMath.Norm(pose.fk[j] * delta);
                    if (touched != null && !touched.Contains(j))
                        touched.Add(j);
                }
            }
        }

        public static bool DrivenByIk(AsiKeyProject p, AsiKeyPose pose, AsiFingerRef f)
        {
            if (pose == null || pose.chains == null)
                return false;
            for (int i = 0; i < 3; i++)
            {
                int j = JointAt(f, i);
                if (j < 0)
                    continue;
                int c = p.ChainOf(j);
                if (c < 0 || c >= pose.chainCount)
                    continue;
                AsiChainState s = pose.chains[c];
                if (s.valid && s.blend >= 0.5f)
                    return true;
            }
            return false;
        }

        /// <summary>这几节正好组成的那条链。多出来的骨头（例如整条手臂）不算。</summary>
        public static int FindChain(AsiKeyProject p, AsiFingerRef f)
        {
            int n = ChainJoints(p, f);
            if (n == 0)
                return -1;
            int c = -1;
            for (int i = 0; i < n; i++)
            {
                int ci = p.ChainOf(s_joints[i]);
                if (ci < 0)
                    return -1;
                if (c < 0)
                    c = ci;
                else if (c != ci)
                    return -1;
            }
            AsiKeyChain ch = p.chains[c];
            if (ch.bones.Length != n)
                return -1;
            for (int i = 0; i < n; i++)
            {
                if (!ch.Contains(s_joints[i]))
                    return -1;
            }
            return c;
        }

        /// <summary>手指链关掉膝/肘铰链锁。已经关着的不动。有改动时返回 true。</summary>
        public static bool ReleaseHinge(AsiKeyProject p)
        {
            if (p == null)
                return false;
            bool changed = false;
            for (int i = 0; i < p.chains.Count; i++)
            {
                if (!p.chains[i].hinge || !IsFingerChain(p, i))
                    continue;
                p.chains[i].hinge = false;
                changed = true;
            }
            if (changed)
                p.TouchStructure();
            return changed;
        }

        /// <summary>这条链的成员全是手指骨。</summary>
        public static bool IsFingerChain(AsiKeyProject p, int chain)
        {
            if (p == null || chain < 0 || chain >= p.chains.Count)
                return false;
            return IsFingerChain(p, p.chains[chain]);
        }

        /// <summary>这条链的成员全是手指骨。</summary>
        public static bool IsFingerChain(AsiKeyProject p, AsiKeyChain c)
        {
            if (p == null || c == null || c.bones == null || c.bones.Length == 0)
                return false;
            for (int i = 0; i < c.bones.Length; i++)
            {
                int b = c.bones[i];
                if (b < 0 || b >= p.bones.Count)
                    return false;
                if (AsiKeyBipedMap.GroupOf(p.bones[b].humanBone) != AsiKeyBipedMap.Group.Finger)
                    return false;
            }
            return true;
        }

        /// <summary>这节手指的原卷曲轴，在旋转父级的局部空间（绑定姿势）里。</summary>
        public static Vector3 CurlAxisInParent(AsiKeyProject p, int bone)
        {
            AsiKeyBone b = p.bones[bone];
            Vector3 along, curl, spread;
            OriginalAxes(b, out along, out curl, out spread);
            return (b.restRot * curl).normalized;
        }

        /// <summary>
        /// 只保留这节手指相对绑定姿势绕自己原卷曲轴的转动，去掉侧摆与扭转。
        /// local 是相对旋转父级的局部旋转；IK 下非第一节的手指关节都走这里。
        /// </summary>
        public static Quaternion CurlOnly(AsiKeyProject p, int bone, Quaternion local)
        {
            AsiKeyBone b = p.bones[bone];
            Vector3 along, curl, spread;
            OriginalAxes(b, out along, out curl, out spread);
            // 资产里存的 restRot 不是严格单位长度，直接求逆会残留约 0.04° 侧摆
            Quaternion rest = AsiKeyMath.Norm(b.restRot);
            float angle = AsiKeyHinge.TwistAngle(AsiKeyMath.Norm(Quaternion.Inverse(rest) * local), curl);
            return AsiKeyMath.Norm(rest * Quaternion.AngleAxis(angle, curl));
        }

        /// <summary>给人形单位里每根有两节以上的手指建 IK 链。已经有链的跳过，少于两节的跳过。</summary>
        public static void EnsureChains(AsiKeyProject p)
        {
            if (p == null)
                return;
            for (int u = 0; u < p.units.Count; u++)
            {
                if (!p.UnitIsHumanoid(u))
                    continue;
                for (int side = 0; side < 2; side++)
                {
                    for (int id = 0; id < Count; id++)
                    {
                        AsiFingerRef f;
                        if (!TryFinger(p, u, side, id, out f))
                            continue;
                        int chain;
                        SetChain(p, f, true, out chain);
                    }
                }
            }
        }

        /// <summary>打开或关掉这根手指的 IK 链。成功时 chain 是链下标；关掉且本来没有时 chain 为 -1。</summary>
        public static string SetChain(AsiKeyProject p, AsiFingerRef f, bool on, out int chain)
        {
            chain = -1;
            int existing = FindChain(p, f);
            if (!on)
            {
                if (existing < 0)
                    return "";
                chain = existing;
                AsiKeyRigBuilder.DeleteChain(p, existing);
                return "";
            }
            if (existing >= 0)
            {
                chain = existing;
                if (p.chains[existing].hinge)
                {
                    p.chains[existing].hinge = false;
                    p.TouchStructure();
                }
                return "";
            }
            int n = ChainJoints(p, f);
            if (n < 2)
                return Label(f.side, f.id) + "没有两节以上连在一起的关节，不能做 IK";
            List<int> bones = new List<int>(n);
            for (int i = 0; i < n; i++)
                bones.Add(s_joints[i]);
            string err = AsiKeyRigBuilder.CreateChain(p, bones, Label(f.side, f.id));
            if (!string.IsNullOrEmpty(err))
                return err;
            chain = p.chains.Count - 1;
            p.chains[chain].hinge = false;
            AsiHandAxes axes;
            Vector3 pole;
            if (TryAxes(p, f.unit, f.side, out axes) && TryCurlPole(p, p.chains[chain], axes.palm, out pole))
                p.chains[chain].poleHint = pole;
            p.TouchStructure();
            return "";
        }

        /// <summary>
        /// FK 标架用的卷曲轴（单位空间）：这节的原局部卷曲轴，符号使绕它正转时指尖朝手背走。
        /// 五指同一约定，拇指不再按角色右方向拼标架。掌心取 <see cref="TryAxes"/>，左右手符号都可靠。
        /// </summary>
        public static bool TryFrameCurl(AsiKeyProject p, int bone, out Vector3 curlAxis)
        {
            curlAxis = Vector3.zero;
            int unit;
            int side;
            AsiHandAxes axes;
            if (!FingerSide(p, bone, out unit, out side) || !TryAxes(p, unit, side, out axes))
                return false;
            AsiKeyBone b = p.bones[bone];
            Vector3 along;
            Vector3 curl;
            OriginalAxes(b, out along, out curl, out _);
            Vector3 c = b.rigRestRot * curl;
            if (Vector3.Dot(b.rigRestRot * Vector3.Cross(curl, along), axes.palm) > 0f)
                c = -c;
            if (c.sqrMagnitude < 1e-8f)
                return false;
            curlAxis = c.normalized;
            return true;
        }

        /// <summary>
        /// 手指链的弯曲方向：垂直于第一节、凸向手背，且落在链根原卷曲轴的弯曲平面里，
        /// 这样 <see cref="AsiKeyHinge.Axis"/> 算出的铰链轴正好是链根的卷曲轴。
        /// </summary>
        private static bool TryCurlPole(AsiKeyProject p, AsiKeyChain chain, Vector3 palm, out Vector3 pole)
        {
            pole = Vector3.zero;
            if (palm.sqrMagnitude < 1e-8f || chain.bones.Length < 3)
                return false;
            AsiKeyBone root = p.bones[chain.bones[0]];
            Vector3 u = p.bones[chain.bones[1]].rigRestPos - root.rigRestPos;
            if (u.sqrMagnitude < 1e-12f)
                return false;
            Vector3 curl;
            OriginalAxes(root, out _, out curl, out _);
            Vector3 v = Vector3.Cross(u.normalized, root.rigRestRot * curl);
            if (v.sqrMagnitude < 1e-8f)
                return false;
            if (Vector3.Dot(v, palm) > 0f)
                v = -v;
            pole = v.normalized;
            return true;
        }

        /// <summary>
        /// 旧版按掌心或静止弯曲给手指链定的弯曲方向，拇指铰链会偏离它自己的卷曲轴。
        /// 弯曲方向仍是旧默认值的链改成卷曲轴平面；手动改过的不动。有改动时返回 true。
        /// </summary>
        public static bool AlignCurlPoles(AsiKeyProject p)
        {
            if (p == null)
                return false;
            bool changed = false;
            for (int i = 0; i < p.chains.Count; i++)
            {
                AsiKeyChain chain = p.chains[i];
                if (chain.bones.Length < 3 || !IsFingerChain(p, i))
                    continue;
                int unit;
                int side;
                AsiHandAxes axes;
                Vector3 pole;
                if (!FingerSide(p, chain.bones[0], out unit, out side) || !TryAxes(p, unit, side, out axes)
                    || !TryCurlPole(p, chain, axes.palm, out pole))
                    continue;
                if (Vector3.Angle(chain.poleHint, pole) < 0.01f)
                    continue;
                if (Vector3.Angle(chain.poleHint, LegacyPole(p, chain, axes.palm)) > 0.5f)
                    continue;
                chain.poleHint = pole;
                changed = true;
            }
            if (changed)
                p.TouchStructure();
            return changed;
        }

        /// <summary>
        /// 手指外形坐标系的扭转跟 FK 标架对齐：+Z 沿骨骼，+Y 朝 <see cref="AsiKeyBipedFrame"/> 的 Y（手背侧）。
        /// 旧版按世界上方定扭转，拇指方块会和它的轴错开。要在轴向算完之后调用。有改动时返回 true。
        /// </summary>
        public static bool AlignShapeFrames(AsiKeyProject p)
        {
            if (p == null)
                return false;
            bool changed = false;
            for (int i = 0; i < p.bones.Count; i++)
            {
                Vector3 curl;
                if (!TryFrameCurl(p, i, out curl))
                    continue;
                AsiKeyBone b = p.bones[i];
                Vector3 along = b.axis.sqrMagnitude > 1e-10f ? b.axis.normalized : Vector3.forward;
                Vector3 up = Vector3.Cross(Quaternion.Inverse(b.rigRestRot) * curl, along);
                if (up.sqrMagnitude < 1e-8f)
                    continue;
                Quaternion frame = Quaternion.LookRotation(along, up);
                if (Quaternion.Angle(frame, b.frame) < 0.01f)
                    continue;
                b.frame = frame;
                changed = true;
            }
            return changed;
        }

        /// <summary>手指骨或指尖轴心所在的单位与手（0 左 1 右）。</summary>
        private static bool FingerSide(AsiKeyProject p, int bone, out int unit, out int side)
        {
            unit = 0;
            side = 0;
            if (p == null || bone < 0 || bone >= p.bones.Count)
                return false;
            AsiKeyBone b = p.bones[bone];
            int hb = b.humanBone;
            if (b.fingerTip && b.parent >= 0 && b.parent < p.bones.Count)
                hb = p.bones[b.parent].humanBone;
            if (hb >= (int)HumanBodyBones.LeftThumbProximal && hb <= (int)HumanBodyBones.LeftLittleDistal)
                side = 0;
            else if (hb >= (int)HumanBodyBones.RightThumbProximal && hb <= (int)HumanBodyBones.RightLittleDistal)
                side = 1;
            else
                return false;
            unit = b.unit;
            return true;
        }

        private static int JointBone(AsiKeyProject p, int unit, int side, int id, int joint)
        {
            int index = (side * Count + id) * 3 + joint;
            if (index < 0 || index >= JointMap.Length)
                return -1;
            return p.BoneByHuman(JointMap[index], unit);
        }

        private static bool Hits(List<int> selected, AsiFingerRef f)
        {
            if (selected == null)
                return false;
            for (int i = 0; i < 3; i++)
            {
                int j = JointAt(f, i);
                if (j >= 0 && selected.Contains(j))
                    return true;
            }
            return false;
        }

        private static Vector3 BoneDir(AsiKeyBone b)
        {
            Vector3 d = b.rigRestRot * b.axis;
            if (d.sqrMagnitude < 1e-10f)
                d = b.rigRestRot * Vector3.forward;
            return d.sqrMagnitude > 1e-10f ? d.normalized : Vector3.forward;
        }

        private static Vector3 Unit(int i)
        {
            return i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;
        }

        private static Vector3 Snap(Vector3 v)
        {
            int best = 0;
            float bestAbs = -1f;
            float sign = 1f;
            for (int i = 0; i < 3; i++)
            {
                float d = Vector3.Dot(v, Unit(i));
                if (Mathf.Abs(d) <= bestAbs)
                    continue;
                bestAbs = Mathf.Abs(d);
                best = i;
                sign = d >= 0f ? 1f : -1f;
            }
            return Unit(best) * sign;
        }

        private static Vector3 Across(AsiKeyProject p, int unit, int side, Vector3 palm, Vector3 forward)
        {
            int index = JointBone(p, unit, side, 1, 0);
            int little = JointBone(p, unit, side, 4, 0);
            int thumb = JointBone(p, unit, side, 0, 0);
            int middle = JointBone(p, unit, side, 2, 0);
            Vector3 across = Vector3.zero;
            if (index >= 0 && little >= 0)
                across = p.bones[index].rigRestPos - p.bones[little].rigRestPos;
            if (across.sqrMagnitude < 1e-8f && thumb >= 0 && middle >= 0)
                across = p.bones[thumb].rigRestPos - p.bones[middle].rigRestPos;
            across = Vector3.ProjectOnPlane(across, palm);
            if (across.sqrMagnitude < 1e-8f)
                across = Vector3.ProjectOnPlane(Vector3.Cross(palm, forward), palm);
            if (across.sqrMagnitude < 1e-8f)
                across = Vector3.right;
            return across.normalized;
        }

        /// <summary>旧版建链给的弯曲方向：静止伸直时取手背方向，已弯过的取静止弯曲估算。只用来识别未手动改过的链。</summary>
        private static Vector3 LegacyPole(AsiKeyProject p, AsiKeyChain chain, Vector3 palm)
        {
            Vector3 a = p.bones[chain.bones[0]].rigRestPos;
            Vector3 mid = p.bones[chain.bones[1]].rigRestPos;
            Vector3 tip = p.bones[chain.Effector].rigRestPos;
            Vector3 dir = tip - a;
            float len = dir.magnitude;
            if (len > 1e-6f)
            {
                dir /= len;
                Vector3 off = mid - a;
                off -= Vector3.Dot(off, dir) * dir;
                if (off.magnitude > len * 0.02f)
                    return AsiKeyRigBuilder.GuessPole(p, chain);
            }
            return (-palm).normalized;
        }

        /// <summary>
        /// 给每根有末节的手指在骨长尽头加一根指尖轴心。不进 IK 链、没有模型骨骼、外形隐藏所以点不中。
        /// 已经有的不重复加。追加在骨骼列表末尾，不改已有下标。要在轴向算完之后调用。有新增时返回 true。
        /// </summary>
        public static bool AddTipPivots(AsiKeyProject p)
        {
            if (p == null)
                return false;
            bool added = false;
            for (int u = 0; u < p.units.Count; u++)
            {
                if (!p.UnitIsHumanoid(u))
                    continue;
                for (int side = 0; side < 2; side++)
                {
                    for (int id = 0; id < Count; id++)
                    {
                        AsiFingerRef f;
                        if (!TryFinger(p, u, side, id, out f) || f.distal < 0 || HasTip(p, f.distal))
                            continue;
                        AsiKeyBone distal = p.bones[f.distal];
                        AsiKeyBone b = new AsiKeyBone();
                        b.fingerTip = true;
                        b.unit = distal.unit;
                        b.parent = f.distal;
                        b.humanBone = -1;
                        b.path = "";
                        b.side = distal.side;
                        b.mirror = -1;
                        using (p.Scope(distal.unit))
                            b.name = p.UniqueBoneName(distal.name + " Tip");
                        Vector3 axis = distal.axis.sqrMagnitude > 1e-8f ? distal.axis.normalized : Vector3.forward;
                        float len = Mathf.Max(distal.boneLength, 0.01f);
                        b.rigRestRot = distal.rigRestRot;
                        b.rigRestPos = distal.rigRestPos + distal.rigRestRot * (axis * len);
                        Quaternion inv = Quaternion.Inverse(distal.rigRestRot);
                        b.restPos = inv * (b.rigRestPos - distal.rigRestPos);
                        b.restRot = Quaternion.identity;
                        b.axis = axis;
                        b.boneLength = len * 0.25f;
                        b.frame = distal.frame;
                        b.shape.kind = AsiShapeKind.Hidden;
                        b.shape.visible = true;
                        p.bones.Add(b);
                        added = true;
                    }
                }
            }
            if (!added)
                return false;
            PairTips(p);
            p.TouchStructure();
            return true;
        }

        private static void PairTips(AsiKeyProject p)
        {
            for (int i = 0; i < p.bones.Count; i++)
            {
                AsiKeyBone b = p.bones[i];
                if (!b.fingerTip || b.mirror >= 0)
                    continue;
                string mirrorName;
                if (AsiKeyBipedMap.GuessSide(b.name, out mirrorName) == AsiBoneSide.Center)
                    continue;
                int j = p.BoneByName(mirrorName, b.unit);
                if (j < 0 || j == i || !p.bones[j].fingerTip)
                    continue;
                b.mirror = j;
                if (p.bones[j].mirror < 0)
                    p.bones[j].mirror = i;
            }
        }

        /// <summary>这根指尖轴心所驱动的手指链；不是指尖轴心，或末节不在手指链末端时为 -1。</summary>
        public static int TipChain(AsiKeyProject p, int bone)
        {
            if (p == null || bone < 0 || bone >= p.bones.Count || !p.bones[bone].fingerTip)
                return -1;
            int parent = p.bones[bone].parent;
            if (parent < 0 || parent >= p.bones.Count)
                return -1;
            int c = p.ChainOf(parent);
            if (c < 0 || !IsFingerChain(p, c) || p.chains[c].Effector != parent)
                return -1;
            return c;
        }

        private static bool HasTip(AsiKeyProject p, int distal)
        {
            for (int i = 0; i < p.bones.Count; i++)
            {
                if (p.bones[i].fingerTip && p.bones[i].parent == distal)
                    return true;
            }
            return false;
        }
    }
}
