using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// Kochanek-Bartels（TCB）插值，参数范围与 Max 一致：0..50，25 为默认（即 KB 的 0）。
    /// 段 k1→k2 的时间先经 k1.easeFrom / k2.easeTo 重映射。
    /// 不等间距关键帧按相邻段长度修正切线。
    /// </summary>
    public static class AsiKeyInterpTCB
    {
        public static void Coeffs(AsiKey k, out float inA, out float inB, out float outA, out float outB)
        {
            float t = (k.tension - 25f) / 25f;
            float c = (k.continuity - 25f) / 25f;
            float b = (k.bias - 25f) / 25f;
            outA = (1f - t) * (1f + c) * (1f + b) * 0.5f;
            outB = (1f - t) * (1f - c) * (1f - b) * 0.5f;
            inA = (1f - t) * (1f - c) * (1f + b) * 0.5f;
            inB = (1f - t) * (1f + c) * (1f - b) * 0.5f;
        }

        public static float EaseU(AsiKey a, AsiKey b, float u)
        {
            return AsiKeyMath.Ease(u, a.easeFrom / 50f, b.easeTo / 50f);
        }

        private static void Spans(bool has0, bool has3, ref float n0, float n1, ref float n2, out float sOut, out float sIn)
        {
            if (!has0 || n0 <= 1e-5f) n0 = n1;
            if (!has3 || n2 <= 1e-5f) n2 = n1;
            sOut = 2f * n1 / (n0 + n1);
            sIn = 2f * n1 / (n1 + n2);
        }

        public static float Seg(float p0, float p1, float p2, float p3, bool has0, bool has3,
            AsiKey k1, AsiKey k2, float n0, float n1, float n2, float u)
        {
            float d1 = p2 - p1;
            float d0 = has0 ? p1 - p0 : d1;
            float d2 = has3 ? p3 - p2 : d1;
            float sOut;
            float sIn;
            Spans(has0, has3, ref n0, n1, ref n2, out sOut, out sIn);
            float iA, iB, oA, oB, x0, x1;
            Coeffs(k1, out x0, out x1, out oA, out oB);
            Coeffs(k2, out iA, out iB, out x0, out x1);
            float ts = (oA * d0 + oB * d1) * sOut;
            float td = (iA * d1 + iB * d2) * sIn;
            return AsiKeyMath.Hermite(p1, p2, ts, td, u);
        }

        public static Vector3 Seg(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, bool has0, bool has3,
            AsiKey k1, AsiKey k2, float n0, float n1, float n2, float u)
        {
            Vector3 d1 = p2 - p1;
            Vector3 d0 = has0 ? p1 - p0 : d1;
            Vector3 d2 = has3 ? p3 - p2 : d1;
            float sOut;
            float sIn;
            Spans(has0, has3, ref n0, n1, ref n2, out sOut, out sIn);
            float iA, iB, oA, oB, x0, x1;
            Coeffs(k1, out x0, out x1, out oA, out oB);
            Coeffs(k2, out iA, out iB, out x0, out x1);
            Vector3 ts = (oA * d0 + oB * d1) * sOut;
            Vector3 td = (iA * d1 + iB * d2) * sIn;
            return AsiKeyMath.Hermite(p1, p2, ts, td, u);
        }

        /// <summary>
        /// 四元数 TCB：差分取对数空间的半角向量，用 KB 系数组合成切线，
        /// 再以 q·exp(±T/3) 作为贝塞尔控制点做 de Casteljau 球面插值。
        /// </summary>
        public static Quaternion SegQuat(Quaternion q0, Quaternion q1, Quaternion q2, Quaternion q3, bool has0, bool has3,
            AsiKey k1, AsiKey k2, float n0, float n1, float n2, float u)
        {
            q1 = AsiKeyMath.Norm(q1);
            q2 = AsiKeyMath.Align(AsiKeyMath.Norm(q2), q1);
            Vector3 d1 = AsiKeyMath.Log(Quaternion.Inverse(q1) * q2);
            Vector3 d0 = d1;
            if (has0)
            {
                q0 = AsiKeyMath.Align(AsiKeyMath.Norm(q0), q1);
                d0 = AsiKeyMath.Log(Quaternion.Inverse(q0) * q1);
            }
            Vector3 d2 = d1;
            if (has3)
            {
                q3 = AsiKeyMath.Align(AsiKeyMath.Norm(q3), q2);
                d2 = AsiKeyMath.Log(Quaternion.Inverse(q2) * q3);
            }
            float sOut;
            float sIn;
            Spans(has0, has3, ref n0, n1, ref n2, out sOut, out sIn);
            float iA, iB, oA, oB, x0, x1;
            Coeffs(k1, out x0, out x1, out oA, out oB);
            Coeffs(k2, out iA, out iB, out x0, out x1);
            Vector3 ts = (oA * d0 + oB * d1) * sOut;
            Vector3 td = (iA * d1 + iB * d2) * sIn;
            Quaternion a = q1 * AsiKeyMath.Exp(ts / 3f);
            Quaternion b = q2 * AsiKeyMath.Exp(-td / 3f);
            return BezierSlerp(q1, a, b, q2, u);
        }

        public static Quaternion BezierSlerp(Quaternion q0, Quaternion q1, Quaternion q2, Quaternion q3, float u)
        {
            Quaternion a = Quaternion.Slerp(q0, q1, u);
            Quaternion b = Quaternion.Slerp(q1, q2, u);
            Quaternion c = Quaternion.Slerp(q2, q3, u);
            Quaternion d = Quaternion.Slerp(a, b, u);
            Quaternion e = Quaternion.Slerp(b, c, u);
            return Quaternion.Slerp(d, e, u);
        }
    }
}
