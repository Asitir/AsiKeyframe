using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AsitirTool.Editor
{
    public static class AsiKeyUtil
    {
        public static string GetId(Object o)
        {
            if (o == null)
                return "";
            return GlobalObjectId.GetGlobalObjectIdSlow(o).ToString();
        }

        public static Object FromId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            GlobalObjectId gid;
            if (!GlobalObjectId.TryParse(id, out gid))
                return null;
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
        }

        public static string ScenePath(Transform t)
        {
            if (t == null)
                return "";
            List<string> parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static Transform FindScenePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            int slash = path.IndexOf('/');
            string rootName = slash < 0 ? path : path.Substring(0, slash);
            string rest = slash < 0 ? "" : path.Substring(slash + 1);
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;
                GameObject[] roots = scene.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    if (roots[r].name != rootName)
                        continue;
                    Transform t = rest.Length == 0 ? roots[r].transform : roots[r].transform.Find(rest);
                    if (t != null)
                        return t;
                }
            }
            return null;
        }

        public static Transform ResolveTransform(string id, string path)
        {
            Object o = FromId(id);
            GameObject go = o as GameObject;
            if (go != null)
                return go.transform;
            Component c = o as Component;
            if (c != null)
                return c.transform;
            return FindScenePath(path);
        }

        /// <summary>
        /// IK 物体引用写成 "GlobalObjectId|场景路径"，未保存场景里 id 解析不了时按路径找。
        /// 播放模式下的物体是场景的临时副本，id 退出后对不上，只记路径。
        /// </summary>
        public static string EncodeObjectRef(Transform t)
        {
            if (t == null)
                return "";
            return (EditorApplication.isPlaying ? "" : GetId(t.gameObject)) + "|" + ScenePath(t);
        }

        public static Transform DecodeObjectRef(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                return null;
            int bar = encoded.IndexOf('|');
            string id = bar < 0 ? encoded : encoded.Substring(0, bar);
            string path = bar < 0 ? "" : encoded.Substring(bar + 1);
            return ResolveTransform(id, path);
        }

        public static string ObjectRefName(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                return "";
            int slash = encoded.LastIndexOf('/');
            int bar = encoded.IndexOf('|');
            if (slash >= 0)
                return encoded.Substring(slash + 1);
            return bar >= 0 ? encoded.Substring(bar + 1) : encoded;
        }

        public static string ToAssetPath(string absolute)
        {
            if (string.IsNullOrEmpty(absolute))
                return "";
            string full = Path.GetFullPath(absolute).Replace('\\', '/');
            string data = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (full.StartsWith(data))
                return "Assets" + full.Substring(data.Length);
            return "";
        }

        public static void ToRig(Transform rig, Vector3 worldPos, Quaternion worldRot, out Vector3 pos, out Quaternion rot)
        {
            Quaternion inv = Quaternion.Inverse(rig.rotation);
            pos = inv * (worldPos - rig.position);
            rot = inv * worldRot;
        }
    }
}
