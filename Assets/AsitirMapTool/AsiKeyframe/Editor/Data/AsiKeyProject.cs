using System;
using System.Collections.Generic;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// K 帧工程。资产不能直接引用场景物体，工程父级和各单位目标按 GlobalObjectId 加场景路径记录（场景控制器每次打开时重建，不记录）。
    /// 一个工程父级下可以有多个单位（<see cref="units"/>），骨骼是扁平列表，用 <see cref="AsiKeyBone.unit"/> 标记所属单位。
    /// 姿势空间是工程父级空间（不含缩放，没设父级时是世界空间）。
    /// </summary>
    public class AsiKeyProject : ScriptableObject
    {
        /// <summary>工程父级。为空时姿势空间是世界空间。</summary>
        public string rootId = "";
        public string rootPath = "";
        public string rootName = "";
        public List<AsiKeyUnit> units = new List<AsiKeyUnit>();
        public List<AsiKeyConstraint> constraints = new List<AsiKeyConstraint>();
        /// <summary>「仅选择某个角色的骨骼」筛选的单位。</summary>
        public int selectFilterUnit;
        public AsiObjectBakeMode objectBakeMode;
        public string mergedClipName = "";

        public float frameRate = 30f;
        public int startFrame;
        public int endFrame = 60;
        public AsiInterpMode defaultMode = AsiInterpMode.TCB;

        public List<AsiKeyBone> bones = new List<AsiKeyBone>();
        public List<AsiKeyChain> chains = new List<AsiKeyChain>();
        public List<AsiKeyLayer> layers = new List<AsiKeyLayer>();
        public int activeLayer;
        public int structureStamp;

        public bool xray = true;
        public bool showLinks = true;
        public bool showTrajectory;
        public float shapeScale = 1f;
        public AsiBoneDisplay boneDisplay;

        public string bakeFolder = "Assets";
        /// <summary>片段名前缀：单位没设片段名时用「前缀_单位名」，只有一个角色单位时直接用它。</summary>
        public string clipName = "NewMotion";
        public bool bakeLoop;
        public bool bakeCompress;

        [NonSerialized] public int version;

        public void Touch()
        {
            version++;
        }

        public void TouchStructure()
        {
            structureStamp++;
            version++;
        }

        public void EnsureLayers()
        {
            if (layers.Count == 0)
                layers.Add(new AsiKeyLayer());
            if (activeLayer < 0 || activeLayer >= layers.Count)
                activeLayer = 0;
        }

        public AsiKeyLayer ActiveLayer
        {
            get
            {
                EnsureLayers();
                return layers[activeLayer];
            }
        }

        // ---------------- 单位 ----------------

        [NonSerialized] private int _scope = -1;

        /// <summary>
        /// 临时把按名字 / 路径 / 人形骨骼 / 质心的查询限定在一个单位内（-1 = 不限定），离开 using 时还原。
        /// 生成骨骼这类按「一个角色」写的函数，在作用域里对某个单位照常工作。
        /// </summary>
        public UnitScope Scope(int unit)
        {
            UnitScope s = new UnitScope(this, _scope);
            _scope = unit;
            return s;
        }

        public struct UnitScope : IDisposable
        {
            private readonly AsiKeyProject _p;
            private readonly int _prev;

            public UnitScope(AsiKeyProject p, int prev)
            {
                _p = p;
                _prev = prev;
            }

            public void Dispose()
            {
                if (_p != null)
                    _p._scope = _prev;
            }
        }

        public int ScopeUnit
        {
            get { return _scope; }
        }

        private bool InScope(int i)
        {
            return _scope < 0 || bones[i].unit == _scope;
        }

        public bool ValidUnit(int u)
        {
            return u >= 0 && u < units.Count;
        }

        /// <summary>单位种类；无效单位按物体处理。</summary>
        public AsiUnitKind UnitKind(int u)
        {
            return ValidUnit(u) ? units[u].kind : AsiUnitKind.Object;
        }

        public bool IsCharacter(int u)
        {
            return ValidUnit(u) && units[u].kind != AsiUnitKind.Object;
        }

        public bool UnitIsHumanoid(int u)
        {
            return ValidUnit(u) && units[u].kind == AsiUnitKind.Humanoid;
        }

        /// <summary>当前单位作用域（没有作用域时为单位 0）是否为人形。</summary>
        public bool humanoid
        {
            get { return UnitIsHumanoid(CurrentUnit); }
        }

        /// <summary>当前单位作用域，没有作用域时为单位 0。</summary>
        public int CurrentUnit
        {
            get { return _scope >= 0 ? _scope : 0; }
        }

        public float UnitComRatio(int u)
        {
            return ValidUnit(u) ? units[u].comRatio : 0.5f;
        }

        public bool AnyHumanoid
        {
            get
            {
                for (int u = 0; u < units.Count; u++)
                {
                    if (units[u].kind == AsiUnitKind.Humanoid)
                        return true;
                }
                return false;
            }
        }

        public int UnitOf(int bone)
        {
            return bone >= 0 && bone < bones.Count ? bones[bone].unit : -1;
        }

        public string UnitName(int u)
        {
            return ValidUnit(u) ? units[u].name : "(无效单位)";
        }

        public List<int> UnitBones(int u)
        {
            List<int> list = new List<int>();
            for (int i = 0; i < bones.Count; i++)
            {
                if (bones[i].unit == u)
                    list.Add(i);
            }
            return list;
        }

        /// <summary>多单位工程里骨骼名前加单位名，单位之间可以重名。</summary>
        public string BoneLabel(int i)
        {
            if (i < 0 || i >= bones.Count)
                return "(无效骨骼)";
            if (units.Count <= 1)
                return bones[i].name;
            return UnitName(bones[i].unit) + "/" + bones[i].name;
        }

        public int ComOf(int unit)
        {
            for (int i = 0; i < bones.Count; i++)
            {
                if (bones[i].isCom && bones[i].unit == unit)
                    return i;
            }
            return -1;
        }

        /// <summary>单位的根骨骼：质心，或物体单位 / 骨骼角色第一根父级为 -1 的骨骼。</summary>
        public int UnitRootBone(int unit)
        {
            int com = ComOf(unit);
            if (com >= 0)
                return com;
            for (int i = 0; i < bones.Count; i++)
            {
                if (bones[i].unit == unit && bones[i].parent < 0)
                    return i;
            }
            return -1;
        }

        /// <summary>当前单位作用域里的质心（没有作用域时为第一个质心）。</summary>
        public int ComIndex
        {
            get
            {
                for (int i = 0; i < bones.Count; i++)
                {
                    if (bones[i].isCom && InScope(i))
                        return i;
                }
                return -1;
            }
        }

        public int ChainOf(int bone)
        {
            for (int c = 0; c < chains.Count; c++)
            {
                if (chains[c].Contains(bone))
                    return c;
            }
            return -1;
        }

        public int ChainUnit(int c)
        {
            return c >= 0 && c < chains.Count && chains[c].bones.Length > 0 ? UnitOf(chains[c].bones[0]) : -1;
        }

        public int BoneByPath(string path)
        {
            for (int i = 0; i < bones.Count; i++)
            {
                if (!bones[i].isCom && bones[i].path == path && InScope(i))
                    return i;
            }
            return -1;
        }

        public int BoneByPath(string path, int unit)
        {
            using (Scope(unit))
                return BoneByPath(path);
        }

        public int BoneByName(string boneName)
        {
            for (int i = 0; i < bones.Count; i++)
            {
                if (bones[i].name == boneName && InScope(i))
                    return i;
            }
            return -1;
        }

        public int BoneByName(string boneName, int unit)
        {
            using (Scope(unit))
                return BoneByName(boneName);
        }

        public int BoneByHuman(HumanBodyBones hb)
        {
            int id = (int)hb;
            for (int i = 0; i < bones.Count; i++)
            {
                if (bones[i].humanBone == id && InScope(i))
                    return i;
            }
            return -1;
        }

        public int BoneByHuman(HumanBodyBones hb, int unit)
        {
            using (Scope(unit))
                return BoneByHuman(hb);
        }

        public int ConstraintsOn(int bone, List<int> result)
        {
            result.Clear();
            for (int k = 0; k < constraints.Count; k++)
            {
                if (constraints[k].bone == bone)
                    result.Add(k);
            }
            return result.Count;
        }

        public bool HasConstraint(int bone)
        {
            for (int k = 0; k < constraints.Count; k++)
            {
                if (constraints[k].bone == bone)
                    return true;
            }
            return false;
        }

        public int MemberCount(AsiKeyTrack t)
        {
            switch (t.kind)
            {
                case AsiTrackKind.Chain:
                    return t.target >= 0 && t.target < chains.Count ? chains[t.target].bones.Length : 0;
                case AsiTrackKind.ComHorizontal:
                case AsiTrackKind.ComVertical:
                case AsiTrackKind.Constraint:
                case AsiTrackKind.LayerWeight:
                    return 0;
                default:
                    return 1;
            }
        }

        public bool HasPos(AsiKeyTrack t)
        {
            switch (t.kind)
            {
                case AsiTrackKind.ComHorizontal:
                case AsiTrackKind.ComVertical:
                case AsiTrackKind.Constraint:
                case AsiTrackKind.LayerWeight:
                    return true;
                case AsiTrackKind.Bone:
                    return t.target >= 0 && t.target < bones.Count && bones[t.target].animatePosition;
                default:
                    return false;
            }
        }

        /// <summary>位移通道掩码：x=1, y=2, z=4。约束影响度轨道只用 x。</summary>
        public int PosMask(AsiKeyTrack t)
        {
            switch (t.kind)
            {
                case AsiTrackKind.ComHorizontal: return 5;
                case AsiTrackKind.ComVertical: return 2;
                case AsiTrackKind.Bone: return HasPos(t) ? 7 : 0;
                case AsiTrackKind.Constraint:
                case AsiTrackKind.LayerWeight: return 1;
                default: return 0;
            }
        }

        public int TangentCount(AsiKeyTrack t)
        {
            return MemberCount(t) * 3 + 3;
        }

        public int TrackBone(AsiKeyTrack t, int member)
        {
            if (t.kind == AsiTrackKind.Chain)
            {
                if (t.target < 0 || t.target >= chains.Count)
                    return -1;
                int[] b = chains[t.target].bones;
                return member >= 0 && member < b.Length ? b[member] : -1;
            }
            if (t.kind == AsiTrackKind.Constraint)
                return t.target >= 0 && t.target < constraints.Count ? constraints[t.target].bone : -1;
            return t.target;
        }

        public AsiKeyTrack FindTrack(int layer, AsiTrackKind kind, int target)
        {
            if (layer < 0 || layer >= layers.Count)
                return null;
            List<AsiKeyTrack> tracks = layers[layer].tracks;
            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].kind == kind && tracks[i].target == target)
                    return tracks[i];
            }
            return null;
        }

        public AsiKeyTrack GetTrack(int layer, AsiTrackKind kind, int target)
        {
            AsiKeyTrack t = FindTrack(layer, kind, target);
            if (t != null)
                return t;
            t = new AsiKeyTrack();
            t.kind = kind;
            t.target = target;
            t.mode = defaultMode;
            layers[layer].tracks.Add(t);
            return t;
        }

        public string TrackName(AsiKeyTrack t)
        {
            string unit = units.Count > 1 ? UnitName(UnitOf(TrackBone(t, 0))) + "/" : "";
            switch (t.kind)
            {
                case AsiTrackKind.ComHorizontal: return unit + "质心 水平";
                case AsiTrackKind.ComVertical: return unit + "质心 垂直";
                case AsiTrackKind.ComRotation: return unit + "质心 旋转";
                case AsiTrackKind.Chain:
                    return t.target >= 0 && t.target < chains.Count ? unit + chains[t.target].name : "(无效链)";
                case AsiTrackKind.Constraint:
                    return t.target >= 0 && t.target < constraints.Count ? "约束 " + ConstraintLabel(t.target) : "(无效约束)";
                case AsiTrackKind.LayerWeight:
                    return t.target >= 0 && t.target < layers.Count ? layers[t.target].name + " 权重" : "层权重";
                default:
                    return t.target >= 0 && t.target < bones.Count ? BoneLabel(t.target) : "(无效骨骼)";
            }
        }

        public string ConstraintLabel(int k)
        {
            AsiKeyConstraint c = constraints[k];
            string n = string.IsNullOrEmpty(c.name) ? AsiKeyLabels.Constraint[(int)c.kind] : c.name;
            return BoneLabel(c.bone) + " · " + n;
        }

        public int Depth(int bone)
        {
            int d = 0;
            int guard = bones.Count + 1;
            int p = bone >= 0 && bone < bones.Count ? bones[bone].parent : -1;
            while (p >= 0 && guard-- > 0)
            {
                d++;
                p = bones[p].parent;
            }
            return d;
        }

        /// <summary>沿关节连接（位置锚点优先，否则父级）向上数的层数。</summary>
        public int LinkDepth(int bone)
        {
            int d = 0;
            int guard = bones.Count + 1;
            int p = LinkParent(bone);
            while (p >= 0 && guard-- > 0)
            {
                d++;
                p = LinkParent(p);
            }
            return d;
        }

        /// <summary>关节位置所挂的骨骼，没有时为 -1。</summary>
        public int AnchorOf(int bone)
        {
            AsiKeyBone b = bones[bone];
            if (!b.anchored || b.animatePosition || b.anchor < 0 || b.anchor >= bones.Count || b.anchor == bone)
                return -1;
            return b.anchor;
        }

        /// <summary>关节连接的骨骼：位置挂在锚点上时是锚点，否则是旋转父级。</summary>
        public int LinkParent(int bone)
        {
            if (bone < 0 || bone >= bones.Count)
                return -1;
            int a = AnchorOf(bone);
            return a >= 0 ? a : bones[bone].parent;
        }

        /// <summary>绑定姿势下关节在 <see cref="LinkParent"/> 局部空间中的位置。</summary>
        public Vector3 LinkOffset(int bone)
        {
            AsiKeyBone b = bones[bone];
            return AnchorOf(bone) >= 0 ? b.anchorPos : b.restPos;
        }

        /// <summary>关节连在 bone 上的第一个骨骼（下标最小），没有时为 -1。</summary>
        public int FirstChild(int bone)
        {
            if (bone < 0)
                return -1;
            for (int i = 0; i < bones.Count; i++)
            {
                if (i != bone && LinkParent(i) == bone)
                    return i;
            }
            return -1;
        }

        /// <summary>脚链（人形 LeftFoot/RightFoot 末端）且有脚趾：固定/滑动关键帧默认绕脚趾根转。</summary>
        public bool UsesChildPivot(AsiKeyChain chain)
        {
            int e = chain != null ? chain.Effector : -1;
            if (e < 0 || e >= bones.Count)
                return false;
            int h = bones[e].humanBone;
            return (h == (int)HumanBodyBones.LeftFoot || h == (int)HumanBodyBones.RightFoot) && FirstChild(e) >= 0;
        }

        /// <summary>链轴心在末端控制器局部空间的位置；<see cref="AsiKeyChain.ChildPivot"/> 取第一个子级的关节位置。</summary>
        public Vector3 PivotOf(AsiKeyChain chain, int index)
        {
            if (index != AsiKeyChain.ChildPivot)
                return chain.Pivot(index);
            int c = FirstChild(chain.Effector);
            return c >= 0 ? LinkOffset(c) : Vector3.zero;
        }

        /// <summary>模型骨骼需要写入位置：根骨骼、质心的直接子骨骼（关节位置挂在别的骨骼上的除外）、记录位移的骨骼。</summary>
        public bool Movable(int bone)
        {
            AsiKeyBone b = bones[bone];
            if (b.animatePosition)
                return true;
            if (AnchorOf(bone) >= 0)
                return false;
            return b.parent < 0 || bones[b.parent].isCom;
        }

        /// <summary>父级与位置锚点都在前的求值顺序。</summary>
        public int[] BuildOrder()
        {
            int n = bones.Count;
            int[] order = new int[n];
            int[] depth = new int[n];
            for (int i = 0; i < n; i++)
                depth[i] = -1;
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                OrderDepth(i, depth, n + 1);
            }
            Array.Sort(order, (a, b) => depth[a] != depth[b] ? depth[a].CompareTo(depth[b]) : a.CompareTo(b));
            return order;
        }

        private int OrderDepth(int i, int[] depth, int guard)
        {
            if (depth[i] >= 0)
                return depth[i];
            int d = 0;
            if (guard > 0)
            {
                int parent = bones[i].parent;
                if (parent >= 0 && parent < bones.Count)
                    d = OrderDepth(parent, depth, guard - 1) + 1;
                int a = AnchorOf(i);
                if (a >= 0)
                    d = Mathf.Max(d, OrderDepth(a, depth, guard - 1) + 1);
            }
            depth[i] = d;
            return d;
        }

        /// <summary>bone 的旋转或关节位置是否受 ancestor 带动（沿旋转父级与位置锚点向上找）。</summary>
        public bool IsUnder(int bone, int ancestor)
        {
            return IsUnder(bone, ancestor, bones.Count + 1);
        }

        private bool IsUnder(int bone, int ancestor, int guard)
        {
            int b = bone;
            while (b >= 0 && b < bones.Count && guard-- > 0)
            {
                if (b == ancestor)
                    return true;
                int a = AnchorOf(b);
                if (a >= 0 && IsUnder(a, ancestor, guard))
                    return true;
                b = bones[b].parent;
            }
            return false;
        }

        public bool IsUnderChain(int bone, int chain)
        {
            if (bone < 0 || chain < 0 || chain >= chains.Count)
                return false;
            int[] members = chains[chain].bones;
            for (int i = 0; i < members.Length; i++)
            {
                if (IsUnder(bone, members[i]))
                    return true;
            }
            return false;
        }

        public float FrameToTime(float f)
        {
            return (f - startFrame) / Mathf.Max(1f, frameRate);
        }

        /// <summary>在当前单位作用域里不重名。</summary>
        public string UniqueBoneName(string baseName)
        {
            string n = string.IsNullOrEmpty(baseName) ? "Bone" : baseName;
            if (BoneByName(n) < 0)
                return n;
            for (int i = 2; ; i++)
            {
                string c = n + "_" + i;
                if (BoneByName(c) < 0)
                    return c;
            }
        }

        public string UniqueUnitName(string baseName)
        {
            string n = string.IsNullOrEmpty(baseName) ? "单位" : baseName.Replace('/', '_');
            for (int i = 1; ; i++)
            {
                string c = i == 1 ? n : n + "_" + i;
                bool used = false;
                for (int u = 0; u < units.Count && !used; u++)
                    used = units[u].name == c;
                if (!used)
                    return c;
            }
        }
    }
}
