using System.Collections.Generic;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 把 Unity Scene 视图的临时缩放（默认 Alt+右键拖动）改绑到 Ctrl+Alt+中键，Alt+右键单击留给 K帧的 Alt 菜单。
    /// 每次加载脚本时检查，缩放还在 Alt+右键就改（用菜单恢复后不再自动改）；Unity 自带的 Default 快捷键配置只读，这时新建并启用「AsiKeyframe」配置。
    /// 快捷键配置是 Unity 全局的，所有工程都生效。
    /// </summary>
    public static class AsiKeyZoomShortcut
    {
        public const string Profile = "AsiKeyframe";
        private const string PrefKeepUnity = "AsiKey.ZoomKeepUnity";
        private const string ZoomPrefix = "Scene View/Temporary Zoom Tool";

        [InitializeOnLoadMethod]
        private static void AutoRebind()
        {
            if (EditorPrefs.GetBool(PrefKeepUnity, false))
                return;
            EditorApplication.delayCall += () =>
            {
                if (!EditorPrefs.GetBool(PrefKeepUnity, false))
                    Rebind();
            };
        }

        /// <summary>把绑定在 Alt+右键的缩放改成 Ctrl+Alt+中键，返回改了几个。</summary>
        public static int Rebind()
        {
            IShortcutManager sm = ShortcutManager.instance;
            List<string> ids = ZoomIds(sm, true);
            if (ids.Count == 0)
                return 0;
            if (!EnsureWritableProfile(sm))
                return 0;
            ShortcutBinding to = new ShortcutBinding(new KeyCombination(KeyCode.Mouse2, ShortcutModifiers.Action | ShortcutModifiers.Alt));
            for (int k = 0; k < ids.Count; k++)
                sm.RebindShortcut(ids[k], to);
            Debug.Log("[K帧工具] Scene 视图缩放已从 Alt+右键改为 Ctrl+Alt+中键（Unity 快捷键配置「" + sm.activeProfileId + "」），Alt+右键单击打开 K帧菜单。");
            return ids.Count;
        }

        /// <summary>恢复 Unity 默认的 Alt+右键缩放，之后不再自动改绑。</summary>
        [MenuItem("AsitirTool/K帧：恢复 Unity 的 Alt+右键缩放", false, 40)]
        public static void Restore()
        {
            EditorPrefs.SetBool(PrefKeepUnity, true);
            IShortcutManager sm = ShortcutManager.instance;
            if (sm.IsProfileReadOnly(sm.activeProfileId))
                return;
            List<string> ids = ZoomIds(sm, false);
            for (int k = 0; k < ids.Count; k++)
                sm.ClearShortcutOverride(ids[k]);
            Debug.Log("[K帧工具] Scene 视图缩放已恢复为 Unity 默认（Alt+右键），Alt+右键单击仍会打开 K帧菜单。");
        }

        [MenuItem("AsitirTool/K帧：缩放改为 Ctrl+Alt+中键", false, 41)]
        private static void RebindMenu()
        {
            EditorPrefs.DeleteKey(PrefKeepUnity);
            if (Rebind() == 0)
                Debug.Log("[K帧工具] 没有绑定在 Alt+右键的 Scene 视图缩放，不用改。");
        }

        public static bool IsAltRightZoom(ShortcutBinding b)
        {
            foreach (KeyCombination kc in b.keyCombinationSequence)
                return kc.keyCode == KeyCode.Mouse1 && kc.modifiers == ShortcutModifiers.Alt;
            return false;
        }

        private static List<string> ZoomIds(IShortcutManager sm, bool onlyAltRight)
        {
            List<string> ids = new List<string>();
            foreach (string id in sm.GetAvailableShortcutIds())
            {
                if (!id.StartsWith(ZoomPrefix))
                    continue;
                if (!onlyAltRight || IsAltRightZoom(sm.GetShortcutBinding(id)))
                    ids.Add(id);
            }
            return ids;
        }

        private static bool EnsureWritableProfile(IShortcutManager sm)
        {
            if (!sm.IsProfileReadOnly(sm.activeProfileId))
                return true;
            bool exists = false;
            foreach (string p in sm.GetAvailableProfileIds())
                exists |= p == Profile;
            if (!exists)
                sm.CreateProfile(Profile);
            sm.activeProfileId = Profile;
            return !sm.IsProfileReadOnly(sm.activeProfileId);
        }
    }
}
