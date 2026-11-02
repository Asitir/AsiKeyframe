using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 模型骨骼预览。写入前先登记到 AnimationMode，停止时由 AnimationMode 还原；
    /// 同时自己备份一份原始局部变换，存场景、重载程序集和停止时都按备份还原，保证不改动场景数据。
    /// 播放模式下 AnimationMode 不可用：直接写 Transform，并暂停会覆盖骨骼的 Animator，停止时还原姿势、恢复 Animator。
    /// </summary>
    public class AsiKeyPreview
    {
        private struct Backup
        {
            public Vector3 pos;
            public Quaternion rot;
            public Vector3 scale;
        }

        private static readonly string[] PosProps = { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z" };
        private static readonly string[] RotProps = { "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };

        private AnimationModeDriver _driver;
        private readonly Dictionary<Transform, Backup> _backup = new Dictionary<Transform, Backup>();
        private readonly List<Animator> _paused = new List<Animator>();
        private bool _direct;

        public bool Active
        {
            get { return _direct || _driver != null && AnimationMode.InAnimationMode(_driver); }
        }

        public void Begin(AnimationModeDriver driver)
        {
            _driver = driver;
            if (EditorApplication.isPlaying)
            {
                if (!_direct)
                {
                    _direct = true;
                    _backup.Clear();
                }
                return;
            }
            if (_driver != null && !AnimationMode.InAnimationMode(_driver))
            {
                AnimationMode.StartAnimationMode(_driver);
                _backup.Clear();
            }
        }

        /// <summary>登记一个要写入的物体。root 是它所属单位的目标（路径相对它计算）。</summary>
        public void Register(Transform t, Transform root)
        {
            if (t == null || _backup.ContainsKey(t))
                return;
            Backup b;
            b.pos = t.localPosition;
            b.rot = t.localRotation;
            b.scale = t.localScale;
            _backup[t] = b;
            if (_direct || !Active)
                return;
            string path = root == null || t == root || !t.IsChildOf(root) ? "" : AnimationUtility.CalculateTransformPath(t, root);
            Vector3 p = b.pos;
            Quaternion r = b.rot;
            float[] pv = { p.x, p.y, p.z };
            float[] rv = { r.x, r.y, r.z, r.w };
            for (int i = 0; i < 3; i++)
                AddModification(t, path, PosProps[i], pv[i]);
            for (int i = 0; i < 4; i++)
                AddModification(t, path, RotProps[i], rv[i]);
        }

        private static void AddModification(Transform t, string path, string property, float value)
        {
            EditorCurveBinding binding = EditorCurveBinding.FloatCurve(path, typeof(Transform), property);
            PropertyModification mod = new PropertyModification();
            mod.target = t;
            mod.propertyPath = property;
            mod.value = value.ToString(CultureInfo.InvariantCulture);
            AnimationMode.AddPropertyModification(binding, mod, true);
        }

        /// <summary>把模型临时还原成原始姿势，不清空登记。</summary>
        public void Restore()
        {
            foreach (KeyValuePair<Transform, Backup> pair in _backup)
            {
                Transform t = pair.Key;
                if (t == null)
                    continue;
                t.localPosition = pair.Value.pos;
                t.localRotation = pair.Value.rot;
                t.localScale = pair.Value.scale;
            }
        }

        /// <summary>播放模式下暂停 Animator（只暂停原本开着的），End 时恢复。</summary>
        public void Pause(Animator a)
        {
            if (!_direct || a == null || !a.enabled || _paused.Contains(a))
                return;
            a.enabled = false;
            _paused.Add(a);
        }

        public void End()
        {
            if (_driver != null && AnimationMode.InAnimationMode(_driver))
                AnimationMode.StopAnimationMode(_driver);
            Restore();
            _backup.Clear();
            for (int i = 0; i < _paused.Count; i++)
            {
                if (_paused[i] != null)
                    _paused[i].enabled = true;
            }
            _paused.Clear();
            _direct = false;
        }
    }
}
