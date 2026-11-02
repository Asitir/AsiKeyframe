using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// Biped 式膝/肘铰链：三成员链的中间关节相对链根只绕一根铰链轴弯。
    /// 铰链轴在链根局部空间里固定，与 <see cref="AsiKeyIKSolver"/> 两骨解析解的弯曲平面相同：
    /// axis = (poleHint 投影到垂直于第一节的方向) × 第一节方向，绕它转正角度时膝/肘向 poleHint 那侧凸出。
    /// 弯曲角 0 为伸直，中间关节的局部旋转写成 AngleAxis(弯曲角 - 静止弯曲角, axis) * restRot。
    /// </summary>
    public static class AsiKeyHinge
    {
        public static bool Active(AsiKeyProject p, AsiKeyChain chain)
        {
            if (chain == null || !chain.hinge || chain.bones.Length != 3)
                return false;
            for (int k = 0; k < 3; k++)
            {
                if (chain.bones[k] < 0 || chain.bones[k] >= p.bones.Count)
                    return false;
            }
            return p.bones[chain.bones[1]].parent == chain.bones[0];
        }

        /// <summary>bone 是开启铰链锁的三成员链的中间关节时返回 true。</summary>
        public static bool MidOf(AsiKeyProject p, int bone, out int chain)
        {
            chain = p.ChainOf(bone);
            if (chain < 0)
                return false;
            AsiKeyChain c = p.chains[chain];
            return Active(p, c) && c.bones[1] == bone;
        }

        /// <summary>链根局部空间的铰链轴，以及静止姿势的弯曲角（度）。</summary>
        public static Vector3 Axis(AsiKeyProject p, AsiKeyChain chain, out float restBend)
        {
            AsiKeyBone root = p.bones[chain.bones[0]];
            AsiKeyBone mid = p.bones[chain.bones[1]];
            Quaternion invRoot = Quaternion.Inverse(root.rigRestRot);
            Vector3 u = invRoot * (mid.rigRestPos - root.rigRestPos);
            if (u.sqrMagnitude < 1e-12f)
                u = root.axis;
            u.Normalize();
            Vector3 lower = invRoot * (mid.rigRestRot * p.LinkOffset(chain.bones[2]));
            Vector3 f = invRoot * chain.poleHint;
            f -= Vector3.Dot(f, u) * u;
            if (f.sqrMagnitude < 1e-8f)
                f = -(lower - Vector3.Dot(lower, u) * u);
            if (f.sqrMagnitude < 1e-8f * Mathf.Max(lower.sqrMagnitude, 1e-12f))
                f = AsiKeyMath.Perpendicular(u);
            Vector3 axis = Vector3.Cross(f.normalized, u).normalized;
            if (AsiKeyFingers.IsFingerChain(p, chain))
            {
                // 手指中节只能绕自己的原卷曲轴弯，铰链轴取它而不是链根的轴，否则弯曲会带侧摆
                Vector3 curl = AsiKeyFingers.CurlAxisInParent(p, chain.bones[1]);
                axis = Vector3.Dot(curl, axis) < 0f ? -curl : curl;
            }
            Vector3 l = lower - Vector3.Dot(lower, axis) * axis;
            restBend = l.sqrMagnitude > 1e-12f ? Vector3.SignedAngle(u, l, axis) : 0f;
            return axis;
        }

        /// <summary>q 绕 axis（单位向量）的扭转分量，度，[-180, 180)。</summary>
        public static float TwistAngle(Quaternion q, Vector3 axis)
        {
            float d = q.x * axis.x + q.y * axis.y + q.z * axis.z;
            if (Mathf.Abs(d) < 1e-9f && Mathf.Abs(q.w) < 1e-9f)
                return 0f;
            float a = 2f * Mathf.Atan2(d, q.w) * Mathf.Rad2Deg;
            return Mathf.Repeat(a + 180f, 360f) - 180f;
        }

        /// <summary>中间关节局部旋转（相对链根）里绕铰链轴的弯曲角，度。</summary>
        public static float Bend(AsiKeyProject p, AsiKeyChain chain, Quaternion local)
        {
            float restBend;
            Vector3 axis = Axis(p, chain, out restBend);
            Quaternion r = p.bones[chain.bones[1]].restRot;
            return Mathf.Repeat(restBend + TwistAngle(local * Quaternion.Inverse(r), axis) + 180f, 360f) - 180f;
        }

        public static Quaternion Project(AsiKeyProject p, AsiKeyChain chain, Quaternion local)
        {
            bool clamped;
            return Project(p, chain, local, out clamped);
        }

        /// <summary>只保留绕铰链轴的弯曲，弯曲角夹到 bendMin..bendMax（静止姿势本身总在范围内）。</summary>
        public static Quaternion Project(AsiKeyProject p, AsiKeyChain chain, Quaternion local, out bool clamped)
        {
            float restBend;
            Vector3 axis = Axis(p, chain, out restBend);
            Quaternion r = p.bones[chain.bones[1]].restRot;
            float lo = Mathf.Min(Mathf.Min(chain.bendMin, chain.bendMax), restBend);
            float hi = Mathf.Max(Mathf.Max(chain.bendMin, chain.bendMax), restBend);
            float bend = lo + Mathf.Repeat(restBend + TwistAngle(AsiKeyMath.Norm(local) * Quaternion.Inverse(r), axis) - lo, 360f);
            clamped = false;
            if (bend > hi + 1e-3f)
            {
                clamped = true;
                bend = bend - hi <= lo + 360f - bend ? hi : lo;
            }
            return AsiKeyMath.Norm(Quaternion.AngleAxis(bend - restBend, axis) * r);
        }
    }
}
