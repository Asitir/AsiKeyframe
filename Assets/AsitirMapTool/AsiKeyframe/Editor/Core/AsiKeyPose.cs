using System.Collections.Generic;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>IK 目标：末端原点与朝向，位于 space 指定的坐标系中。</summary>
    public struct AsiChainTarget
    {
        public AsiIKSpace space;
        public int bone;
        public string obj;
        public Vector3 pos;
        public Quaternion rot;
    }

    /// <summary>某一帧上一条 IK 链的状态。空间不同的两个关键帧之间，a、b 各自换算到角色根空间后按 mix 混合。</summary>
    public struct AsiChainState
    {
        public bool valid;
        public float blend;
        public float swivel;
        public int pivot;
        public bool join;
        public AsiKeyType type;
        public AsiChainTarget a;
        public AsiChainTarget b;
        public float mix;
        /// <summary>链根在基础层关键帧插值出的局部旋转，稳定膝/肘零平面的参考；hasRef 为 false 时用当前 FK。</summary>
        public Quaternion refRot;
        public bool hasRef;
        /// <summary>IK 拖动开始时的「根→末端」方向与零平面（链根父级空间），拖动中零平面由它随方向做最小旋转；按关键帧求值时没有。</summary>
        public Vector3 dragDir;
        public Vector3 dragPole;
        public bool hasDrag;
    }

    /// <summary>
    /// 求值结果。所有世界量都在工程父级空间（不含缩放，没设父级时是世界空间）。
    /// 父级为 -1 的骨骼相对所属单位的坐标系（unitPos / unitRot，求解时从上下文读取）。
    /// fk/fkPos 是 IK 之前的局部姿势；rot/pos 是最终可见的局部姿势。
    /// postRot/postPos 是关闭「保持底层 IK」的层在 IK 之后叠加的增量；actRot/actPos 是其中正在编辑的那一层。
    /// conWeight 是各约束在这一帧的影响度（叠加层已加上）。
    /// </summary>
    public class AsiKeyPose
    {
        public int count = -1;
        public int chainCount = -1;
        public int stamp = int.MinValue;
        public float frame;
        public Quaternion[] fk;
        public Vector3[] fkPos;
        public Quaternion[] rot;
        public Vector3[] pos;
        public Vector3[] wpos;
        public Quaternion[] wrot;
        public Quaternion[] postRot;
        public Vector3[] postPos;
        public Quaternion[] actRot;
        public Vector3[] actPos;
        public AsiChainState[] chains;
        public int[] order;
        public Vector3[] unitPos = new Vector3[0];
        public Quaternion[] unitRot = new Quaternion[0];
        public float[] conWeight = new float[0];

        public Vector3 UnitPos(int u)
        {
            return u >= 0 && u < unitPos.Length ? unitPos[u] : Vector3.zero;
        }

        public Quaternion UnitRot(int u)
        {
            return u >= 0 && u < unitRot.Length ? unitRot[u] : Quaternion.identity;
        }

        /// <summary>旋转父级的世界位姿；没有父级时是所属单位的坐标系。</summary>
        public void ParentFrame(AsiKeyProject p, int i, out Vector3 ppos, out Quaternion prot)
        {
            AsiKeyBone b = p.bones[i];
            if (b.parent >= 0)
            {
                ppos = wpos[b.parent];
                prot = wrot[b.parent];
                return;
            }
            ppos = UnitPos(b.unit);
            prot = UnitRot(b.unit);
        }

        public Quaternion ParentRot(AsiKeyProject p, int i)
        {
            AsiKeyBone b = p.bones[i];
            return b.parent >= 0 ? wrot[b.parent] : UnitRot(b.unit);
        }

        /// <summary>从上下文读入各单位坐标系。上下文为空或单位数不够时剩下的取单位矩阵。</summary>
        public void LoadUnits(AsiKeyContext ctx)
        {
            for (int u = 0; u < unitPos.Length; u++)
            {
                bool has = ctx != null && u < ctx.unitPos.Length;
                unitPos[u] = has ? ctx.unitPos[u] : Vector3.zero;
                unitRot[u] = has ? ctx.unitRot[u] : Quaternion.identity;
            }
        }

        public void Ensure(AsiKeyProject p)
        {
            int n = p.bones.Count;
            int c = p.chains.Count;
            int units = p.units.Count;
            if (unitPos.Length != units)
            {
                unitPos = new Vector3[units];
                unitRot = new Quaternion[units];
                for (int u = 0; u < units; u++)
                    unitRot[u] = Quaternion.identity;
            }
            if (conWeight.Length != p.constraints.Count)
                conWeight = new float[p.constraints.Count];
            if (n != count)
            {
                count = n;
                fk = new Quaternion[n];
                fkPos = new Vector3[n];
                rot = new Quaternion[n];
                pos = new Vector3[n];
                wpos = new Vector3[n];
                wrot = new Quaternion[n];
                postRot = new Quaternion[n];
                postPos = new Vector3[n];
                actRot = new Quaternion[n];
                actPos = new Vector3[n];
                stamp = int.MinValue;
            }
            if (c != chainCount)
            {
                chainCount = c;
                chains = new AsiChainState[c];
            }
            if (stamp != p.structureStamp || order == null || order.Length != n)
            {
                order = p.BuildOrder();
                stamp = p.structureStamp;
            }
        }

        public void ResetToRest(AsiKeyProject p)
        {
            for (int i = 0; i < count; i++)
            {
                AsiKeyBone b = p.bones[i];
                fk[i] = b.restRot;
                fkPos[i] = b.restPos;
                postRot[i] = Quaternion.identity;
                postPos[i] = Vector3.zero;
                actRot[i] = Quaternion.identity;
                actPos[i] = Vector3.zero;
            }
            for (int c = 0; c < chainCount; c++)
                chains[c] = default(AsiChainState);
            for (int k = 0; k < conWeight.Length && k < p.constraints.Count; k++)
                conWeight[k] = p.constraints[k].weight;
        }

        public void ComputeWorld(AsiKeyProject p)
        {
            for (int o = 0; o < order.Length; o++)
            {
                int i = order[o];
                int parent = p.bones[i].parent;
                if (parent < 0)
                {
                    int u = p.bones[i].unit;
                    Quaternion ur = UnitRot(u);
                    wpos[i] = UnitPos(u) + ur * pos[i];
                    wrot[i] = ur * rot[i];
                }
                else
                {
                    wrot[i] = wrot[parent] * rot[i];
                    int a = p.AnchorOf(i);
                    if (a >= 0)
                    {
                        wpos[i] = wpos[a] + wrot[a] * p.bones[i].anchorPos;
                        pos[i] = Quaternion.Inverse(wrot[parent]) * (wpos[i] - wpos[parent]);
                    }
                    else
                    {
                        wpos[i] = wpos[parent] + wrot[parent] * pos[i];
                    }
                }
            }
        }

        public void CopyFrom(AsiKeyPose o)
        {
            frame = o.frame;
            if (unitPos.Length != o.unitPos.Length)
            {
                unitPos = (Vector3[])o.unitPos.Clone();
                unitRot = (Quaternion[])o.unitRot.Clone();
            }
            else
            {
                System.Array.Copy(o.unitPos, unitPos, unitPos.Length);
                System.Array.Copy(o.unitRot, unitRot, unitRot.Length);
            }
            if (conWeight.Length != o.conWeight.Length)
                conWeight = (float[])o.conWeight.Clone();
            else
                System.Array.Copy(o.conWeight, conWeight, conWeight.Length);
            if (o.count != count || o.chainCount != chainCount)
            {
                count = o.count;
                chainCount = o.chainCount;
                fk = (Quaternion[])o.fk.Clone();
                fkPos = (Vector3[])o.fkPos.Clone();
                rot = (Quaternion[])o.rot.Clone();
                pos = (Vector3[])o.pos.Clone();
                wpos = (Vector3[])o.wpos.Clone();
                wrot = (Quaternion[])o.wrot.Clone();
                postRot = (Quaternion[])o.postRot.Clone();
                postPos = (Vector3[])o.postPos.Clone();
                actRot = (Quaternion[])o.actRot.Clone();
                actPos = (Vector3[])o.actPos.Clone();
                chains = (AsiChainState[])o.chains.Clone();
                order = (int[])o.order.Clone();
                stamp = o.stamp;
                return;
            }
            System.Array.Copy(o.fk, fk, count);
            System.Array.Copy(o.fkPos, fkPos, count);
            System.Array.Copy(o.rot, rot, count);
            System.Array.Copy(o.pos, pos, count);
            System.Array.Copy(o.wpos, wpos, count);
            System.Array.Copy(o.wrot, wrot, count);
            System.Array.Copy(o.postRot, postRot, count);
            System.Array.Copy(o.postPos, postPos, count);
            System.Array.Copy(o.actRot, actRot, count);
            System.Array.Copy(o.actPos, actPos, count);
            System.Array.Copy(o.chains, chains, chainCount);
            order = o.order;
            stamp = o.stamp;
        }
    }

    /// <summary>
    /// 求值上下文：工程父级在场景中的位姿（rig*，姿势空间 → 世界），各单位坐标系（姿势空间），以及场景物体引用的解析缓存。
    /// </summary>
    public class AsiKeyContext
    {
        public Vector3 rigPos;
        public Quaternion rigRot = Quaternion.identity;
        public Quaternion rigInv = Quaternion.identity;
        public Vector3[] unitPos = new Vector3[0];
        public Quaternion[] unitRot = new Quaternion[0];
        private readonly Dictionary<string, Transform> _objects = new Dictionary<string, Transform>();

        public void SetRig(Vector3 position, Quaternion rotation)
        {
            rigPos = position;
            rigRot = rotation;
            rigInv = Quaternion.Inverse(rotation);
        }

        public void SetUnitCount(int n)
        {
            if (unitPos.Length == n)
                return;
            unitPos = new Vector3[n];
            unitRot = new Quaternion[n];
            for (int u = 0; u < n; u++)
                unitRot[u] = Quaternion.identity;
        }

        /// <summary>按世界位姿设定单位坐标系。</summary>
        public void SetUnitWorld(int u, Vector3 worldPos, Quaternion worldRot)
        {
            unitPos[u] = rigInv * (worldPos - rigPos);
            unitRot[u] = AsiKeyMath.Norm(rigInv * worldRot);
        }

        public void SetUnitIdentity(int u)
        {
            unitPos[u] = Vector3.zero;
            unitRot[u] = Quaternion.identity;
        }

        public void ClearCache()
        {
            _objects.Clear();
        }

        public Transform Resolve(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                return null;
            Transform t;
            if (_objects.TryGetValue(encoded, out t) && t != null)
                return t;
            t = AsiKeyUtil.DecodeObjectRef(encoded);
            _objects[encoded] = t;
            return t;
        }

        public bool ObjectFrame(string encoded, out Vector3 pos, out Quaternion rot)
        {
            Transform t = Resolve(encoded);
            if (t == null)
            {
                pos = Vector3.zero;
                rot = Quaternion.identity;
                return false;
            }
            pos = rigInv * (t.position - rigPos);
            rot = rigInv * t.rotation;
            return true;
        }

        public Vector3 ToWorldPos(Vector3 rigSpace)
        {
            return rigPos + rigRot * rigSpace;
        }

        public Quaternion ToWorldRot(Quaternion rigSpace)
        {
            return rigRot * rigSpace;
        }

        public Vector3 FromWorldPos(Vector3 world)
        {
            return rigInv * (world - rigPos);
        }

        public Quaternion FromWorldRot(Quaternion world)
        {
            return AsiKeyMath.Norm(rigInv * world);
        }

        public Vector3 UnitPos(int u)
        {
            return u >= 0 && u < unitPos.Length ? unitPos[u] : Vector3.zero;
        }

        public Quaternion UnitRot(int u)
        {
            return u >= 0 && u < unitRot.Length ? unitRot[u] : Quaternion.identity;
        }
    }
}
