// 备选录制方式：发送 Win+Alt+R 让 Xbox Game Bar 开始/停止录制，停止后把 Captures 目录里新生成的 mp4 移到输出目录并改名。
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using UnityEngine;

namespace GalleryRecorder
{
    internal sealed class GameBarRecorder
    {
        private readonly ManualLogSource log;
        private DateTime startedAt;
        private string captureDir;

        public bool IsRecording { get; private set; }
        public string CurrentOutput { get; private set; }

        public GameBarRecorder(ManualLogSource log) { this.log = log; }

        public void Start(string output, string captureDir)
        {
            CurrentOutput = output;
            this.captureDir = captureDir;
            startedAt = DateTime.Now.AddSeconds(-2);
            if (!Application.isFocused) log.LogWarning("游戏窗口不在前台，Game Bar 可能录不到游戏。请保持游戏窗口在前台。");
            SendWinAltR();
            IsRecording = true;
            log.LogInfo("已发送 Win+Alt+R（Game Bar 开始录制）→ " + output);
        }

        /// <summary>停止录制；save=true 时等待 Game Bar 写完文件并移动改名。</summary>
        public Task<bool> Stop(bool save = true)
        {
            if (!IsRecording) return Task.FromResult(false);
            IsRecording = false;
            SendWinAltR();
            log.LogInfo("已发送 Win+Alt+R（Game Bar 停止录制）");
            string output = CurrentOutput, dir = captureDir;
            DateTime since = startedAt;
            return Task.Run(() =>
            {
                // Game Bar 停止后需要几秒钟写完文件
                for (int i = 0; i < 120; i++)
                {
                    Thread.Sleep(1000);
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        var f = new DirectoryInfo(dir).GetFiles("*.mp4")
                            .Where(x => x.CreationTime >= since)
                            .OrderByDescending(x => x.CreationTime).FirstOrDefault();
                        if (f == null) continue;
                        if (!save) { log.LogInfo("Game Bar 录像未保存（已放弃）：" + f.FullName); return false; }
                        try { using (f.Open(FileMode.Open, FileAccess.Read, FileShare.None)) { } }
                        catch (IOException) { continue; } // 仍在写入
                        Recorder.TryDelete(output);
                        File.Move(f.FullName, output);
                        log.LogInfo("已保存：" + output);
                        return true;
                    }
                    catch (Exception ex) { log.LogWarning("整理 Game Bar 录像出错：" + ex.Message); }
                }
                log.LogError($"2 分钟内没在 {dir} 找到新的 Game Bar 录像。确认 Game Bar 已开启、保存目录正确（配置 GameBarCaptureDir）。");
                return false;
            });
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        private const uint KEYUP = 0x0002;
        private const byte VK_LWIN = 0x5B, VK_MENU = 0x12, VK_R = 0x52;

        private static void SendWinAltR()
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_R, 0, 0, UIntPtr.Zero);
            keybd_event(VK_R, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYUP, UIntPtr.Zero);
        }
    }
}
