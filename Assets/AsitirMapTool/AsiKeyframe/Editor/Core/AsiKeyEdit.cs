using System.Collections.Generic;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>关键帧写入、排序、模式互转、欧拉过滤、踩踏段连接。</summary>
    public static class AsiKeyEdit
    {
        public static void EnsureArrays(AsiKeyProject p, AsiKeyTrack tr, AsiKey k)
        {
            int m = p.MemberCount(tr);
            if (k.rot == null || k.rot.Length != m)
            {
                Quaternion[] r = new Quaternion[m];
                for (int i = 0; i < m; i++)
                    r[i] = k.rot != null && i < k.rot.Length ? k.rot[i] : Quaternion.identity;
                k.rot = r;
            }
            if (k.euler == null || k.euler.Length != m)
            {
                Vector3[] e = new Vector3[m];
                for (int i = 0; i < m; i++)
                    e[i] = k.euler != null && i < k.euler.Length ? k.euler[i] : AsiKeyMath.QuatToEuler(k.rot[i], tr.rotOrder, Vector3.zero);
                k.euler = e;
            }
            int tc = p.TangentCount(tr);
            if (k.tan == null || k.tan.Length != tc)
            {
                AsiTangent[] t = new AsiTangent[tc];
                for (int i = 0; i < tc; i++)
                    t[i] = k.tan != null && i < k.tan.Length ? k.tan[i] : AsiTangent.Default(AsiTangentType.Auto);
                k.tan = t;
            }
        }

        public static AsiKey GetOrInsertKey(AsiKeyProject p, AsiKeyTrack tr, float frame, out bool created)
        {
            int idx = AsiKeyTrackEval.FindKeyIndex(tr.keys, frame);
            if (idx >= 0)
            {
                created = false;
                AsiKey existing = tr.keys[idx];
                EnsureArrays(p, tr, existing);
                return existing;
            }
            created = true;
            AsiKey k = new AsiKey();
            k.frame = frame;
            EnsureArrays(p, tr, k);
            int insert = 0;
            while (insert < tr.keys.Count && tr.keys[insert].frame < frame)
                insert++;
            if (insert > 0)
            {
                AsiKey prev = tr.keys[insert - 1];
                k.tension = prev.tension;
                k.continuity = prev.continuity;
                k.bias = prev.bias;
                for (int i = 0; i < k.tan.Length && i < prev.tan.Length; i++)
                {
                    AsiTangent t = prev.tan[i];
                    AsiTangent n = AsiTangent.Default(t.inType == AsiTangentType.Fixed ? AsiTangentType.Auto : t.inType);
                    n.outType = t.outType == AsiTangentType.Fixed ? AsiTangentType.Auto : t.outType;
                    n.weighted = t.weighted;
                    k.tan[i] = n;
                }
            }
            tr.keys.Insert(insert, k);
            return k;
        }

        private static Vector3 EulerHint(AsiKeyTrack tr, AsiKey k, int m)
        {
            int idx = tr.keys.IndexOf(k);
            if (idx > 0 && m < tr.keys[idx - 1].euler.Length)
                return tr.keys[idx - 1].euler[m];
            if (idx >= 0 && idx + 1 < tr.keys.Count && m < tr.keys[idx + 1].euler.Length)
                return tr.keys[idx + 1].euler[m];
            return m < k.euler.Length ? k.euler[m] : Vector3.zero;
        }

        public static void WriteRot(AsiKeyTrack tr, AsiKey k, int m, Quaternion q)
        {
            if (m < 0 || m >= k.rot.Length)
                return;
            q = AsiKeyMath.Norm(q);
            k.rot[m] = q;
            k.euler[m] = AsiKeyMath.QuatToEuler(q, tr.rotOrder, EulerHint(tr, k, m));
        }

        public static void SyncRotFromEuler(AsiKeyTrack tr, AsiKey k)
        {
            for (int m = 0; m < k.rot.Length && m < k.euler.Length; m++)
                k.rot[m] = AsiKeyMath.EulerToQuat(k.euler[m], tr.rotOrder);
        }

        public static void SortKeys(AsiKeyTrack tr)
        {
            tr.keys.Sort((a, b) => a.frame.CompareTo(b.frame));
        }

        public static void RecomputeTangents(AsiKeyProject p, AsiKeyTrack tr)
        {
            if (tr.mode != AsiInterpMode.Curve || tr.keys.Count == 0)
                return;
            for (int i = 0; i < tr.keys.Count; i++)
                EnsureArrays(p, tr, tr.keys[i]);
            int rotCh = p.MemberCount(tr) * 3;
            for (int ch = 0; ch < rotCh; ch++)
                AsiKeyInterpCurve.RecomputeChannel(tr.keys, ch, rotCh);
            int mask = p.PosMask(tr);
            for (int a = 0; a < 3; a++)
            {
                if ((mask & (1 << a)) != 0)
                    AsiKeyInterpCurve.RecomputeChannel(tr.keys, rotCh + a, rotCh);
            }
        }

        /// <summary>写入后的统一收尾：排序、固定关键帧跟随上一帧的绑定、踩踏段连接、曲线切线重算。</summary>
        public static void AfterEdit(AsiKeyProject p, AsiKeyTrack tr, int layer, AsiKey edited)
        {
            SortKeys(tr);
            if (tr.kind == AsiTrackKind.Chain && layer == 0)
            {
                AsiKeySession session = AsiKeySession.Current;
                if (session != null && session.project == p && session.Valid)
                    session.SyncPlantedBindings(tr);
                EnforceJoins(p, tr, edited != null ? tr.keys.IndexOf(edited) : -1);
            }
            RecomputeTangents(p, tr);
        }

        /// <summary>
        /// 连续的踩踏关键帧共用一个轴心锚点。改动了段中某个关键帧的位置时，把位移差加到段首，整段一起挪。
        /// </summary>
        public static void EnforceJoins(AsiKeyProject p, AsiKeyTrack tr, int edited)
        {
            if (tr.target < 0 || tr.target >= p.chains.Count)
                return;
            AsiKeyChain chain = p.chains[tr.target];
            List<AsiKey> keys = tr.keys;
            if (edited > 0 && edited < keys.Count)
            {
                int s = edited;
                while (s > 0 && AsiKeyEvaluator.Joined(keys[s - 1], keys[s]))
                    s--;
                if (s != edited)
                {
                    Vector3 enforced = keys[s].effPos;
                    for (int j = s + 1; j <= edited; j++)
                    {
                        Vector3 piv = p.PivotOf(chain, keys[j - 1].pivot);
                        Vector3 anchor = enforced + keys[j - 1].effRot * piv;
                        enforced = anchor - keys[j].effRot * piv;
                    }
                    keys[s].effPos += keys[edited].effPos - enforced;
                }
            }
            for (int j = 1; j < keys.Count; j++)
            {
                if (!AsiKeyEvaluator.Joined(keys[j - 1], keys[j]))
                    continue;
                Vector3 piv = p.PivotOf(chain, keys[j - 1].pivot);
                Vector3 anchor = keys[j - 1].effPos + keys[j - 1].effRot * piv;
                keys[j].effPos = anchor - keys[j].effRot * piv;
            }
        }

        /// <summary>欧拉过滤：消除相邻关键帧之间的 ±180° 跳变，姿势不变。</summary>
        public static void EulerFilter(AsiKeyProject p, AsiKeyTrack tr)
        {
            for (int i = 0; i < tr.keys.Count; i++)
            {
                EnsureArrays(p, tr, tr.keys[i]);
                if (tr.mode == AsiInterpMode.Curve)
                    SyncRotFromEuler(tr, tr.keys[i]);
            }
            RebuildEulerFromRot(p, tr);
            RecomputeTangents(p, tr);
        }

        /// <summary>以四元数为准按当前旋转顺序重新展开欧拉角，相邻关键帧取最近的圈数。</summary>
        public static void RebuildEulerFromRot(AsiKeyProject p, AsiKeyTrack tr)
        {
            int m = p.MemberCount(tr);
            for (int i = 0; i < tr.keys.Count; i++)
            {
                AsiKey k = tr.keys[i];
                EnsureArrays(p, tr, k);
                for (int j = 0; j < m; j++)
                {
                    Quaternion q = AsiKeyMath.Norm(k.rot[j]);
                    Vector3 hint = i > 0 ? tr.keys[i - 1].euler[j] : AsiKeyMath.QuatToEuler(q, tr.rotOrder, Vector3.zero);
                    k.euler[j] = AsiKeyMath.QuatToEuler(q, tr.rotOrder, hint);
                }
            }
        }

        public static void ConvertMode(AsiKeyProject p, AsiKeyTrack tr, AsiInterpMode mode)
        {
            if (tr.mode == mode)
                return;
            for (int i = 0; i < tr.keys.Count; i++)
                EnsureArrays(p, tr, tr.keys[i]);
            if (mode == AsiInterpMode.Curve)
            {
                RebuildEulerFromRot(p, tr);
                for (int i = 0; i < tr.keys.Count; i++)
                {
                    AsiKey k = tr.keys[i];
                    for (int t = 0; t < k.tan.Length; t++)
                        k.tan[t] = AsiTangent.Default(AsiTangentType.Auto);
                }
                tr.mode = mode;
                RecomputeTangents(p, tr);
            }
            else
            {
                for (int i = 0; i < tr.keys.Count; i++)
                    SyncRotFromEuler(tr, tr.keys[i]);
                tr.mode = mode;
            }
        }

        public static void SetRotOrder(AsiKeyProject p, AsiKeyTrack tr, AsiRotOrder order)
        {
            if (tr.rotOrder == order)
                return;
            for (int i = 0; i < tr.keys.Count; i++)
            {
                EnsureArrays(p, tr, tr.keys[i]);
                if (tr.mode == AsiInterpMode.Curve)
                    SyncRotFromEuler(tr, tr.keys[i]);
            }
            tr.rotOrder = order;
            RebuildEulerFromRot(p, tr);
            RecomputeTangents(p, tr);
        }

        /// <summary>把一组关键帧整体平移 delta 帧。同轨道上被覆盖的旧关键帧会被删除。</summary>
        public static void MoveKeys(AsiKeyProject p, AsiKeyTrack tr, List<AsiKey> moving, float delta, int layer)
        {
            if (moving.Count == 0 || Mathf.Abs(delta) < 1e-5f)
                return;
            for (int i = 0; i < moving.Count; i++)
                moving[i].frame = Mathf.Round((moving[i].frame + delta) * 100f) / 100f;
            for (int i = tr.keys.Count - 1; i >= 0; i--)
            {
                AsiKey k = tr.keys[i];
                if (moving.Contains(k))
                    continue;
                for (int j = 0; j < moving.Count; j++)
                {
                    if (Mathf.Abs(moving[j].frame - k.frame) < 0.01f)
                    {
                        tr.keys.RemoveAt(i);
                        break;
                    }
                }
            }
            AfterEdit(p, tr, layer, null);
        }

        public static void ScaleKeys(AsiKeyProject p, AsiKeyTrack tr, List<AsiKey> keys, float pivot, float factor, int layer)
        {
            for (int i = 0; i < keys.Count; i++)
                keys[i].frame = Mathf.Round((pivot + (keys[i].frame - pivot) * factor) * 100f) / 100f;
            SortKeys(tr);
            for (int i = tr.keys.Count - 1; i >= 1; i--)
            {
                if (Mathf.Abs(tr.keys[i].frame - tr.keys[i - 1].frame) < 0.01f)
                    tr.keys.RemoveAt(keys.Contains(tr.keys[i - 1]) && !keys.Contains(tr.keys[i]) ? i : i - 1);
            }
            AfterEdit(p, tr, layer, null);
        }

        /// <summary>
        /// 把 keys 复制到 pivot + (帧 - pivot) * factor + delta。落点上已有的关键帧被替换，复制出的互相重合时保留后一个。返回复制出的关键帧。
        /// </summary>
        public static List<AsiKey> CopyKeys(AsiKeyProject p, AsiKeyTrack tr, List<AsiKey> keys, float pivot, float factor, float delta, int layer)
        {
            List<AsiKey> copies = new List<AsiKey>();
            for (int i = 0; i < keys.Count; i++)
            {
                AsiKey c = keys[i].Clone();
                c.frame = Mathf.Round((pivot + (keys[i].frame - pivot) * factor + delta) * 100f) / 100f;
                for (int j = tr.keys.Count - 1; j >= 0; j--)
                {
                    if (Mathf.Abs(tr.keys[j].frame - c.frame) < 0.01f)
                    {
                        copies.Remove(tr.keys[j]);
                        tr.keys.RemoveAt(j);
                    }
                }
                tr.keys.Add(c);
                copies.Add(c);
            }
            AfterEdit(p, tr, layer, null);
            return copies;
        }

        public static void ApplyTCB(AsiKey k, float tension, float continuity, float bias, float easeTo, float easeFrom)
        {
            k.tension = Mathf.Clamp(tension, 0f, 50f);
            k.continuity = Mathf.Clamp(continuity, 0f, 50f);
            k.bias = Mathf.Clamp(bias, 0f, 50f);
            k.easeTo = Mathf.Clamp(easeTo, 0f, 50f);
            k.easeFrom = Mathf.Clamp(easeFrom, 0f, 50f);
        }

        public static void SetTangentType(AsiKey k, int ch, bool setIn, bool setOut, AsiTangentType type)
        {
            if (k.tan == null)
                return;
            int from = ch < 0 ? 0 : ch;
            int to = ch < 0 ? k.tan.Length - 1 : ch;
            for (int c = from; c <= to && c < k.tan.Length; c++)
            {
                AsiTangent t = k.tan[c];
                if (setIn && type != AsiTangentType.Stepped) t.inType = type;
                if (setOut) t.outType = type;
                k.tan[c] = t;
            }
        }
    }
}
