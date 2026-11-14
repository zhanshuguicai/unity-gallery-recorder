// 热键：优先用新版 Input System；游戏没有它（或只开了旧版输入）时改用 UnityEngine.Input。
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace GalleryRecorder
{
    public static class Hotkeys
    {
        private static bool inputSystemMissing, legacyBroken;
        private static readonly Dictionary<string, KeyCode> legacyCache = new Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase);

        /// <summary>这一帧是否刚按下。name 用按键名，例如 "F8"。</summary>
        public static bool Pressed(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Equals("None", StringComparison.OrdinalIgnoreCase)) return false;
            name = name.Trim();

            if (!inputSystemMissing)
            {
                try
                {
                    bool? r = InputSystemKeys.Pressed(name);
                    if (r.HasValue) return r.Value;   // 有键盘设备：以 Input System 为准
                }
                catch (Exception) { inputSystemMissing = true; } // 游戏没带 Unity.InputSystem
            }

            // 没有 Input System，或游戏只启用了旧版输入（Input System 里没有键盘设备）
            if (legacyBroken) return false;
            try
            {
                if (!legacyCache.TryGetValue(name, out var kc))
                {
                    try { kc = (KeyCode)Enum.Parse(typeof(KeyCode), name, true); } catch { kc = KeyCode.None; }
                    legacyCache[name] = kc;
                }
                return kc != KeyCode.None && Input.GetKeyDown(kc);
            }
            catch (Exception)
            {
                // 游戏只启用了新版输入时旧版 Input 会抛异常；以后不再尝试
                legacyBroken = true;
                if (inputSystemMissing) GalleryRecorderPlugin.Log?.LogWarning("游戏里读不到键盘输入，热键不可用。");
                return false;
            }
        }
    }

    /// <summary>单独放一个类：游戏没有 Unity.InputSystem.dll 时，只有调用这里才会出错，可以被上面捕获。</summary>
    internal static class InputSystemKeys
    {
        private static readonly Dictionary<string, UnityEngine.InputSystem.Key> cache =
            new Dictionary<string, UnityEngine.InputSystem.Key>(StringComparer.OrdinalIgnoreCase);

        /// <summary>null = Input System 不可用（例如还没有键盘设备）。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool? Pressed(string name)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return null;
            if (!cache.TryGetValue(name, out var key))
            {
                try { key = (UnityEngine.InputSystem.Key)Enum.Parse(typeof(UnityEngine.InputSystem.Key), name, true); }
                catch { key = UnityEngine.InputSystem.Key.None; }
                cache[name] = key;
            }
            if (key == UnityEngine.InputSystem.Key.None) return false;
            var ctl = kb[key];
            return ctl != null && ctl.wasPressedThisFrame;
        }
    }
}
