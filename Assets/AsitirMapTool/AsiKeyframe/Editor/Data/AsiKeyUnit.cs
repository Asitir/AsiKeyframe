using System;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>单位种类：人形角色（Biped 结构）、骨骼角色（蒙皮骨骼）、物体（镜头、灯光、道具等，本身就是一根骨骼）。</summary>
    public enum AsiUnitKind { Humanoid = 0, Generic = 1, Object = 2 }

    /// <summary>物体单位的烘焙方式：各自一个 Clip，或合并成工程父级上的一个 Generic Clip。</summary>
    public enum AsiObjectBakeMode { Separate = 0, Merged = 1 }

    /// <summary>
    /// 工程里的一个可 K 帧单位。目标是工程父级下的场景物体，按 GlobalObjectId 加场景路径记录。
    /// 角色单位的单位坐标系是 Animator 的 Transform；物体单位的单位坐标系就是工程父级。
    /// </summary>
    [Serializable]
    public class AsiKeyUnit
    {
        public string name = "";
        public AsiUnitKind kind;
        public string targetId = "";
        public string targetPath = "";
        public Avatar avatar;
        /// <summary>人形单位的质心在盆骨与第一节腰椎之间的位置。</summary>
        public float comRatio = 0.5f;
        /// <summary>「自定义可选单位」筛选下可以在 Scene 里点选。</summary>
        public bool selectable = true;
        /// <summary>控制器可见；不可显时既不画也不能选。</summary>
        public bool visible = true;
        public bool bake = true;
        /// <summary>人形单位烘焙 Humanoid（肌肉）Clip，否则烘焙专属骨骼 Clip。</summary>
        public bool bakeHumanoid = true;
        public string clipName = "";

        public bool IsCharacter
        {
            get { return kind != AsiUnitKind.Object; }
        }
    }
}
