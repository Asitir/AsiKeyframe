using System.Collections.Generic;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// Maya Graph Editor 式曲线：每个通道独立，切线类型决定斜率，权重切线按贝塞尔控制点求值。
    /// 旋转通道是按轨道旋转顺序拆出的欧拉角（度）。
    /// </summary>
    public static class AsiKeyInterpCurve
    {
        public static float Seg(float v0, float v1, AsiTangent t0, AsiTangent t1, float f0, float f1, float f)
        {
            float dt = f1 - f0;
            if (dt <= 1e-6f)
                return v1;
            if (t0.outType == AsiTangentType.Stepped)
                return v0;
            float u = Mathf.Clamp01((f - f0) / dt);
            bool weighted = t0.weighted || t1.weighted;
            if (!weighted)
                return AsiKeyMath.Hermite(v0, v1, t0.outSlope * dt, t1.inSlope * dt, u);

            float w0 = Mathf.Clamp(t0.weighted ? t0.outWeight : 1f / 3f, 0.001f, 1f);
            float w1 = Mathf.Clamp(t1.weighted ? t1.inWeight : 1f / 3f, 0.001f, 1f);
            float x1 = w0;
            float x2 = 1f - w1;
            float y1 = v0 + t0.outSlope * dt * w0;
            float y2 = v1 - t1.inSlope * dt * w1;
            float s = SolveBezierX(x1, x2, u);
            float is1 = 1f - s;
            return is1 * is1 * is1 * v0 + 3f * is1 * is1 * s * y1 + 3f * is1 * s * s * y2 + s * s * s * v1;
        }

        private static float BezierX(float x1, float x2, float s)
        {
            float i = 1f - s;
            return 3f * i * i * s * x1 + 3f * i * s * s * x2 + s * s * s;
        }

        private static float SolveBezierX(float x1, float x2, float u)
        {
            float lo = 0f;
            float hi = 1f;
            float s = u;
            for (int it = 0; it < 12; it++)
            {
                float x = BezierX(x1, x2, s) - u;
                if (Mathf.Abs(x) < 1e-6f)
                    return s;
                if (x > 0f) hi = s; else lo = s;
                float i = 1f - s;
                float dx = 3f * i * i * x1 + 6f * i * s * (x2 - x1) + 3f * s * s * (1f - x2);
                float next = Mathf.Abs(dx) > 1e-6f ? s - x / dx : 0.5f * (lo + hi);
                if (next <= lo || next >= hi)
                    next = 0.5f * (lo + hi);
                s = next;
            }
            return s;
        }

        /// <summary>
        /// 没有切线数据的值（链关键帧的 swivel）按「自动」切线插值段 1→2：v0 / v3 是前后相邻关键帧的值，
        /// 没有时 has0 / has3 传 false；f0..f3 是对应帧号。
        /// </summary>
        public static float AutoSeg(float v0, float v1, float v2, float v3, bool has0, bool has3,
            float f0, float f1, float f2, float f3, float f)
        {
            AsiTangent t1 = AsiTangent.Default(AsiTangentType.Auto);
            AsiTangent t2 = t1;
            t1.outSlope = AutoSlope(v1, v0, v2, f1, f0, f2, has0, true);
            t2.inSlope = AutoSlope(v2, v1, v3, f2, f1, f3, true, has3);
            return Seg(v1, v2, t1, t2, f1, f2, f);
        }

        private static float AutoSlope(float v, float vp, float vn, float f, float fp, float fn, bool hasPrev, bool hasNext)
        {
            float sPrev = hasPrev ? (v - vp) / Mathf.Max(1e-5f, f - fp) : 0f;
            float sNext = hasNext ? (vn - v) / Mathf.Max(1e-5f, fn - f) : 0f;
            if (!hasPrev) sPrev = sNext;
            if (!hasNext) sNext = sPrev;
            int i = hasPrev ? 1 : 0;
            int n = i + (hasNext ? 2 : 1);
            return Slope(AsiTangentType.Auto, false, i, n, v, vp, vn, f, fp, fn, sPrev, sNext);
        }

        public static float GetValue(AsiKey k, int ch, int rotCh)
        {
            if (ch < rotCh)
            {
                int m = ch / 3;
                if (m >= k.euler.Length)
                    return 0f;
                Vector3 e = k.euler[m];
                int a = ch % 3;
                return a == 0 ? e.x : (a == 1 ? e.y : e.z);
            }
            int p = ch - rotCh;
            return p == 0 ? k.pos.x : (p == 1 ? k.pos.y : k.pos.z);
        }

        public static void SetValue(AsiKey k, int ch, int rotCh, float v)
        {
            if (ch < rotCh)
            {
                int m = ch / 3;
                if (m >= k.euler.Length)
                    return;
                Vector3 e = k.euler[m];
                int a = ch % 3;
                if (a == 0) e.x = v; else if (a == 1) e.y = v; else e.z = v;
                k.euler[m] = e;
                return;
            }
            int p = ch - rotCh;
            Vector3 pos = k.pos;
            if (p == 0) pos.x = v; else if (p == 1) pos.y = v; else pos.z = v;
            k.pos = pos;
        }

        public static AsiTangent GetTan(AsiKey k, int ch)
        {
            if (k.tan == null || ch >= k.tan.Length)
                return AsiTangent.Default(AsiTangentType.Auto);
            return k.tan[ch];
        }

        /// <summary>按切线类型重算一条通道上所有非「固定」切线的斜率。</summary>
        public static void RecomputeChannel(List<AsiKey> keys, int ch, int rotCh)
        {
            int n = keys.Count;
            for (int i = 0; i < n; i++)
            {
                AsiKey k = keys[i];
                if (k.tan == null || ch >= k.tan.Length)
                    continue;
                AsiTangent t = k.tan[ch];
                float v = GetValue(k, ch, rotCh);
                bool hasPrev = i > 0;
                bool hasNext = i < n - 1;
                float vp = hasPrev ? GetValue(keys[i - 1], ch, rotCh) : v;
                float vn = hasNext ? GetValue(keys[i + 1], ch, rotCh) : v;
                float fp = hasPrev ? keys[i - 1].frame : k.frame - 1f;
                float fn = hasNext ? keys[i + 1].frame : k.frame + 1f;
                float sPrev = hasPrev ? (v - vp) / Mathf.Max(1e-5f, k.frame - fp) : 0f;
                float sNext = hasNext ? (vn - v) / Mathf.Max(1e-5f, fn - k.frame) : 0f;
                if (!hasPrev) sPrev = sNext;
                if (!hasNext) sNext = sPrev;

                if (t.inType != AsiTangentType.Fixed)
                    t.inSlope = Slope(t.inType, true, i, n, v, vp, vn, k.frame, fp, fn, sPrev, sNext);
                if (t.outType != AsiTangentType.Fixed)
                    t.outSlope = Slope(t.outType, false, i, n, v, vp, vn, k.frame, fp, fn, sPrev, sNext);
                if (!t.broken && t.inType == AsiTangentType.Fixed && t.outType != AsiTangentType.Fixed && t.outType != AsiTangentType.Stepped)
                    t.outSlope = t.inSlope;
                k.tan[ch] = t;
            }
        }

        private static float Slope(AsiTangentType type, bool isIn, int i, int n, float v, float vp, float vn,
            float f, float fp, float fn, float sPrev, float sNext)
        {
            bool hasPrev = i > 0;
            bool hasNext = i < n - 1;
            float spline;
            if (hasPrev && hasNext)
                spline = (vn - vp) / Mathf.Max(1e-5f, fn - fp);
            else
                spline = hasPrev ? sPrev : sNext;

            switch (type)
            {
                case AsiTangentType.Linear:
                    return isIn ? sPrev : sNext;
                case AsiTangentType.Flat:
                case AsiTangentType.Stepped:
                    return 0f;
                case AsiTangentType.Spline:
                    return spline;
                case AsiTangentType.Clamped:
                {
                    const float eps = 1e-3f;
                    bool closePrev = hasPrev && Mathf.Abs(v - vp) < eps;
                    bool closeNext = hasNext && Mathf.Abs(vn - v) < eps;
                    if (closePrev || closeNext)
                        return isIn ? sPrev : sNext;
                    return spline;
                }
                case AsiTangentType.Plateau:
                {
                    if (!hasPrev || !hasNext)
                        return 0f;
                    if ((v - vp) * (vn - v) <= 0f)
                        return 0f;
                    return ClampOvershoot(spline, v, vp, vn, f, fp, fn);
                }
                case AsiTangentType.Auto:
                {
                    if (hasPrev && hasNext && (v - vp) * (vn - v) <= 0f)
                        return 0f;
                    return ClampOvershoot(spline, v, vp, vn, f, fp, fn, hasPrev, hasNext);
                }
                default:
                    return spline;
            }
        }

        private static float ClampOvershoot(float s, float v, float vp, float vn, float f, float fp, float fn, bool hasPrev = true, bool hasNext = true)
        {
            float limit = float.MaxValue;
            if (hasPrev)
                limit = Mathf.Min(limit, 3f * Mathf.Abs(v - vp) / Mathf.Max(1e-5f, f - fp));
            if (hasNext)
                limit = Mathf.Min(limit, 3f * Mathf.Abs(vn - v) / Mathf.Max(1e-5f, fn - f));
            if (limit == float.MaxValue)
                return s;
            return Mathf.Clamp(s, -limit, limit);
        }
    }
}
