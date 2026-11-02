using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 工程单位的增删。角色单位借一个临时工程复用 <see cref="AsiKeyRigBuilder.Build"/> 生成骨骼和 IK 链，
    /// 再按下标偏移合并进来；物体单位只有一根骨骼（物体本身）。调用方负责 Undo 与会话的结构同步。
    /// </summary>
    public static class AsiKeyUnits
    {
        public static Transform ResolveTarget(AsiKeyProject p, int u)
        {
            if (!p.ValidUnit(u))
                return null;
            return AsiKeyUtil.ResolveTransform(p.units[u].targetId, p.units[u].targetPath);
        }

        public static AsiUnitKind DetectKind(Transform t)
        {
            Animator a = t != null ? t.GetComponent<Animator>() : null;
            if (a == null)
                return AsiUnitKind.Object;
            return AsiKeyRigBuilder.IsHumanoid(a) ? AsiUnitKind.Humanoid : AsiUnitKind.Generic;
        }

        /// <summary>能否把 t 加为单位。不能时返回原因。</summary>
        public static string CanAdd(AsiKeyProject p, Transform projectRoot, Transform t, AsiUnitKind kind, int ignore = -1)
        {
            if (t == null)
                return "没有选择物体";
            if (projectRoot != null && (t == projectRoot || !t.IsChildOf(projectRoot)))
                return "单位必须是工程父级「" + projectRoot.name + "」下的子物体";
            Transform top = t;
            while (top.parent != null)
                top = top.parent;
            if (AsiKeyRigObjects.IsRigRoot(top))
                return "不能把 K帧控制器加为单位";
            if (kind != AsiUnitKind.Object && t.GetComponent<Animator>() == null)
                return "角色单位需要 Animator";
            for (int u = 0; u < p.units.Count; u++)
            {
                Transform o = u != ignore ? ResolveTarget(p, u) : null;
                if (o == null)
                    continue;
                if (o == t)
                    return "「" + t.name + "」已经是单位「" + p.units[u].name + "」";
                if (t.IsChildOf(o))
                {
                    return p.units[u].IsCharacter
                        ? "「" + t.name + "」在角色单位「" + p.units[u].name + "」内部，可以在骨骼页把它加为该角色的骨骼"
                        : "「" + t.name + "」在单位「" + p.units[u].name + "」内部，单位之间不能嵌套（可以用链接约束让它跟随）";
                }
                if (o.IsChildOf(t))
                    return "单位「" + p.units[u].name + "」在「" + t.name + "」内部，单位之间不能嵌套";
            }
            return "";
        }

        /// <summary>把 t 加为新单位并生成骨骼。返回错误信息，成功时 unit 为新单位下标。</summary>
        public static string AddUnit(AsiKeyProject p, Transform projectRoot, Transform t, AsiUnitKind kind, out int unit)
        {
            unit = -1;
            string error = CanAdd(p, projectRoot, t, kind);
            if (!string.IsNullOrEmpty(error))
                return error;
            AsiKeyUnit u = new AsiKeyUnit();
            u.name = p.UniqueUnitName(t.name);
            u.kind = kind;
            u.targetId = AsiKeyUtil.GetId(t.gameObject);
            u.targetPath = AsiKeyUtil.ScenePath(t);
            u.clipName = t.name + "_Motion";
            Animator animator = t.GetComponent<Animator>();
            if (kind != AsiUnitKind.Object)
                u.avatar = animator.avatar;
            p.units.Add(u);
            unit = p.units.Count - 1;
            error = BuildBones(p, unit, projectRoot, t);
            if (!string.IsNullOrEmpty(error))
            {
                p.units.RemoveAt(unit);
                unit = -1;
                return error;
            }
            p.TouchStructure();
            return "";
        }

        /// <summary>按单位类型生成骨骼并追加到工程（单位已有的骨骼要先 <see cref="StripBones"/>）。</summary>
        public static string BuildBones(AsiKeyProject p, int u, Transform projectRoot, Transform t)
        {
            AsiKeyUnit unit = p.units[u];
            if (unit.kind == AsiUnitKind.Object)
            {
                AddObjectBone(p, u, projectRoot, t);
                return "";
            }
            Animator animator = t.GetComponent<Animator>();
            if (animator == null)
                return "「" + t.name + "」没有 Animator";
            if (unit.kind == AsiUnitKind.Humanoid && !AsiKeyRigBuilder.IsHumanoid(animator))
                unit.kind = AsiUnitKind.Generic;
            AsiKeyProject tmp = ScriptableObject.CreateInstance<AsiKeyProject>();
            tmp.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                AsiKeyUnit tu = new AsiKeyUnit();
                tu.kind = unit.kind;
                tu.comRatio = unit.comRatio;
                tmp.units.Add(tu);
                string error = AsiKeyRigBuilder.Build(tmp, animator);
                if (!string.IsNullOrEmpty(error))
                    return error;
                Merge(p, tmp, u);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tmp);
            }
            return "";
        }

        private static void Merge(AsiKeyProject p, AsiKeyProject tmp, int u)
        {
            int offset = p.bones.Count;
            for (int i = 0; i < tmp.bones.Count; i++)
            {
                AsiKeyBone b = tmp.bones[i];
                b.unit = u;
                if (b.parent >= 0)
                    b.parent += offset;
                if (b.mirror >= 0)
                    b.mirror += offset;
                if (b.anchor >= 0)
                    b.anchor += offset;
                p.bones.Add(b);
            }
            for (int c = 0; c < tmp.chains.Count; c++)
            {
                AsiKeyChain chain = tmp.chains[c];
                for (int k = 0; k < chain.bones.Length; k++)
                    chain.bones[k] += offset;
                p.chains.Add(chain);
            }
            tmp.bones.Clear();
            tmp.chains.Clear();
        }

        private static void AddObjectBone(AsiKeyProject p, int u, Transform projectRoot, Transform t)
        {
            AsiKeyBone b = new AsiKeyBone();
            using (p.Scope(u))
                b.name = p.UniqueBoneName(t.name);
            b.unit = u;
            b.path = "";
            b.parent = -1;
            b.animatePosition = true;
            b.side = AsiBoneSide.Center;
            if (projectRoot != null)
            {
                AsiKeyRigBuilder.RigSpace(projectRoot, t, out b.rigRestPos, out b.rigRestRot);
            }
            else
            {
                b.rigRestPos = t.position;
                b.rigRestRot = t.rotation;
            }
            b.restPos = b.rigRestPos;
            b.restRot = b.rigRestRot;
            b.axis = Vector3.forward;
            b.frame = Quaternion.identity;
            b.shape = ObjectShape(t, out b.boneLength);
            p.bones.Add(b);
        }

        /// <summary>物体外形：按 Renderer 包围盒（物体旋转坐标系、不含缩放）画方块，没有 Renderer 时用 0.2 的小方块。</summary>
        public static AsiKeyShape ObjectShape(Transform t, out float length)
        {
            AsiKeyShape s = new AsiKeyShape();
            s.kind = AsiShapeKind.BipBox;
            s.color = new Color(0.4f, 0.85f, 1f);
            Quaternion inv = Quaternion.Inverse(t.rotation);
            Renderer[] rs = t.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            for (int r = 0; r < rs.Length; r++)
            {
                if (rs[r] is ParticleSystemRenderer || rs[r] is TrailRenderer || rs[r] is LineRenderer)
                    continue;
                Bounds wb = rs[r].bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = wb.center + Vector3.Scale(wb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    Vector3 l = inv * (corner - t.position);
                    if (!any)
                    {
                        min = l;
                        max = l;
                        any = true;
                    }
                    else
                    {
                        min = Vector3.Min(min, l);
                        max = Vector3.Max(max, l);
                    }
                }
            }
            Vector3 size = any ? max - min : Vector3.one * 0.2f;
            Vector3 center = any ? (max + min) * 0.5f : Vector3.zero;
            size = Vector3.Max(size, Vector3.one * 0.02f);
            s.width = size.x;
            s.height = size.y;
            s.length = size.z;
            s.radiusX = size.x * 0.5f;
            s.radiusY = size.y * 0.5f;
            s.offset = new Vector3(center.x, center.y, center.z - size.z * 0.5f);
            length = size.z;
            return s;
        }

        // ---------------- 删除 ----------------

        /// <summary>删除单位及其骨骼、IK 链、轨道和相关约束，后面单位与骨骼的下标前移。</summary>
        public static void RemoveUnit(AsiKeyProject p, int u)
        {
            if (!p.ValidUnit(u))
                return;
            StripBones(p, u);
            for (int i = 0; i < p.bones.Count; i++)
            {
                if (p.bones[i].unit > u)
                    p.bones[i].unit--;
            }
            p.units.RemoveAt(u);
            if (p.selectFilterUnit == u)
                p.selectFilterUnit = 0;
            else if (p.selectFilterUnit > u)
                p.selectFilterUnit--;
            p.TouchStructure();
        }

        /// <summary>重新生成单位的骨骼（清掉该单位的动画、IK 链和以它为对象的约束）。</summary>
        public static string RebuildUnit(AsiKeyProject p, int u, Transform projectRoot)
        {
            Transform t = ResolveTarget(p, u);
            if (t == null)
                return "找不到单位目标：" + p.units[u].targetPath;
            StripBones(p, u);
            string error = BuildBones(p, u, projectRoot, t);
            p.TouchStructure();
            return error;
        }

        /// <summary>删掉单位 u 的全部骨骼，批量重映射其余引用。</summary>
        public static void StripBones(AsiKeyProject p, int u)
        {
            int n = p.bones.Count;
            int[] map = new int[n];
            int next = 0;
            for (int i = 0; i < n; i++)
                map[i] = p.bones[i].unit == u ? -1 : next++;
            Func<int, int> remap = x => x >= 0 && x < n ? map[x] : -1;

            int cn = p.chains.Count;
            int[] chainMap = new int[cn];
            int nextChain = 0;
            for (int c = 0; c < cn; c++)
            {
                bool dead = false;
                int[] bs = p.chains[c].bones;
                for (int k = 0; k < bs.Length && !dead; k++)
                    dead = remap(bs[k]) < 0;
                chainMap[c] = dead ? -1 : nextChain++;
            }

            for (int l = 0; l < p.layers.Count; l++)
            {
                List<AsiKeyTrack> tracks = p.layers[l].tracks;
                for (int t = tracks.Count - 1; t >= 0; t--)
                {
                    AsiKeyTrack tr = tracks[t];
                    if (tr.kind == AsiTrackKind.Constraint)
                        continue;
                    int nt = tr.kind == AsiTrackKind.Chain
                        ? (tr.target >= 0 && tr.target < cn ? chainMap[tr.target] : -1)
                        : remap(tr.target);
                    if (nt < 0)
                    {
                        tracks.RemoveAt(t);
                        continue;
                    }
                    tr.target = nt;
                    for (int k = 0; k < tr.keys.Count; k++)
                    {
                        AsiKey key = tr.keys[k];
                        if (key.ikBone < 0)
                            continue;
                        key.ikBone = remap(key.ikBone);
                        if (key.ikBone < 0 && key.ikSpace == AsiIKSpace.Object && string.IsNullOrEmpty(key.ikObject))
                            key.ikSpace = AsiIKSpace.World;
                    }
                }
            }

            AsiKeyConstraints.RemapBones(p, remap);

            List<AsiKeyChain> chains = new List<AsiKeyChain>();
            for (int c = 0; c < cn; c++)
            {
                if (chainMap[c] < 0)
                    continue;
                AsiKeyChain chain = p.chains[c];
                for (int k = 0; k < chain.bones.Length; k++)
                    chain.bones[k] = remap(chain.bones[k]);
                chains.Add(chain);
            }
            p.chains.Clear();
            p.chains.AddRange(chains);

            List<AsiKeyBone> bones = new List<AsiKeyBone>();
            for (int i = 0; i < n; i++)
            {
                if (map[i] < 0)
                    continue;
                AsiKeyBone b = p.bones[i];
                b.parent = remap(b.parent);
                b.mirror = remap(b.mirror);
                b.anchor = remap(b.anchor);
                if (b.anchor < 0)
                    b.anchored = false;
                bones.Add(b);
            }
            p.bones.Clear();
            p.bones.AddRange(bones);
        }

        /// <summary>单位目标的路径变化（改名、移动层级）时更新记录。有改动时返回 true。</summary>
        public static bool RefreshPath(AsiKeyProject p, int u, Transform t)
        {
            if (t == null)
                return false;
            string path = AsiKeyUtil.ScenePath(t);
            if (p.units[u].targetPath == path)
                return false;
            p.units[u].targetPath = path;
            return true;
        }
    }
}
