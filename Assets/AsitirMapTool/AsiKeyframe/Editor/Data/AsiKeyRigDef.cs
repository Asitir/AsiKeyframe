using System;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 控制器外形。外形坐标系由骨骼的 frame 加上 euler 得到：+Z 沿骨骼方向，XY 为截面。
    /// 方块从关节起沿 +Z 延伸 length；圆环与方形线框画在 XY 平面，offset 可沿 Z 推到骨骼中段。
    /// </summary>
    [Serializable]
    public class AsiKeyShape
    {
        public AsiShapeKind kind = AsiShapeKind.BipBox;
        public float width = 0.06f;
        public float height = 0.06f;
        public float length = 0.2f;
        public float radiusX = 0.08f;
        public float radiusY = 0.08f;
        public Vector3 offset;
        public Vector3 euler;
        public Color color = Color.white;
        public Mesh mesh;
        public Vector3 meshScale = Vector3.one;
        public bool visible = true;

        public AsiKeyShape Clone()
        {
            return (AsiKeyShape)MemberwiseClone();
        }
    }

    [Serializable]
    public class AsiKeyBone
    {
        public string name = "";
        /// <summary>所属单位（<see cref="AsiKeyProject.units"/> 下标）。</summary>
        public int unit;
        /// <summary>相对单位目标（Animator 根或物体本身）的路径；质心为空，物体单位的主骨骼也为空。</summary>
        public string path = "";
        public int humanBone = -1;
        public int parent = -1;
        public bool isCom;
        public bool animatePosition;
        /// <summary>指尖轴心：末节末端的子级，不显示、点不中，只在单独选中时让手指 IK 链绕它转。</summary>
        public bool fingerTip;
        public AsiBoneSide side;
        public int mirror = -1;

        /// <summary>
        /// 关节位置挂在 anchor 骨骼上（模型里的父骨骼），旋转仍跟随 parent。
        /// Humanoid 按 Biped 独立朝向这样设置腰椎、大腿、脚、上臂和头：旋转只跟随质心，关节仍连在模型父骨骼上。记录位移时不生效。
        /// </summary>
        public bool anchored;
        public int anchor = -1;
        /// <summary>绑定姿势下关节在 anchor 局部空间中的位置。</summary>
        public Vector3 anchorPos;

        /// <summary>绑定姿势下相对父控制器（父级为 -1 时相对单位坐标系）的局部变换。</summary>
        public Vector3 restPos;
        public Quaternion restRot = Quaternion.identity;
        /// <summary>
        /// 「重置选中骨骼」复原到的原始姿势（与 restPos/restRot 同一空间，相对旋转父级）。hasOrig 为 false 时用绑定姿势；
        /// 只给重置用，不影响 IK、铰链、镜像。
        /// </summary>
        public bool hasOrig;
        public Vector3 origPos;
        public Quaternion origRot = Quaternion.identity;
        /// <summary>绑定姿势下的单位空间变换，用于镜像和增删骨骼时换算。</summary>
        public Vector3 rigRestPos;
        public Quaternion rigRestRot = Quaternion.identity;

        /// <summary>控制器局部空间中的骨骼方向与长度。</summary>
        public Vector3 axis = Vector3.forward;
        public float boneLength = 0.1f;
        /// <summary>外形基准朝向（控制器局部空间），+Z 沿骨骼。</summary>
        public Quaternion frame = Quaternion.identity;

        public AsiKeyShape shape = new AsiKeyShape();
    }

    [Serializable]
    public class AsiKeyChain
    {
        public string name = "";
        /// <summary>根 → 末端，后一个成员的关节连在前一个成员上（<see cref="AsiKeyProject.LinkParent"/>）。</summary>
        public int[] bones = new int[0];
        public AsiBoneSide side;
        public bool auto;
        /// <summary>轴心下标 -1：末端第一个子级的关节（脚趾根），位置由 <see cref="AsiKeyProject.PivotOf"/> 按骨骼结构算。</summary>
        public const int ChildPivot = -1;

        /// <summary>末端控制器局部空间的轴心点。下标 0 恒为末端原点。</summary>
        public Vector3[] pivots = { Vector3.zero };
        public string[] pivotNames = { "末端原点" };
        /// <summary>角色根空间中，肢体伸直时用的弯曲方向。三成员链的铰链轴也由它决定。</summary>
        public Vector3 poleHint = Vector3.forward;

        /// <summary>三成员链：编辑时中间关节（膝/肘）只绕铰链轴弯，弯曲角（0 为伸直）限制在 bendMin..bendMax。</summary>
        public bool hinge;
        public float bendMin;
        public float bendMax = 160f;

        public int Effector
        {
            get { return bones.Length > 0 ? bones[bones.Length - 1] : -1; }
        }

        public int Root
        {
            get { return bones.Length > 0 ? bones[0] : -1; }
        }

        public Vector3 Pivot(int index)
        {
            if (pivots == null || index < 0 || index >= pivots.Length)
                return Vector3.zero;
            return pivots[index];
        }

        public bool Contains(int bone)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == bone)
                    return true;
            }
            return false;
        }

        public int MemberIndex(int bone)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == bone)
                    return i;
            }
            return -1;
        }
    }
}
