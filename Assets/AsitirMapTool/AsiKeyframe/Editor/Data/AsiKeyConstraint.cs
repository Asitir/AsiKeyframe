using System;
using System.Collections.Generic;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>仿 3ds Max 的约束：位置、旋转（方向）、注视、链接。</summary>
    public enum AsiConstraintKind { Position = 0, Orientation = 1, LookAt = 2, Link = 3 }

    /// <summary>注视约束的上方向：工程父级的 Y、某个物体的某根轴、不指定（从骨骼自身朝向做最小旋转）。</summary>
    public enum AsiLookUp { World = 0, Object = 1, None = 2 }

    /// <summary>约束目标：工程里的骨骼（下标），或不属于任何单位的场景物体（编码引用），二选一。</summary>
    [Serializable]
    public class AsiConstraintTarget
    {
        public int bone = -1;
        public string obj = "";
        public float weight = 1f;
    }

    /// <summary>链接约束的一个条目：从 frame 起父级换成 bone / obj（都为空时是世界，即工程父级）。偏移是被约束骨骼在父级空间里的位姿。</summary>
    [Serializable]
    public class AsiLinkEntry
    {
        public float frame;
        public int bone = -1;
        public string obj = "";
        public Vector3 offPos;
        public Quaternion offRot = Quaternion.identity;
    }

    /// <summary>
    /// 一个约束。所有位姿都在工程父级空间。整体影响度可以 K 帧（<see cref="AsiTrackKind.Constraint"/> 轨道，pos.x 存值），没有关键帧时用 weight。
    /// 位置约束：目标位置加权平均 + offPos。旋转约束：目标旋转加权平均 × offRot。
    /// 注视约束：aimAxis 指向目标的加权位置，upAxis 尽量朝上方向，再 × offRot。链接约束：当前条目的父级位姿 × 条目偏移。
    /// </summary>
    [Serializable]
    public class AsiKeyConstraint
    {
        public string name = "";
        public AsiConstraintKind kind;
        /// <summary>被约束的骨骼。</summary>
        public int bone = -1;
        public bool enabled = true;
        /// <summary>没有影响度关键帧时的影响度。</summary>
        public float weight = 1f;
        public List<AsiConstraintTarget> targets = new List<AsiConstraintTarget>();
        /// <summary>添加目标时抓取偏移，让骨骼保持原位；关闭时偏移清零。</summary>
        public bool keepOffset = true;
        public Vector3 offPos;
        public Quaternion offRot = Quaternion.identity;

        /// <summary>注视：骨骼局部的瞄准轴与上方向轴，0..5 = +X +Y +Z -X -Y -Z。</summary>
        public int aimAxis = 2;
        public int upAxis = 1;
        public AsiLookUp upMode;
        public int upBone = -1;
        public string upObj = "";
        /// <summary>上方向物体的轴，0..5 同上。</summary>
        public int upTargetAxis = 1;

        /// <summary>链接：按帧排序。</summary>
        public List<AsiLinkEntry> links = new List<AsiLinkEntry>();

        public static Vector3 AxisVector(int axis)
        {
            switch (axis)
            {
                case 0: return Vector3.right;
                case 1: return Vector3.up;
                case 3: return Vector3.left;
                case 4: return Vector3.down;
                case 5: return Vector3.back;
                default: return Vector3.forward;
            }
        }
    }
}
