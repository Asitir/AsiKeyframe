using System;
using System.Collections.Generic;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>外形预设：人形骨骼按 HumanBodyBones 匹配，其余按骨骼名匹配。</summary>
    public class AsiKeyShapePreset : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public int humanBone = -1;
            public bool isCom;
            public string boneName = "";
            public AsiKeyShape shape = new AsiKeyShape();
        }

        public List<Entry> entries = new List<Entry>();

        public void Capture(AsiKeyProject p)
        {
            entries.Clear();
            for (int i = 0; i < p.bones.Count; i++)
            {
                AsiKeyBone b = p.bones[i];
                Entry e = new Entry();
                e.humanBone = b.humanBone;
                e.isCom = b.isCom;
                e.boneName = b.name;
                e.shape = b.shape.Clone();
                entries.Add(e);
            }
        }

        public int Apply(AsiKeyProject p, List<int> onlyBones)
        {
            int applied = 0;
            for (int i = 0; i < p.bones.Count; i++)
            {
                if (onlyBones != null && onlyBones.Count > 0 && !onlyBones.Contains(i))
                    continue;
                Entry e = Find(p.bones[i]);
                if (e == null)
                    continue;
                p.bones[i].shape = e.shape.Clone();
                applied++;
            }
            return applied;
        }

        private Entry Find(AsiKeyBone b)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (b.isCom && e.isCom)
                    return e;
                if (b.humanBone >= 0 && e.humanBone == b.humanBone)
                    return e;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].boneName == b.name)
                    return entries[i];
            }
            return null;
        }
    }
}
