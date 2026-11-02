using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>姿势库里的一根骨骼：相对所属单位坐标系的世界姿态，按人形骨骼 / 路径 / 名字匹配到目标角色。</summary>
    [Serializable]
    public class AsiKeyPoseBone
    {
        public string name = "";
        public string path = "";
        public int humanBone = -1;
        public bool isCom;
        /// <summary>复制时选中（局部姿势只粘贴这些）；全身姿势全为 true。</summary>
        public bool copied;
        public Quaternion rot = Quaternion.identity;
        public Vector3 pos;
    }

    [Serializable]
    public class AsiKeyPoseEntry
    {
        public string name = "";
        public bool fullBody;
        public string unitName = "";
        public string created = "";
        /// <summary>缩略图 PNG。</summary>
        public byte[] thumbnail = new byte[0];
        /// <summary>单位的全部骨骼（局部姿势也存全部，用来换算相对父级的朝向）。</summary>
        public List<AsiKeyPoseBone> bones = new List<AsiKeyPoseBone>();

        public int CopiedCount
        {
            get
            {
                int n = 0;
                for (int k = 0; k < bones.Count; k++)
                {
                    if (bones[k].copied)
                        n++;
                }
                return n;
            }
        }
    }

    [Serializable]
    public class AsiKeyPoseCategory
    {
        public string name = "";
        public List<AsiKeyPoseEntry> poses = new List<AsiKeyPoseEntry>();
    }

    /// <summary>全局姿势库，所有 K帧工程共用。至少有一个「默认」分类。</summary>
    public class AsiKeyPoseLibrary : ScriptableObject
    {
        public const string DefaultPath = "Assets/AsitirMapTool/AsiKeyframe/Data/AsiKeyPoseLibrary.asset";
        public const string DefaultCategory = "默认";

        public List<AsiKeyPoseCategory> categories = new List<AsiKeyPoseCategory>();

        private static AsiKeyPoseLibrary s_instance;

        /// <summary>项目里的姿势库资产，没有时在 <see cref="DefaultPath"/> 新建。</summary>
        public static AsiKeyPoseLibrary Instance
        {
            get
            {
                if (s_instance != null)
                    return s_instance;
                string[] guids = AssetDatabase.FindAssets("t:" + typeof(AsiKeyPoseLibrary).Name);
                for (int k = 0; k < guids.Length && s_instance == null; k++)
                    s_instance = AssetDatabase.LoadAssetAtPath<AsiKeyPoseLibrary>(AssetDatabase.GUIDToAssetPath(guids[k]));
                if (s_instance == null)
                {
                    s_instance = CreateInstance<AsiKeyPoseLibrary>();
                    string dir = System.IO.Path.GetDirectoryName(DefaultPath);
                    if (!AssetDatabase.IsValidFolder(dir))
                        System.IO.Directory.CreateDirectory(dir);
                    AssetDatabase.CreateAsset(s_instance, DefaultPath);
                }
                s_instance.EnsureDefault();
                return s_instance;
            }
        }

        public void EnsureDefault()
        {
            if (categories.Count > 0)
                return;
            categories.Add(new AsiKeyPoseCategory { name = DefaultCategory });
            Save();
        }

        public AsiKeyPoseCategory Category(int index)
        {
            EnsureDefault();
            return categories[Mathf.Clamp(index, 0, categories.Count - 1)];
        }

        public string UniqueCategoryName(string baseName)
        {
            string n = baseName;
            for (int k = 2; CategoryIndex(n) >= 0; k++)
                n = baseName + " " + k;
            return n;
        }

        public int CategoryIndex(string categoryName)
        {
            for (int k = 0; k < categories.Count; k++)
            {
                if (categories[k].name == categoryName)
                    return k;
            }
            return -1;
        }

        public void Save()
        {
            EditorUtility.SetDirty(this);
            if (AssetDatabase.Contains(this))
                AssetDatabase.SaveAssetIfDirty(this);
        }
    }
}
