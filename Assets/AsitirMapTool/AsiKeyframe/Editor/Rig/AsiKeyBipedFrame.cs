using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// Biped 轴向：K帧变换轴在「局部」参考坐标下用的坐标系，写成控制器局部空间里的旋转。
    /// X 沿骨骼（脚 → 脚趾、小腿 → 脚）；三成员链的链根与中间关节 Z 取铰链轴（膝/肘绕 Z 弯，手指链除外），X 取骨骼方向垂直于铰链的分量；
    /// 手指（含拇指与指尖轴心）Z 取这节的原局部卷曲轴，五指都绕 Z 卷曲；
    /// 其余骨骼 Z 取角色右方向垂直于 X 的分量（与 X 接近平行时取前方），并让 Y = Z × X 尽量朝上；质心直接用角色根的轴。
    /// 自由度按 Biped：三成员链的中间关节（小腿/小臂）只有 X（特殊旋转）与 Z（铰链）。
    /// </summary>
    public static class AsiKeyBipedFrame
    {
        public const int AxisX = 1;
        public const int AxisY = 2;
        public const int AxisZ = 4;
        public const int AllAxes = AxisX | AxisY | AxisZ;

        private static AsiKeyProject s_project;
        private static int s_stamp = int.MinValue;
        private static Quaternion[] s_frames = new Quaternion[0];

        /// <summary>骨骼 i 的 Biped 坐标系（控制器局部空间）。</summary>
        public static Quaternion Local(AsiKeyProject p, int i)
        {
            Ensure(p);
            return i >= 0 && i < s_frames.Length ? s_frames[i] : Quaternion.identity;
        }

        /// <summary>控制器局部空间里的第 k 根轴（0 = X，1 = Y，2 = Z）。</summary>
        public static Vector3 Axis(AsiKeyProject p, int i, int k)
        {
            return Local(p, i) * Unit(k);
        }

        public static Vector3 Unit(int k)
        {
            return k == 0 ? Vector3.right : k == 1 ? Vector3.up : Vector3.forward;
        }

        /// <summary>局部参考坐标下可转的轴（AxisX | AxisY | AxisZ）。关闭铰链锁的链、以及手指，不限制。</summary>
        public static int AxisMask(AsiKeyProject p, int i)
        {
            int c;
            if (AsiKeyHinge.MidOf(p, i, out c) && !AsiKeyFingers.IsFingerChain(p, c))
                return AxisX | AxisZ;
            return AllAxes;
        }

        /// <summary>bone 是三成员链的中间关节（父级就是链根）时返回链下标，否则 -1。</summary>
        public static int MidOf3(AsiKeyProject p, int bone)
        {
            int c = p.ChainOf(bone);
            if (c < 0)
                return -1;
            AsiKeyChain chain = p.chains[c];
            if (chain.bones.Length != 3 || chain.bones[1] != bone || p.bones[bone].parent != chain.bones[0])
                return -1;
            return c;
        }

        private static void Ensure(AsiKeyProject p)
        {
            if (s_project == p && s_stamp == p.structureStamp && s_frames.Length == p.bones.Count)
                return;
            s_project = p;
            s_stamp = p.structureStamp;
            s_frames = new Quaternion[p.bones.Count];
            for (int i = 0; i < s_frames.Length; i++)
                s_frames[i] = Compute(p, i);
        }

        private static Quaternion Compute(AsiKeyProject p, int i)
        {
            AsiKeyBone b = p.bones[i];
            Quaternion rest = b.rigRestRot;
            Quaternion inv = Quaternion.Inverse(rest);
            if (b.isCom)
                return AsiKeyMath.Norm(inv);
            Vector3 x = rest * b.axis;
            if (x.sqrMagnitude < 1e-10f)
                x = rest * Vector3.forward;
            x.Normalize();

            Vector3 z = Vector3.zero;
            bool hinge = AsiKeyFingers.TryFrameCurl(p, i, out z);
            int c = hinge ? -1 : p.ChainOf(i);
            if (c >= 0)
            {
                AsiKeyChain chain = p.chains[c];
                int m = chain.MemberIndex(i);
                if (!AsiKeyFingers.IsFingerChain(p, c) && chain.bones.Length == 3 && m <= 1 && p.bones[chain.bones[1]].parent == chain.bones[0])
                {
                    float restBend;
                    z = p.bones[chain.bones[0]].rigRestRot * AsiKeyHinge.Axis(p, chain, out restBend);
                    hinge = true;
                }
            }
            if (hinge)
            {
                // Z 必须正好是铰链轴，X 取骨骼方向垂直于铰链的分量，绕 X 转才不会带出弯曲。
                z.Normalize();
                Vector3 px = x - Vector3.Dot(x, z) * z;
                if (px.sqrMagnitude > 1e-8f)
                    x = px.normalized;
            }
            else
            {
                z = Mathf.Abs(Vector3.Dot(x, Vector3.right)) > 0.9f ? Vector3.forward : Vector3.right;
                z -= Vector3.Dot(z, x) * x;
                if (z.sqrMagnitude < 1e-8f)
                    z = AsiKeyMath.Perpendicular(x);
                z.Normalize();
            }
            Vector3 y = Vector3.Cross(z, x);
            if (!hinge && Vector3.Dot(y, Vector3.up) < -1e-3f)
            {
                z = -z;
                y = -y;
            }
            return AsiKeyMath.Norm(inv * Quaternion.LookRotation(z, y));
        }
    }
}
