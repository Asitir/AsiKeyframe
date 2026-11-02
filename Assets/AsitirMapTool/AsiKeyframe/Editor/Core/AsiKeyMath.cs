using UnityEngine;

namespace AsitirTool.Editor
{
    public static class AsiKeyMath
    {
        public static Quaternion Norm(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 1e-8f || float.IsNaN(m))
                return Quaternion.identity;
            float inv = 1f / m;
            return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
        }

        public static Quaternion Neg(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, -q.z, -q.w);
        }

        public static Quaternion Align(Quaternion q, Quaternion reference)
        {
            return Quaternion.Dot(q, reference) < 0f ? Neg(q) : q;
        }

        /// <summary>单位四元数的对数（半角轴向量），Exp(Log(q)) == q。</summary>
        public static Vector3 Log(Quaternion q)
        {
            Vector3 v = new Vector3(q.x, q.y, q.z);
            float s = v.magnitude;
            if (s < 1e-7f)
                return v;
            float a = Mathf.Atan2(s, Mathf.Clamp(q.w, -1f, 1f));
            return v * (a / s);
        }

        public static Quaternion Exp(Vector3 v)
        {
            float a = v.magnitude;
            if (a < 1e-7f)
                return Norm(new Quaternion(v.x, v.y, v.z, 1f));
            float s = Mathf.Sin(a) / a;
            return new Quaternion(v.x * s, v.y * s, v.z * s, Mathf.Cos(a));
        }

        /// <summary>把旋转增量按权重缩放，权重可以大于 1。</summary>
        public static Quaternion Scale(Quaternion d, float w)
        {
            if (Mathf.Abs(w - 1f) < 1e-5f)
                return d;
            return Exp(Log(Align(Norm(d), Quaternion.identity)) * w);
        }

        /// <summary>Max 的 Ease To / Ease From 时间重映射，a、b 为 0..1。</summary>
        public static float Ease(float u, float a, float b)
        {
            if (u <= 0f)
                return 0f;
            if (u >= 1f)
                return 1f;
            a = Mathf.Clamp01(a);
            b = Mathf.Clamp01(b);
            float s = a + b;
            if (s < 1e-5f)
                return u;
            if (s > 1f)
            {
                a /= s;
                b /= s;
            }
            float k = 1f / (2f - a - b);
            if (u < a)
                return (k / a) * u * u;
            if (u < 1f - b)
                return k * (2f * u - a);
            u = 1f - u;
            return 1f - (k / b) * u * u;
        }

        public static float Hermite(float p0, float p1, float m0, float m1, float u)
        {
            float u2 = u * u;
            float u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * p0 + (u3 - 2f * u2 + u) * m0 + (-2f * u3 + 3f * u2) * p1 + (u3 - u2) * m1;
        }

        public static Vector3 Hermite(Vector3 p0, Vector3 p1, Vector3 m0, Vector3 m1, float u)
        {
            float u2 = u * u;
            float u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * p0 + (u3 - 2f * u2 + u) * m0 + (-2f * u3 + 3f * u2) * p1 + (u3 - u2) * m1;
        }

        private static readonly int[][] OrderAxes =
        {
            new[] { 0, 1, 2 },
            new[] { 1, 2, 0 },
            new[] { 2, 0, 1 },
            new[] { 0, 2, 1 },
            new[] { 1, 0, 2 },
            new[] { 2, 1, 0 },
        };

        private static Vector3 Axis(int i)
        {
            return i == 0 ? Vector3.right : (i == 1 ? Vector3.up : Vector3.forward);
        }

        private static float Get(Vector3 v, int i)
        {
            return i == 0 ? v.x : (i == 1 ? v.y : v.z);
        }

        private static void Set(ref Vector3 v, int i, float value)
        {
            if (i == 0) v.x = value;
            else if (i == 1) v.y = value;
            else v.z = value;
        }

        private static float QComp(Quaternion q, int i)
        {
            return i == 0 ? q.x : (i == 1 ? q.y : q.z);
        }

        /// <summary>按 Maya 旋转顺序组合欧拉角（度）。顺序 ABC 表示先 A、再 B、最后 C。</summary>
        public static Quaternion EulerToQuat(Vector3 e, AsiRotOrder order)
        {
            int[] ax = OrderAxes[(int)order];
            Quaternion qa = Quaternion.AngleAxis(Get(e, ax[0]), Axis(ax[0]));
            Quaternion qb = Quaternion.AngleAxis(Get(e, ax[1]), Axis(ax[1]));
            Quaternion qc = Quaternion.AngleAxis(Get(e, ax[2]), Axis(ax[2]));
            return qc * qb * qa;
        }

        /// <summary>四元数转欧拉角（度），在两组等价解中取离 hint 最近的一组，并展开到 hint 附近的圈数。</summary>
        public static Vector3 QuatToEuler(Quaternion q, AsiRotOrder order, Vector3 hint)
        {
            q = Norm(q);
            int[] ax = OrderAxes[(int)order];
            int i = ax[0];
            int j = ax[1];
            int k = ax[2];
            bool even = order == AsiRotOrder.XYZ || order == AsiRotOrder.YZX || order == AsiRotOrder.ZXY;
            Matrix4x4 m = Matrix4x4.Rotate(q);
            float sign = even ? -1f : 1f;
            float sb = Mathf.Clamp(sign * m[k, i], -1f, 1f);
            float a;
            float b = Mathf.Asin(sb);
            float c;
            if (Mathf.Abs(sb) > 0.99995f)
            {
                a = Get(hint, i) * Mathf.Deg2Rad;
                Quaternion qa = Quaternion.AngleAxis(a * Mathf.Rad2Deg, Axis(i));
                Quaternion qb = Quaternion.AngleAxis(b * Mathf.Rad2Deg, Axis(j));
                Quaternion qc = q * Quaternion.Inverse(qa) * Quaternion.Inverse(qb);
                c = 2f * Mathf.Atan2(QComp(qc, k), qc.w);
            }
            else
            {
                a = Mathf.Atan2(-sign * m[k, j], m[k, k]);
                c = Mathf.Atan2(-sign * m[j, i], m[i, i]);
            }

            Vector3 r1 = Vector3.zero;
            Set(ref r1, i, a * Mathf.Rad2Deg);
            Set(ref r1, j, b * Mathf.Rad2Deg);
            Set(ref r1, k, c * Mathf.Rad2Deg);
            Vector3 r2 = Vector3.zero;
            Set(ref r2, i, a * Mathf.Rad2Deg + 180f);
            Set(ref r2, j, 180f - b * Mathf.Rad2Deg);
            Set(ref r2, k, c * Mathf.Rad2Deg + 180f);
            r1 = Unwrap(r1, hint);
            r2 = Unwrap(r2, hint);
            float d1 = Mathf.Abs(r1.x - hint.x) + Mathf.Abs(r1.y - hint.y) + Mathf.Abs(r1.z - hint.z);
            float d2 = Mathf.Abs(r2.x - hint.x) + Mathf.Abs(r2.y - hint.y) + Mathf.Abs(r2.z - hint.z);
            return d2 + 0.001f < d1 ? r2 : r1;
        }

        public static float UnwrapAngle(float v, float hint)
        {
            return v + 360f * Mathf.Round((hint - v) / 360f);
        }

        public static Vector3 Unwrap(Vector3 v, Vector3 hint)
        {
            return new Vector3(UnwrapAngle(v.x, hint.x), UnwrapAngle(v.y, hint.y), UnwrapAngle(v.z, hint.z));
        }

        /// <summary>角色根空间沿 YZ 平面（左右）镜像。</summary>
        public static Vector3 MirrorX(Vector3 v)
        {
            return new Vector3(-v.x, v.y, v.z);
        }

        public static Quaternion MirrorX(Quaternion q)
        {
            return new Quaternion(q.x, -q.y, -q.z, q.w);
        }

        public static bool Finite(float v)
        {
            return !(float.IsNaN(v) || float.IsInfinity(v));
        }

        public static bool Finite(Vector3 v)
        {
            return Finite(v.x) && Finite(v.y) && Finite(v.z);
        }

        public static bool Finite(Quaternion q)
        {
            return Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w);
        }

        public static Vector3 Perpendicular(Vector3 v)
        {
            Vector3 c = Vector3.Cross(v, Vector3.up);
            if (c.sqrMagnitude < 1e-6f)
                c = Vector3.Cross(v, Vector3.right);
            return c.normalized;
        }
    }
}
