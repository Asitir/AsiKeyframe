using System.Collections.Generic;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>单条轨道在某一帧的取值。</summary>
    public static class AsiKeyTrackEval
    {
        public const int Before = 0;
        public const int After = 1;
        public const int Inside = 2;

        /// <summary>定位 f 所在段：Inside 时 keys[i] ≤ f &lt; keys[i+1]，u 为未缓动的段内比例。</summary>
        public static int Locate(List<AsiKey> keys, float f, out int i, out float u)
        {
            int n = keys.Count;
            u = 0f;
            i = 0;
            if (n == 0 || f <= keys[0].frame)
                return Before;
            if (f >= keys[n - 1].frame)
            {
                i = n - 1;
                return After;
            }
            int lo = 0;
            int hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid].frame <= f) lo = mid; else hi = mid;
            }
            i = lo;
            float span = keys[lo + 1].frame - keys[lo].frame;
            u = span > 1e-6f ? (f - keys[lo].frame) / span : 0f;
            return Inside;
        }

        public static int FindKeyIndex(List<AsiKey> keys, float f, float tolerance = 0.01f)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (Mathf.Abs(keys[i].frame - f) <= tolerance)
                    return i;
            }
            return -1;
        }

        public static Quaternion KeyRot(AsiKey k, int m, Quaternion fallback)
        {
            if (m < 0 || m >= k.rot.Length)
                return fallback;
            Quaternion q = k.rot[m];
            if (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f)
                return fallback;
            return q;
        }

        public static Quaternion EvalRot(AsiKeyTrack tr, int m, float f, Quaternion fallback)
        {
            List<AsiKey> keys = tr.keys;
            int n = keys.Count;
            if (n == 0)
                return fallback;
            int i;
            float u;
            int state = Locate(keys, f, out i, out u);
            if (state == Before)
                return KeyRot(keys[0], m, fallback);
            if (state == After)
                return KeyRot(keys[n - 1], m, fallback);

            AsiKey k1 = keys[i];
            AsiKey k2 = keys[i + 1];
            if (tr.mode == AsiInterpMode.Curve)
            {
                if (m >= k1.euler.Length || m >= k2.euler.Length)
                    return KeyRot(k1, m, fallback);
                Vector3 e1 = k1.euler[m];
                Vector3 e2 = k2.euler[m];
                Vector3 e;
                int c = m * 3;
                e.x = AsiKeyInterpCurve.Seg(e1.x, e2.x, AsiKeyInterpCurve.GetTan(k1, c), AsiKeyInterpCurve.GetTan(k2, c), k1.frame, k2.frame, f);
                e.y = AsiKeyInterpCurve.Seg(e1.y, e2.y, AsiKeyInterpCurve.GetTan(k1, c + 1), AsiKeyInterpCurve.GetTan(k2, c + 1), k1.frame, k2.frame, f);
                e.z = AsiKeyInterpCurve.Seg(e1.z, e2.z, AsiKeyInterpCurve.GetTan(k1, c + 2), AsiKeyInterpCurve.GetTan(k2, c + 2), k1.frame, k2.frame, f);
                return AsiKeyMath.EulerToQuat(e, tr.rotOrder);
            }

            bool has0 = i > 0;
            bool has3 = i + 2 < n;
            Quaternion q1 = KeyRot(k1, m, fallback);
            Quaternion q2 = KeyRot(k2, m, fallback);
            Quaternion q0 = has0 ? KeyRot(keys[i - 1], m, fallback) : q1;
            Quaternion q3 = has3 ? KeyRot(keys[i + 2], m, fallback) : q2;
            float n0 = has0 ? k1.frame - keys[i - 1].frame : 0f;
            float n1 = k2.frame - k1.frame;
            float n2 = has3 ? keys[i + 2].frame - k2.frame : 0f;
            float eu = AsiKeyInterpTCB.EaseU(k1, k2, u);
            return AsiKeyInterpTCB.SegQuat(q0, q1, q2, q3, has0, has3, k1, k2, n0, n1, n2, eu);
        }

        public static Vector3 EvalPos(AsiKeyTrack tr, int mask, int rotCh, float f, Vector3 fallback)
        {
            List<AsiKey> keys = tr.keys;
            int n = keys.Count;
            if (n == 0)
                return fallback;
            int i;
            float u;
            int state = Locate(keys, f, out i, out u);
            Vector3 v;
            if (state == Before)
                v = keys[0].pos;
            else if (state == After)
                v = keys[n - 1].pos;
            else
            {
                AsiKey k1 = keys[i];
                AsiKey k2 = keys[i + 1];
                if (tr.mode == AsiInterpMode.Curve)
                {
                    v.x = AsiKeyInterpCurve.Seg(k1.pos.x, k2.pos.x, AsiKeyInterpCurve.GetTan(k1, rotCh), AsiKeyInterpCurve.GetTan(k2, rotCh), k1.frame, k2.frame, f);
                    v.y = AsiKeyInterpCurve.Seg(k1.pos.y, k2.pos.y, AsiKeyInterpCurve.GetTan(k1, rotCh + 1), AsiKeyInterpCurve.GetTan(k2, rotCh + 1), k1.frame, k2.frame, f);
                    v.z = AsiKeyInterpCurve.Seg(k1.pos.z, k2.pos.z, AsiKeyInterpCurve.GetTan(k1, rotCh + 2), AsiKeyInterpCurve.GetTan(k2, rotCh + 2), k1.frame, k2.frame, f);
                }
                else
                {
                    bool has0 = i > 0;
                    bool has3 = i + 2 < n;
                    Vector3 p0 = has0 ? keys[i - 1].pos : k1.pos;
                    Vector3 p3 = has3 ? keys[i + 2].pos : k2.pos;
                    float n0 = has0 ? k1.frame - keys[i - 1].frame : 0f;
                    float n1 = k2.frame - k1.frame;
                    float n2 = has3 ? keys[i + 2].frame - k2.frame : 0f;
                    float eu = AsiKeyInterpTCB.EaseU(k1, k2, u);
                    v = AsiKeyInterpTCB.Seg(p0, k1.pos, k2.pos, p3, has0, has3, k1, k2, n0, n1, n2, eu);
                }
            }
            if ((mask & 1) == 0) v.x = fallback.x;
            if ((mask & 2) == 0) v.y = fallback.y;
            if ((mask & 4) == 0) v.z = fallback.z;
            return v;
        }

        /// <summary>按通道取值（曲线编辑器用）：旋转通道返回欧拉角，位移通道返回位移分量。</summary>
        public static float EvalChannel(AsiKeyTrack tr, int ch, int rotCh, float f, ref Vector3 eulerHint)
        {
            if (ch >= rotCh)
            {
                Vector3 p = EvalPos(tr, 7, rotCh, f, Vector3.zero);
                int a = ch - rotCh;
                return a == 0 ? p.x : (a == 1 ? p.y : p.z);
            }
            int m = ch / 3;
            int axis = ch % 3;
            if (tr.mode == AsiInterpMode.Curve)
            {
                List<AsiKey> keys = tr.keys;
                if (keys.Count == 0)
                    return 0f;
                int i;
                float u;
                int state = Locate(keys, f, out i, out u);
                if (state == Before)
                    return AsiKeyInterpCurve.GetValue(keys[0], ch, rotCh);
                if (state == After)
                    return AsiKeyInterpCurve.GetValue(keys[keys.Count - 1], ch, rotCh);
                AsiKey k1 = keys[i];
                AsiKey k2 = keys[i + 1];
                return AsiKeyInterpCurve.Seg(AsiKeyInterpCurve.GetValue(k1, ch, rotCh), AsiKeyInterpCurve.GetValue(k2, ch, rotCh),
                    AsiKeyInterpCurve.GetTan(k1, ch), AsiKeyInterpCurve.GetTan(k2, ch), k1.frame, k2.frame, f);
            }
            Quaternion q = EvalRot(tr, m, f, Quaternion.identity);
            eulerHint = AsiKeyMath.QuatToEuler(q, tr.rotOrder, eulerHint);
            return axis == 0 ? eulerHint.x : (axis == 1 ? eulerHint.y : eulerHint.z);
        }
    }
}
