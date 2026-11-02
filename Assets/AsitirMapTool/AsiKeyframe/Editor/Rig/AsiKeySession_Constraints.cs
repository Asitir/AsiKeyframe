using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>约束的增删改、目标拾取模式、塌陷为关键帧。</summary>
    public partial class AsiKeySession
    {
        public enum PickRole { Target = 0, UpObject = 1 }

        public static readonly string[] ConstraintKindNames = { "位置约束", "旋转约束", "注视约束", "链接约束" };

        private string BlockedConstraintStatus(int bone, int k)
        {
            string name = project.bones[bone].name;
            return name + " 受「" + project.ConstraintLabel(k) + "」部分影响，影响度为 0 或 1 时才能编辑";
        }

        /// <summary>正在为哪个约束拾取目标（-1 为不在拾取模式）。</summary>
        public int pickConstraint = -1;
        public PickRole pickRole;

        public bool Picking
        {
            get { return pickConstraint >= 0 && pickConstraint < project.constraints.Count; }
        }

        /// <summary>正在为哪个滑动关键帧点选绑定对象。</summary>
        public AsiKeyTrack pickBindTrack;
        public AsiKey pickBindKey;

        public bool PickingBind
        {
            get { return pickBindTrack != null && pickBindKey != null; }
        }

        /// <summary>约束拾取或滑动关键帧点选绑定，Scene 都要把左键让给点选。</summary>
        public bool ScenePicking
        {
            get { return Picking || PickingBind; }
        }

        public void BeginBindPick(AsiKeyTrack tr, AsiKey key)
        {
            pickConstraint = -1;
            pickBindTrack = tr;
            pickBindKey = key;
            status = "点选绑定对象：在 Scene 里点骨骼控制器或场景物体（Esc 或右键取消）";
            SceneView.RepaintAll();
            RaiseRepaint();
        }

        /// <summary>把当前点选到的物体写成这条滑动关键帧的绑定。骨骼控制器记成骨骼，其它场景物体记成对象引用。</summary>
        public string AssignBindTarget(Transform t)
        {
            if (!PickingBind)
                return "没有正在点选的滑动关键帧";
            if (t == null)
                return "没有点中对象";
            int bone = BoneOfTransform(t);
            if (pickBindTrack.kind == AsiTrackKind.Chain && pickBindTrack.target >= 0 && pickBindTrack.target < project.chains.Count && bone >= 0)
            {
                if (project.chains[pickBindTrack.target].Contains(bone))
                    return "不能绑到这条链自己的骨骼上";
                if (AsiKeyEvaluator.HasCycle(project, pickBindTrack.target, bone))
                    return "绑到这根骨骼会形成循环";
            }
            ConvertKeySpace(pickBindTrack, pickBindKey, AsiIKSpace.Object, bone, bone < 0 ? AsiKeyUtil.EncodeObjectRef(t) : "");
            status = "已绑定到「" + TargetName(bone, bone < 0 ? AsiKeyUtil.EncodeObjectRef(t) : "") + "」";
            return "";
        }

        public void BeginPick(int k, PickRole role)
        {
            pickBindTrack = null;
            pickBindKey = null;
            pickConstraint = k;
            pickRole = role;
            status = "拾取" + (role == PickRole.UpObject ? "上方向物体" : "约束目标") + "：在 Scene 里点控制器或场景物体（Shift 连续拾取，Esc 或右键取消）";
            SceneView.RepaintAll();
            RaiseRepaint();
        }

        public void EndPick()
        {
            if (pickConstraint < 0 && pickBindTrack == null)
                return;
            pickConstraint = -1;
            pickBindTrack = null;
            pickBindKey = null;
            SceneView.RepaintAll();
            RaiseRepaint();
        }

        private void EditConstraints(string undo, Action fn)
        {
            Undo.RecordObject(project, undo);
            fn();
            project.Touch();
            EditorUtility.SetDirty(project);
            Refresh();
        }

        /// <summary>给骨骼加约束，返回约束下标（失败为 -1，原因写在 status）。链接约束在当前帧加一个「链接到世界」的条目。</summary>
        public int AddConstraint(int bone, AsiConstraintKind kind)
        {
            if (!Valid || bone < 0 || bone >= project.bones.Count)
            {
                status = "先选择要被约束的控制器";
                return -1;
            }
            if ((kind == AsiConstraintKind.Position || kind == AsiConstraintKind.Link) && !project.Movable(bone))
            {
                if (kind == AsiConstraintKind.Position)
                {
                    status = "「" + project.bones[bone].name + "」不能移动（只能旋转），不能加位置约束";
                    return -1;
                }
            }
            int k = -1;
            EditConstraints("添加约束", () =>
            {
                AsiKeyConstraint c = new AsiKeyConstraint();
                c.kind = kind;
                c.bone = bone;
                c.name = ConstraintKindNames[(int)kind] + "：" + project.bones[bone].name;
                if (kind == AsiConstraintKind.Link)
                    c.links.Add(new AsiLinkEntry { frame = Mathf.Round(frame) });
                project.constraints.Add(c);
                k = project.constraints.Count - 1;
                if (kind == AsiConstraintKind.Link)
                    AsiKeyConstraints.GrabLinkOffsets(project, ctx, k);
            });
            status = "已添加" + ConstraintKindNames[(int)kind] + "到「" + project.bones[bone].name + "」";
            return k;
        }

        public void RemoveConstraint(int k)
        {
            if (k < 0 || k >= project.constraints.Count)
                return;
            if (pickConstraint == k)
                EndPick();
            else if (pickConstraint > k)
                pickConstraint--;
            AsiKeyPanels.ClearKeySelection();
            EditConstraints("删除约束", () => AsiKeyConstraints.RemoveAt(project, k));
        }

        /// <summary>场景物体换算成约束目标：控制器或单位里的模型骨骼 → 骨骼下标，其它场景物体 → 编码引用。</summary>
        public bool ResolveTargetRef(Transform t, out int bone, out string obj, out string error)
        {
            bone = -1;
            obj = "";
            error = "";
            if (t == null)
            {
                error = "没有物体";
                return false;
            }
            bone = BoneOfTransform(t);
            if (bone >= 0)
                return true;
            if (AsiKeyRigObjects.IsRigObject(t))
            {
                error = "这是控制器根或单位子根，不能作为目标";
                return false;
            }
            if (InsideAnyUnit(t))
            {
                error = "「" + t.name + "」在单位里但没有控制器，先在骨骼页签把它加为骨骼";
                return false;
            }
            if (EditorUtility.IsPersistent(t.gameObject))
            {
                error = "只能用场景里的物体";
                return false;
            }
            obj = AsiKeyUtil.EncodeObjectRef(t);
            return true;
        }

        private string CheckTarget(AsiKeyConstraint c, int bone)
        {
            if (bone < 0)
                return "";
            if (bone == c.bone)
                return "不能以被约束的骨骼自身为目标";
            if (AsiKeyConstraints.WouldCycle(project, c.bone, bone))
                return "「" + project.bones[bone].name + "」受「" + project.bones[c.bone].name + "」带动，会形成循环依赖";
            return "";
        }

        public string AddConstraintTarget(int k, Transform t)
        {
            int bone;
            string obj, error;
            if (!ResolveTargetRef(t, out bone, out obj, out error))
                return error;
            return AddConstraintTarget(k, bone, obj);
        }

        /// <summary>
        /// 加目标。链接约束改为在当前帧加（或改）一个链接条目并重新抓取它及之后条目的偏移；
        /// 其它约束勾了保持偏移时在当前帧抓取偏移，让骨骼停在原位。
        /// </summary>
        public string AddConstraintTarget(int k, int bone, string obj)
        {
            if (k < 0 || k >= project.constraints.Count)
                return "无效约束";
            AsiKeyConstraint c = project.constraints[k];
            string error = CheckTarget(c, bone);
            if (!string.IsNullOrEmpty(error))
                return error;
            obj = bone >= 0 ? "" : obj ?? "";
            if (c.kind == AsiConstraintKind.Link)
                return SetLinkAtFrame(k, bone, obj);
            for (int t = 0; t < c.targets.Count; t++)
            {
                if (c.targets[t].bone == bone && c.targets[t].obj == obj)
                    return "已经是目标了";
            }
            EditConstraints("添加约束目标", () =>
            {
                c.targets.Add(new AsiConstraintTarget { bone = bone, obj = obj, weight = 1f });
                if (c.keepOffset)
                    AsiKeyConstraints.GrabOffset(project, ctx, frame, k);
                else
                    AsiKeyConstraints.ClearOffset(c);
            });
            status = "已添加目标「" + TargetName(bone, obj) + "」";
            return "";
        }

        /// <summary>链接约束：当前帧起父级换成 bone / obj（都为空时为世界）。</summary>
        public string SetLinkAtFrame(int k, int bone, string obj)
        {
            AsiKeyConstraint c = project.constraints[k];
            string error = CheckTarget(c, bone);
            if (!string.IsNullOrEmpty(error))
                return error;
            float f = Mathf.Round(frame);
            EditConstraints("链接", () =>
            {
                AsiLinkEntry le = null;
                for (int e = 0; e < c.links.Count; e++)
                {
                    if (Mathf.Abs(c.links[e].frame - f) < 0.01f)
                        le = c.links[e];
                }
                if (le == null)
                {
                    le = new AsiLinkEntry { frame = f };
                    c.links.Add(le);
                }
                le.bone = bone;
                le.obj = bone >= 0 ? "" : obj ?? "";
                AsiKeyConstraints.SortLinks(c);
                AsiKeyConstraints.GrabLinkOffsets(project, ctx, k, c.links.IndexOf(le));
            });
            status = "第 " + f + " 帧起链接到「" + TargetName(bone, obj) + "」";
            return "";
        }

        public void RemoveLinkEntry(int k, int e)
        {
            AsiKeyConstraint c = project.constraints[k];
            if (e < 0 || e >= c.links.Count)
                return;
            EditConstraints("删除链接条目", () =>
            {
                AsiKeyConstraints.SortLinks(c);
                c.links.RemoveAt(e);
                AsiKeyConstraints.GrabLinkOffsets(project, ctx, k, e);
            });
        }

        /// <summary>
        /// 移动或复制链接关键帧。只改帧号，相对父级的位置和角度保持原样。
        /// 复制时把新条目填进 copies。落到同一帧时留下正在移动的那条。
        /// </summary>
        public void MoveLinkEntries(List<AsiLinkEntry> entries, float pivot, float factor, float delta, bool copy, List<AsiLinkEntry> copies)
        {
            if (!Valid || entries == null || entries.Count == 0)
                return;
            EditConstraints(copy ? "复制链接关键帧" : "移动链接关键帧", () =>
            {
                List<AsiLinkEntry> moved = new List<AsiLinkEntry>();
                for (int i = 0; i < entries.Count; i++)
                {
                    AsiLinkEntry le = entries[i];
                    AsiKeyConstraint host = LinkHost(le);
                    if (host == null)
                        continue;
                    float nf = Mathf.Round((pivot + (le.frame - pivot) * factor + delta) * 100f) / 100f;
                    if (copy)
                    {
                        AsiLinkEntry n = new AsiLinkEntry();
                        n.frame = nf;
                        n.bone = le.bone;
                        n.obj = le.obj;
                        n.offPos = le.offPos;
                        n.offRot = le.offRot;
                        host.links.Add(n);
                        moved.Add(n);
                        if (copies != null)
                            copies.Add(n);
                    }
                    else
                    {
                        le.frame = nf;
                        moved.Add(le);
                    }
                }
                for (int k = 0; k < project.constraints.Count; k++)
                {
                    AsiKeyConstraint c = project.constraints[k];
                    if (c.kind != AsiConstraintKind.Link)
                        continue;
                    AsiKeyConstraints.SortLinks(c);
                    for (int i = c.links.Count - 1; i >= 1; i--)
                    {
                        if (Mathf.Abs(c.links[i].frame - c.links[i - 1].frame) >= 0.01f)
                            continue;
                        bool keepLater = moved.Contains(c.links[i]);
                        bool keepEarlier = moved.Contains(c.links[i - 1]);
                        int drop = keepLater && !keepEarlier ? i - 1 : i;
                        c.links.RemoveAt(drop);
                    }
                }
            });
        }

        /// <summary>删掉这些链接关键帧。剩下的条目仍保持原来的相对偏移。</summary>
        public void DeleteLinkEntries(List<AsiLinkEntry> entries)
        {
            if (!Valid || entries == null || entries.Count == 0)
                return;
            EditConstraints("删除链接关键帧", () =>
            {
                for (int k = 0; k < project.constraints.Count; k++)
                {
                    AsiKeyConstraint c = project.constraints[k];
                    for (int i = c.links.Count - 1; i >= 0; i--)
                    {
                        if (entries.Contains(c.links[i]))
                            c.links.RemoveAt(i);
                    }
                }
            });
        }

        private AsiKeyConstraint LinkHost(AsiLinkEntry le)
        {
            for (int k = 0; k < project.constraints.Count; k++)
            {
                if (project.constraints[k].links.Contains(le))
                    return project.constraints[k];
            }
            return null;
        }

        public void RemoveConstraintTarget(int k, int t)
        {
            AsiKeyConstraint c = project.constraints[k];
            if (t < 0 || t >= c.targets.Count)
                return;
            EditConstraints("删除约束目标", () => c.targets.RemoveAt(t));
        }

        public string SetUpObject(int k, Transform t)
        {
            int bone;
            string obj, error;
            if (!ResolveTargetRef(t, out bone, out obj, out error))
                return error;
            AsiKeyConstraint c = project.constraints[k];
            error = CheckTarget(c, bone);
            if (!string.IsNullOrEmpty(error))
                return error;
            EditConstraints("上方向物体", () =>
            {
                c.upMode = AsiLookUp.Object;
                c.upBone = bone;
                c.upObj = bone >= 0 ? "" : obj;
            });
            return "";
        }

        /// <summary>改约束的普通字段（名称、开关、轴向等）。</summary>
        public void EditConstraint(int k, string undo, Action<AsiKeyConstraint> fn)
        {
            if (k < 0 || k >= project.constraints.Count)
                return;
            AsiKeyConstraint c = project.constraints[k];
            EditConstraints(undo, () => fn(c));
        }

        public void SetKeepOffset(int k, bool on)
        {
            EditConstraint(k, "保持偏移", c =>
            {
                c.keepOffset = on;
                if (on)
                    AsiKeyConstraints.GrabOffset(project, ctx, frame, k);
                else
                    AsiKeyConstraints.ClearOffset(c);
            });
        }

        /// <summary>在当前帧重新抓取偏移（链接约束重新抓取全部条目）。</summary>
        public void RegrabOffset(int k)
        {
            EditConstraint(k, "重新抓取偏移", c =>
            {
                if (c.kind == AsiConstraintKind.Link)
                    AsiKeyConstraints.GrabLinkOffsets(project, ctx, k);
                else
                    AsiKeyConstraints.GrabOffset(project, ctx, frame, k);
            });
        }

        /// <summary>影响度：有关键帧或开着自动关键点时打关键帧，否则改默认值。</summary>
        public void SetConstraintWeight(int k, float w)
        {
            w = Mathf.Clamp01(w);
            bool keyed = false;
            for (int l = 0; l < project.layers.Count && !keyed; l++)
            {
                AsiKeyTrack tr = project.FindTrack(l, AsiTrackKind.Constraint, k);
                keyed = tr != null && tr.keys.Count > 0;
            }
            if (keyed || autoKey)
                KeyConstraintWeight(k, w);
            else
                EditConstraint(k, "约束影响度", c => c.weight = w);
        }

        public string TargetName(int bone, string obj)
        {
            if (bone >= 0 && bone < project.bones.Count)
                return project.bones[bone].name;
            if (string.IsNullOrEmpty(obj))
                return "世界（工程父级）";
            Transform t = ctx.Resolve(obj);
            return t != null ? t.name : "（找不到物体）";
        }

        /// <summary>
        /// 塌陷为关键帧：逐帧求出被约束骨骼的最终局部姿势，写成基础层关键帧（按误差精简），然后关掉约束。
        /// IK 链成员不支持（它们的姿势由链求解）。
        /// </summary>
        public string CollapseConstraint(int k)
        {
            if (k < 0 || k >= project.constraints.Count)
                return "无效约束";
            AsiKeyConstraint c = project.constraints[k];
            if (!AsiKeyConstraints.ValidBone(project, c))
                return "约束没有被约束骨骼";
            int b = c.bone;
            if (project.ChainOf(b) >= 0)
                return "IK 链成员不能塌陷，请改用烘焙";
            int start = project.startFrame;
            int end = Mathf.Max(project.startFrame + 1, project.endFrame);
            int frames = end - start + 1;
            Quaternion[] rot = new Quaternion[frames];
            Vector3[] pos = new Vector3[frames];
            AsiKeyPose p = new AsiKeyPose();
            p.Ensure(project);
            for (int f = 0; f < frames; f++)
            {
                AsiKeyEvaluator.Evaluate(project, ctx, start + f, p, int.MaxValue, -1);
                rot[f] = p.rot[b];
                pos[f] = p.pos[b];
            }
            Undo.RecordObject(project, "塌陷约束");
            project.EnsureLayers();
            int written = AsiKeyClipImporter.WriteBoneKeys(project, b, rot, pos, start, 0.2f, 0.0005f);
            c.enabled = false;
            project.Touch();
            EditorUtility.SetDirty(project);
            Refresh();
            status = "已把「" + c.name + "」塌陷为 " + written + " 个关键帧，约束已关闭";
            return "";
        }
    }
}
