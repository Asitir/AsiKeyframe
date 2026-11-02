using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 骨骼姿势与 Humanoid 肌肉互转。绑在场景骨骼上的 GetHumanPose 在编辑模式下读不到实际姿势，
    /// 这里按 Avatar 关节路径把局部姿势喂进 SetInternalAvatarPose，再读内部人体姿势。
    /// </summary>
    public sealed class AsiKeyHumanBridge : IDisposable
    {
        public readonly Avatar avatar;
        public readonly string[] jointPaths;
        public Transform[] joints;
        private readonly bool[] _human;
        private readonly int _hipsJoint = -1;
        private HumanPoseHandler _handler;
        private NativeArray<float> _avatarPose;
        public HumanPose pose;

        private AsiKeyHumanBridge(Avatar avatar, string[] paths, Transform root)
        {
            this.avatar = avatar;
            jointPaths = paths;
            _handler = new HumanPoseHandler(avatar, paths);
            _avatarPose = new NativeArray<float>(paths.Length * 7, Allocator.Persistent);
            pose = new HumanPose();
            pose.muscles = new float[HumanTrait.MuscleCount];

            HashSet<string> humanNames = new HashSet<string>();
            string hipsName = "";
            HumanBone[] hb = avatar.humanDescription.human;
            for (int i = 0; i < hb.Length; i++)
            {
                humanNames.Add(hb[i].boneName);
                if (hb[i].humanName == "Hips")
                    hipsName = hb[i].boneName;
            }
            _human = new bool[paths.Length];
            for (int i = 1; i < paths.Length; i++)
            {
                string n = paths[i];
                int slash = n.LastIndexOf('/');
                string leaf = slash >= 0 ? n.Substring(slash + 1) : n;
                _human[i] = humanNames.Contains(leaf);
                if (leaf == hipsName)
                    _hipsJoint = i;
            }
            Bind(root);
        }

        public static AsiKeyHumanBridge Create(Avatar avatar, Transform root, out string error)
        {
            error = "";
            if (avatar == null || !avatar.isHuman)
            {
                error = "Avatar 不是 Humanoid";
                return null;
            }
            string[] paths = BuildAvatarJointPaths(avatar, root);
            if (paths.Length < 2)
            {
                error = "Avatar 骨架是空的";
                return null;
            }
            return new AsiKeyHumanBridge(avatar, paths, root);
        }

        public void Bind(Transform root)
        {
            joints = new Transform[jointPaths.Length];
            joints[0] = root;
            for (int i = 1; i < jointPaths.Length; i++)
                joints[i] = root != null ? root.Find(jointPaths[i]) : null;
        }

        public bool IsHumanJoint(int i)
        {
            return i > 0 && i < _human.Length && _human[i];
        }

        public void Read()
        {
            for (int i = 0; i < joints.Length; i++)
            {
                Transform j = i > 0 ? joints[i] : null;
                Vector3 p = j != null ? j.localPosition : Vector3.zero;
                Quaternion r = j != null ? j.localRotation : Quaternion.identity;
                int o = i * 7;
                _avatarPose[o] = p.x;
                _avatarPose[o + 1] = p.y;
                _avatarPose[o + 2] = p.z;
                _avatarPose[o + 3] = r.x;
                _avatarPose[o + 4] = r.y;
                _avatarPose[o + 5] = r.z;
                _avatarPose[o + 6] = r.w;
            }
            _handler.SetInternalAvatarPose(_avatarPose);
            _handler.GetInternalHumanPose(ref pose);
        }

        /// <summary>经过肌肉往返一次，把 Humanoid 限位与重定向后的效果写回人形骨骼。</summary>
        public void Retarget()
        {
            Read();
            _handler.SetInternalHumanPose(ref pose);
            _handler.GetInternalAvatarPose(_avatarPose);
            for (int i = 1; i < joints.Length; i++)
            {
                if (!_human[i] || joints[i] == null)
                    continue;
                int o = i * 7;
                Quaternion r = new Quaternion(_avatarPose[o + 3], _avatarPose[o + 4], _avatarPose[o + 5], _avatarPose[o + 6]);
                if (AsiKeyMath.Finite(r) && (r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w) > 1e-6f)
                    joints[i].localRotation = AsiKeyMath.Norm(r);
                if (i == _hipsJoint)
                {
                    Vector3 p = new Vector3(_avatarPose[o], _avatarPose[o + 1], _avatarPose[o + 2]);
                    if (AsiKeyMath.Finite(p))
                        joints[i].localPosition = p;
                }
            }
        }

        public void Dispose()
        {
            if (_avatarPose.IsCreated)
                _avatarPose.Dispose();
            if (_handler != null)
            {
                _handler.Dispose();
                _handler = null;
            }
        }

        public static string[] BuildAvatarJointPaths(Avatar avatar, Transform root)
        {
            SkeletonBone[] skeleton = avatar.humanDescription.skeleton;
            HashSet<string> names = new HashSet<string>();
            for (int i = 0; i < skeleton.Length; i++)
                names.Add(skeleton[i].name);
            List<string> paths = new List<string>();
            Collect(root, "", names, paths);
            return paths.ToArray();
        }

        private static void Collect(Transform node, string path, HashSet<string> names, List<string> paths)
        {
            if (path.Length == 0 || names.Contains(node.name))
                paths.Add(path);
            for (int i = 0; i < node.childCount; i++)
            {
                Transform child = node.GetChild(i);
                Collect(child, path.Length == 0 ? child.name : path + "/" + child.name, names, paths);
            }
        }

        /// <summary>HumanTrait 的手指肌肉名是 "Left Thumb 1 Stretched"，动画曲线要写成 "LeftHand.Thumb.1 Stretched"。</summary>
        public static string MuscleCurveName(string muscleName)
        {
            string side;
            string rest;
            if (StartsWithFinger(muscleName, "Left "))
            {
                side = "LeftHand.";
                rest = muscleName.Substring(5);
            }
            else if (StartsWithFinger(muscleName, "Right "))
            {
                side = "RightHand.";
                rest = muscleName.Substring(6);
            }
            else
            {
                return muscleName;
            }
            int space = rest.IndexOf(' ');
            if (space < 0)
                return side + rest;
            return side + rest.Substring(0, space) + "." + rest.Substring(space + 1);
        }

        private static bool StartsWithFinger(string muscleName, string side)
        {
            if (!muscleName.StartsWith(side, StringComparison.Ordinal))
                return false;
            string rest = muscleName.Substring(side.Length);
            return rest.StartsWith("Thumb", StringComparison.Ordinal)
                || rest.StartsWith("Index", StringComparison.Ordinal)
                || rest.StartsWith("Middle", StringComparison.Ordinal)
                || rest.StartsWith("Ring", StringComparison.Ordinal)
                || rest.StartsWith("Little", StringComparison.Ordinal);
        }
    }
}
