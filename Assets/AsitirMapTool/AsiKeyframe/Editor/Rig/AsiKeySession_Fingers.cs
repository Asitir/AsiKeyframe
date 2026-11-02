using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>手掌上的卷曲、张开、错落直接改指节旋转，以及每指 IK。不是约束。</summary>
    public partial class AsiKeySession
    {
        private readonly AsiKeyPose _fingerStart = new AsiKeyPose();
        private readonly List<int> _fingerBones = new List<int>();
        private bool _fingerDrag;
        private int _fingerHand = -1;

        /// <summary>开始一次手掌手柄拖动，记下当前各指节的姿势。</summary>
        public void BeginFingerPose(int hand)
        {
            _fingerDrag = false;
            if (!Valid)
                return;
            int unit;
            int side;
            if (!AsiKeyFingers.TryHand(project, hand, out unit, out side))
                return;
            _fingerStart.CopyFrom(pose);
            _fingerHand = hand;
            _fingerBones.Clear();
            _fingerDrag = true;
        }

        /// <summary>从按下时的姿势加上这一次拖动的总角度（度），写进各指节的 fk。</summary>
        public void DragFingerPose(float curlDeg, float spreadDeg, float staggerDeg)
        {
            if (!_fingerDrag || !Valid)
                return;
            pose.CopyFrom(_fingerStart);
            AsiKeyFingers.OffsetPose(project, pose, _fingerHand, curlDeg, spreadDeg, staggerDeg, _fingerBones);
            CommitEdits();
        }

        /// <summary>松开。开着自动关键点时给改过的指节打注册关键帧。</summary>
        public void EndFingerPose()
        {
            if (!_fingerDrag)
                return;
            _fingerDrag = false;
            if (!autoKey)
                return;
            for (int i = 0; i < _fingerBones.Count; i++)
                _pendingKey.Add(_fingerBones[i]);
        }

        /// <summary>取消拖动，回到按下时的姿势。</summary>
        public void CancelFingerPose()
        {
            if (!_fingerDrag || !Valid)
            {
                _fingerDrag = false;
                return;
            }
            _fingerDrag = false;
            pose.CopyFrom(_fingerStart);
            _editedChains.Clear();
            WriteAll();
            _poseDirty = true;
        }

        /// <summary>打开或关掉这根手指的 IK。返回错误文本，成功时为空。</summary>
        public string SetFingerIK(AsiFingerRef f, bool on)
        {
            if (!Valid)
                return "没有打开的 K 帧工程";
            AsiKeyPanels.ClearKeySelection();
            Undo.RecordObject(project, on ? "启用手指 IK" : "关闭手指 IK");
            int index;
            string err = AsiKeyFingers.SetChain(project, f, on, out index);
            if (!string.IsNullOrEmpty(err))
            {
                status = err;
                return err;
            }
            if (!on && index < 0)
                return "";
            if (on && index >= 0)
                RecomputeFreeTargets(index);
            EditorUtility.SetDirty(project);
            Refresh();
            status = (on ? "已启用 " : "已关闭 ") + AsiKeyFingers.Label(f.side, f.id) + " IK";
            return "";
        }
    }
}
