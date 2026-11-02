using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>Biped 四种关键帧：注册 = Set Key，固定 = Planted，滑动 = Sliding，自由 = Free。</summary>
    public enum AsiKeyType { Set = 0, Planted = 1, Sliding = 2, Free = 3 }

    public enum AsiInterpMode { TCB = 0, Curve = 1 }

    /// <summary>IK 目标所在空间。World 指链所属单位的根空间，角色整体移动时目标跟着走。</summary>
    public enum AsiIKSpace { Body = 0, World = 1, Object = 2 }

    public enum AsiShapeKind { BipBox = 0, Ring = 1, Rect = 2, Mesh = 3, Hidden = 4 }

    /// <summary>Scene 里骨骼的统一显示样式。Shape 按各骨骼自己的外形，其余只改绘制和点选，不改外形数据；质心总按自己的外形。</summary>
    public enum AsiBoneDisplay { Shape = 0, Box = 1, Ring = 2, Rect = 3, Octahedron = 4, Stick = 5 }

    public enum AsiTangentType { Auto = 0, Spline = 1, Clamped = 2, Linear = 3, Flat = 4, Stepped = 5, Plateau = 6, Fixed = 7 }

    /// <summary>Maya 旋转顺序：XYZ 表示先绕 X、再绕 Y、最后绕 Z。</summary>
    public enum AsiRotOrder { XYZ = 0, YZX = 1, ZXY = 2, XZY = 3, YXZ = 4, ZYX = 5 }

    public enum AsiBoneSide { Center = 0, Left = 1, Right = 2 }

    /// <summary>Constraint：约束影响度轨道，target 是约束下标，pos.x 存影响度。</summary>
    public enum AsiTrackKind { ComHorizontal = 0, ComVertical = 1, ComRotation = 2, Bone = 3, Chain = 4, Constraint = 5, LayerWeight = 6 }

    /// <summary>
    /// Scene 里的选择筛选。None = Unity 默认点选；其余模式由 K帧接管点选/框选，只能选到通过筛选的控制器，点空白处清空选择。
    /// Bones = 角色（人形 / 骨骼）单位的控制器；Units = 全部单位；OneUnit = 工程的 selectFilterUnit；Custom = 单位列表里勾了「可选」的单位。
    /// </summary>
    public enum AsiSelectFilter { None = 0, Bones = 1, Units = 2, OneUnit = 3, Custom = 4 }

    public static class AsiKeyLabels
    {
        public static readonly string[] KeyType = { "注册", "固定", "滑动", "自由" };
        public static readonly string[] Interp = { "TCB", "曲线" };
        public static readonly string[] Space = { "身体", "世界", "物体" };
        public static readonly string[] Shape = { "方块", "圆环/椭圆", "方形线框", "自定义 Mesh", "隐藏" };
        public static readonly string[] BoneDisplay = { "按外形设定", "方块", "圆环", "方形线框", "八面体", "棍状" };
        public static readonly string[] Tangent = { "自动", "样条", "钳制", "线性", "平直", "阶梯", "高原", "固定" };
        public static readonly string[] RotOrder = { "XYZ", "YZX", "ZXY", "XZY", "YXZ", "ZYX" };
        public static readonly string[] Side = { "中", "左", "右" };
        public static readonly string[] UnitKind = { "人形", "骨骼", "物体" };
        public static readonly string[] Constraint = { "位置约束", "旋转约束", "注视约束", "链接约束" };
        public static readonly string[] Axis6 = { "+X", "+Y", "+Z", "-X", "-Y", "-Z" };
        public static readonly string[] LookUp = { "工程父级 Y", "物体的轴", "不指定" };

        public static Color KeyColor(AsiKeyType type)
        {
            switch (type)
            {
                case AsiKeyType.Planted: return new Color(1f, 0.42f, 0.2f);
                case AsiKeyType.Sliding: return new Color(1f, 0.85f, 0.2f);
                case AsiKeyType.Free: return new Color(0.35f, 0.62f, 1f);
                default: return new Color(0.86f, 0.86f, 0.86f);
            }
        }

        /// <summary>时间轴关键帧通道：质心水平/垂直/旋转，其余按关键帧类型。同一帧的通道叠成一个色块，按这个顺序从上到下。</summary>
        public static readonly string[] Channel = { "质心水平", "质心垂直", "质心旋转", "注册", "固定", "滑动", "自由", "约束影响度", "层权重" };
        public const int ChannelCount = 9;

        public static readonly Color ComHColor = new Color(0.95f, 0.35f, 0.85f);
        public static readonly Color ComVColor = new Color(0.25f, 0.85f, 0.95f);
        public static readonly Color ComRColor = new Color(0.4f, 0.9f, 0.3f);
        public static readonly Color ConstraintColor = new Color(0.65f, 0.45f, 1f);
        /// <summary>链接约束的切换关键帧。不是影响度，是「从这一帧起跟哪个对象」。</summary>
        public static readonly Color LinkKeyColor = new Color(1f, 0.78f, 0.25f);
        public static readonly Color LayerWeightColor = new Color(1f, 0.93f, 0.55f);

        public static int ChannelOf(AsiKeyTrack track, AsiKey key)
        {
            switch (track.kind)
            {
                case AsiTrackKind.ComHorizontal: return 0;
                case AsiTrackKind.ComVertical: return 1;
                case AsiTrackKind.ComRotation: return 2;
                case AsiTrackKind.Chain: return 3 + Mathf.Clamp((int)key.type, 0, 3);
                case AsiTrackKind.Constraint: return 7;
                case AsiTrackKind.LayerWeight: return 8;
                default: return 3;
            }
        }

        public static Color ChannelColor(int channel)
        {
            switch (channel)
            {
                case 0: return ComHColor;
                case 1: return ComVColor;
                case 2: return ComRColor;
                case 7: return ConstraintColor;
                case 8: return LayerWeightColor;
                default: return KeyColor((AsiKeyType)Mathf.Clamp(channel - 3, 0, 3));
            }
        }

        public static readonly Color LeftColor = new Color(0.25f, 0.45f, 1f);
        public static readonly Color RightColor = new Color(0.25f, 0.85f, 0.35f);
        public static readonly Color CenterColor = new Color(0.95f, 0.8f, 0.3f);
        public static readonly Color ComColor = new Color(1f, 0.92f, 0.15f);

        public static Color SideColor(AsiBoneSide side)
        {
            if (side == AsiBoneSide.Left) return LeftColor;
            if (side == AsiBoneSide.Right) return RightColor;
            return CenterColor;
        }
    }
}
