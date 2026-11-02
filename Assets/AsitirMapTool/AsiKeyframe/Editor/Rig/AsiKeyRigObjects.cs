using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 场景里的控制器物体：根物体放在场景根部（跟随工程父级），每个单位一个子根「[U] 单位名」（跟随单位坐标系），
    /// 每根骨骼一个空 Transform，全部打 EditorOnly 标签。
    /// 控制器只在会话期间存在：不显示在 Hierarchy、不存进场景，会话结束时销毁，下次按工程数据重建。
    /// </summary>
    public static class AsiKeyRigObjects
    {
        public const string Tag = "EditorOnly";
        public const string UnitPrefix = "[U] ";
        public const HideFlags Flags = HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor;

        private static GameObject NewObject(string name)
        {
            GameObject go = EditorUtility.CreateGameObjectWithHideFlags(name, Flags);
            go.tag = Tag;
            return go;
        }

        public static Transform CreateRoot(AsiKeyProject p, Transform projectRoot, Scene scene)
        {
            GameObject go = NewObject(AsiKeyRigBuilder.RigRootName(p));
            if (scene.IsValid() && go.scene != scene)
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            Transform root = go.transform;
            if (projectRoot != null)
                root.SetPositionAndRotation(projectRoot.position, projectRoot.rotation);
            return root;
        }

        /// <summary>删掉已加载场景里的全部控制器根（包括旧版本存进场景的那种）。</summary>
        public static void DestroyAll()
        {
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;
                GameObject[] roots = scene.GetRootGameObjects();
                for (int k = 0; k < roots.Length; k++)
                {
                    Transform t = roots[k].transform;
                    if (!IsRigRoot(t))
                        continue;
                    if ((roots[k].hideFlags & HideFlags.DontSaveInEditor) == 0)
                        MarkDirty(t);
                    Object.DestroyImmediate(roots[k]);
                }
            }
        }

        public static bool IsRigRoot(Transform t)
        {
            return t != null && t.parent == null && t.name.StartsWith("[AsiKeyRig]");
        }

        /// <summary>t 在某套 [AsiKeyRig] 控制器下面（含根本身）。</summary>
        public static bool IsRigObject(Transform t)
        {
            return t != null && IsRigRoot(t.root);
        }

        public static string UnitRootName(AsiKeyProject p, int u)
        {
            return UnitPrefix + (p.ValidUnit(u) ? p.units[u].name : "_");
        }

        private static bool IsUnitRoot(Transform root, Transform t)
        {
            return t.parent == root && t.name.StartsWith(UnitPrefix);
        }

        private static void Collect(Transform under, Dictionary<string, Transform> byName)
        {
            Transform[] all = under.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != under && !byName.ContainsKey(all[i].name))
                    byName[all[i].name] = all[i];
            }
        }

        /// <summary>
        /// 按骨骼定义补齐、重挂、删除多余控制器与单位子根。返回下标对应的控制器数组。
        /// </summary>
        public static Transform[] Sync(AsiKeyProject p, Transform root, int[] order, out Transform[] unitRoots)
        {
            int unitCount = p.units.Count;
            bool changed = false;
            Dictionary<string, Transform> subByName = new Dictionary<string, Transform>();
            List<Transform> loose = new List<Transform>();
            for (int c = 0; c < root.childCount; c++)
            {
                Transform ch = root.GetChild(c);
                if (IsUnitRoot(root, ch))
                {
                    if (!subByName.ContainsKey(ch.name))
                        subByName[ch.name] = ch;
                }
                else
                {
                    loose.Add(ch);
                }
            }

            unitRoots = new Transform[unitCount];
            Dictionary<string, Transform>[] byName = new Dictionary<string, Transform>[unitCount];
            for (int u = 0; u < unitCount; u++)
            {
                string n = UnitRootName(p, u);
                Transform ur;
                if (subByName.TryGetValue(n, out ur) && ur != null)
                {
                    subByName.Remove(n);
                }
                else
                {
                    ur = NewObject(n).transform;
                    ur.SetParent(root, false);
                    changed = true;
                }
                if (ur.localScale != Vector3.one)
                    ur.localScale = Vector3.one;
                unitRoots[u] = ur;
                byName[u] = new Dictionary<string, Transform>();
                Collect(ur, byName[u]);
            }

            Transform[] controls = new Transform[p.bones.Count];
            for (int o = 0; o < order.Length; o++)
            {
                int i = order[o];
                AsiKeyBone b = p.bones[i];
                int u = Mathf.Clamp(b.unit, 0, unitCount - 1);
                Transform parent = b.parent >= 0 && controls[b.parent] != null ? controls[b.parent] : unitRoots[u];
                Transform t;
                if (byName[u].TryGetValue(b.name, out t) && t != null && byName[u].Remove(b.name))
                {
                    if (t.parent != parent)
                    {
                        t.SetParent(parent, false);
                        changed = true;
                    }
                }
                else
                {
                    t = NewObject(b.name).transform;
                    t.SetParent(parent, false);
                    t.localPosition = b.restPos;
                    t.localRotation = b.restRot;
                    changed = true;
                }
                if (t.localScale != Vector3.one)
                    t.localScale = Vector3.one;
                if (!t.gameObject.CompareTag(Tag))
                    t.gameObject.tag = Tag;
                controls[i] = t;
            }

            HashSet<Transform> keep = new HashSet<Transform>(controls);
            for (int u = 0; u < unitCount; u++)
                keep.Add(unitRoots[u]);
            List<Transform> extra = new List<Transform>();
            for (int u = 0; u < unitCount; u++)
                extra.AddRange(byName[u].Values);
            extra.AddRange(loose);
            extra.AddRange(subByName.Values);
            for (int k = 0; k < extra.Count; k++)
            {
                Transform t = extra[k];
                if (t == null || keep.Contains(t))
                    continue;
                // 删除前把仍要保留的子级移出来，避免连带删掉。
                for (int c = t.childCount - 1; c >= 0; c--)
                {
                    Transform ch = t.GetChild(c);
                    if (ContainsKept(ch, keep))
                        ch.SetParent(root, false);
                }
                Object.DestroyImmediate(t.gameObject);
                changed = true;
            }
            if (changed)
            {
                // 移出来的保留物体重挂一遍。
                for (int o = 0; o < order.Length; o++)
                {
                    int i = order[o];
                    AsiKeyBone b = p.bones[i];
                    int u = Mathf.Clamp(b.unit, 0, unitCount - 1);
                    Transform parent = b.parent >= 0 && controls[b.parent] != null ? controls[b.parent] : unitRoots[u];
                    if (controls[i] != null && controls[i].parent != parent)
                        controls[i].SetParent(parent, false);
                }
            }
            return controls;
        }

        private static bool ContainsKept(Transform t, HashSet<Transform> keep)
        {
            if (keep.Contains(t))
                return true;
            for (int c = 0; c < t.childCount; c++)
            {
                if (ContainsKept(t.GetChild(c), keep))
                    return true;
            }
            return false;
        }

        public static void DestroyRoot(Transform root)
        {
            if (root == null)
                return;
            Object.DestroyImmediate(root.gameObject);
        }

        private static void MarkDirty(Transform t)
        {
            if (t != null && t.gameObject.scene.IsValid() && !EditorApplication.isPlaying)
                EditorSceneManager.MarkSceneDirty(t.gameObject.scene);
        }
    }
}
