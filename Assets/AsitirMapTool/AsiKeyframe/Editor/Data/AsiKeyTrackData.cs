using System;
using System.Collections.Generic;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>曲线模式下一个通道的入/出切线。斜率单位：值/帧；权重为相邻段时长的比例。</summary>
    [Serializable]
    public struct AsiTangent
    {
        public AsiTangentType inType;
        public AsiTangentType outType;
        public float inSlope;
        public float outSlope;
        public float inWeight;
        public float outWeight;
        public bool weighted;
        public bool broken;

        public static AsiTangent Default(AsiTangentType type)
        {
            AsiTangent t = new AsiTangent();
            t.inType = type;
            t.outType = type;
            t.inWeight = 1f / 3f;
            t.outWeight = 1f / 3f;
            return t;
        }
    }

    /// <summary>
    /// 一个关键帧。单骨轨道 rot 只有 1 个元素，IK 链轨道每个成员 1 个，质心水平/垂直轨道为空。
    /// TCB 模式以 rot 为准，曲线模式以 euler 为准，两者写入时保持同步。
    /// 链在叠加层里只记 rot 增量，IK 参数只在基础层生效。
    /// </summary>
    [Serializable]
    public class AsiKey
    {
        public float frame;
        public AsiKeyType type = AsiKeyType.Set;
        public Quaternion[] rot = new Quaternion[0];
        public Vector3[] euler = new Vector3[0];
        public Vector3 pos;

        public float tension = 25f;
        public float continuity = 25f;
        public float bias = 25f;
        public float easeTo;
        public float easeFrom;

        /// <summary>下标：成员 m 的欧拉轴 a 为 m*3+a；位移轴 a 为 成员数*3+a。</summary>
        public AsiTangent[] tan = new AsiTangent[0];

        public float ikBlend;
        public bool joinToPrev;
        public AsiIKSpace ikSpace;
        public int ikBone = -1;
        public string ikObject = "";
        public int pivot;
        public float swivel;
        /// <summary>末端原点与朝向，位于 ikSpace 对应的坐标系中。</summary>
        public Vector3 effPos;
        public Quaternion effRot = Quaternion.identity;

        public AsiKey Clone()
        {
            AsiKey k = (AsiKey)MemberwiseClone();
            k.rot = (Quaternion[])rot.Clone();
            k.euler = (Vector3[])euler.Clone();
            k.tan = (AsiTangent[])tan.Clone();
            return k;
        }

        public void CopyIKFrom(AsiKey o)
        {
            ikBlend = o.ikBlend;
            joinToPrev = o.joinToPrev;
            ikSpace = o.ikSpace;
            ikBone = o.ikBone;
            ikObject = o.ikObject;
            pivot = o.pivot;
            swivel = o.swivel;
            effPos = o.effPos;
            effRot = o.effRot;
        }

        public bool SameSpace(AsiKey o)
        {
            if (ikSpace != o.ikSpace)
                return false;
            if (ikSpace != AsiIKSpace.Object)
                return true;
            return ikBone == o.ikBone && ikObject == o.ikObject;
        }
    }

    [Serializable]
    public class AsiKeyTrack
    {
        public AsiTrackKind kind;
        /// <summary>骨骼轨道与质心轨道为骨骼下标，IK 链轨道为链下标。</summary>
        public int target = -1;
        public AsiInterpMode mode;
        public AsiRotOrder rotOrder;
        public List<AsiKey> keys = new List<AsiKey>();
    }

    [Serializable]
    public class AsiKeyLayer
    {
        public string name = "基础层";
        public float weight = 1f;
        public bool mute;
        /// <summary>开启时本层增量在 IK 之前叠加，叠加后仍按基础层 IK 目标求解，踩踏的脚不会滑。</summary>
        public bool keepBaseIK = true;
        /// <summary>开启时，本层打过关键帧的骨骼替换它上面各层的姿势，没打过的骨骼保持上面的结果。</summary>
        public bool overwrite;
        /// <summary>在时间轴上显示这一层的权重关键帧。</summary>
        public bool showWeight;
        public List<AsiKeyTrack> tracks = new List<AsiKeyTrack>();
    }
}
