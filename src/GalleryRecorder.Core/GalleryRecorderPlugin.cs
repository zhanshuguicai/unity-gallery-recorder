// Unity Gallery Recorder 核心（BepInEx 5 插件）
// 按 F8：依次自动播放扩展包列出的每一段回想，每段单独录成一个 mp4，录完自动开始下一段；
// 全部录完后把视频再压缩一遍（HEVC），可选打包 zip。进度会记住，中途退出后再按 F8 从没录的开始。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace GalleryRecorder
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class GalleryRecorderPlugin : BaseUnityPlugin
    {
        public const string GUID = "unitygalleryrecorder.core";
        public const string NAME = "Unity Gallery Recorder";
        public const string VERSION = "0.1.0";

        internal static ManualLogSource Log;
        private static GalleryRecorderPlugin instance;
        private static readonly List<GalleryAdapter> pendingAdapters = new List<GalleryAdapter>();

        /// <summary>扩展包在自己插件的 Awake 里调用。只注册当前游戏对应的扩展包。</summary>
        public static void Register(GalleryAdapter adapter)
        {
            if (adapter == null) return;
            if (instance == null) { pendingAdapters.Add(adapter); return; }
            instance.Attach(adapter);
        }

        // ---- 配置 ----
        private ConfigEntry<string> cfgOutputDir;
        private ConfigEntry<float> cfgDelayBetween;
        private ConfigEntry<float> cfgLaunchTimeout;
        private ConfigEntry<float> cfgFinishTimeout;
        private ConfigEntry<bool> cfgForceAuto;
        private ConfigEntry<bool> cfgAutoChoice;
        private ConfigEntry<float> cfgChoiceDelay;
        private ConfigEntry<float> cfgStuckTimeout;
        private ConfigEntry<float> cfgMaxMinutes;
        private ConfigEntry<int> cfgMaxRuns;
        private ConfigEntry<string> cfgMode;
        private ConfigEntry<int> cfgFps;
        private ConfigEntry<int> cfgWidth;
        private ConfigEntry<int> cfgHeight;
        private ConfigEntry<bool> cfgFlip;
        private ConfigEntry<string> cfgVideoArgs;
        private ConfigEntry<string> cfgAudioArgs;
        private ConfigEntry<string> cfgGameBarDir;
        private ConfigEntry<bool> cfgCompress;
        private ConfigEntry<string> cfgCompressArgs;
        private ConfigEntry<bool> cfgDeleteOriginal;
        private ConfigEntry<bool> cfgZip;
        private ConfigEntry<bool> cfgOverlay;
        private ConfigEntry<string> keyStart, keySkip, keyReset, keyOverlay, keyCompress;

        // ---- 运行状态 ----
        private enum Phase { Idle, Launching, Playing, Finishing, Waiting, Compressing }
        private Phase phase = Phase.Idle;
        private GalleryAdapter adapter;
        private readonly AdapterContext ctx = new AdapterContext();
        private bool stopAfterCurrent;
        private readonly List<GalleryEntry> queue = new List<GalleryEntry>();
        private int queueIndex;
        private GalleryEntry current;
        private int run;
        private float phaseSince, playStartedAt, lastProgressAt, readyWaitSince = -1f;
        private bool readyWaitWarned;
        private bool skippedCurrent, forceStopTried;
        private string status = "等待扩展包…";
        private readonly HashSet<string> done = new HashSet<string>();
        private readonly List<Task<bool>> finalizing = new List<Task<bool>>();
        private Recorder recorder;
        private GameBarRecorder gameBar;
        private string outDir, progressPath, ffmpegPath;
        private volatile string compressStatus;
        private Task compressTask;
        private Host host;

        private void Awake()
        {
            instance = this;
            Log = Logger;

            cfgOutputDir = Config.Bind("General", "OutputDir", "", "录像保存目录。留空 = 游戏目录\\GalleryRecordings");
            cfgDelayBetween = Config.Bind("General", "DelayBetweenSeconds", 2f, "一段结束、回到空闲后，等多少秒再开始下一段");
            cfgLaunchTimeout = Config.Bind("General", "LaunchTimeoutSeconds", 60f, "启动一段后这么多秒内没开始播放，就跳过");
            cfgFinishTimeout = Config.Bind("General", "FinishTimeoutSeconds", 30f, "一段播完后最多等这么多秒回到空闲状态");
            cfgForceAuto = Config.Bind("Playback", "ForceAuto", true, "播放时保持自动前进（AUTO）、关闭快进");
            cfgAutoChoice = Config.Bind("Playback", "AutoChoice", true, "出现选项时自动选择（具体选哪个由扩展包决定）");
            cfgChoiceDelay = Config.Bind("Playback", "ChoiceDelaySeconds", 1.5f, "选项出现后多少秒自动选择（让选项画面也录进去）");
            cfgStuckTimeout = Config.Bind("Playback", "StuckTimeoutSeconds", 180f, "播放超过这么多秒没有任何进展就强制结束（仅扩展包支持进度检测时生效）。0 = 不检测");
            cfgMaxMinutes = Config.Bind("Playback", "MaxMinutesPerEntry", 60f, "单段最长录制分钟数，超时强制结束");
            cfgMaxRuns = Config.Bind("Playback", "MaxRunsPerEntry", 16, "同一段因分支最多录几遍");
            cfgMode = Config.Bind("Record", "Mode", "Internal",
                "录制方式：Internal = 插件内置录制（只录游戏画面和游戏声音，窗口被挡住或在别的桌面也不影响）；GameBar = 用 Win+Alt+R 调 Xbox Game Bar 录制（游戏窗口必须在前台，会录到系统所有声音）");
            cfgFps = Config.Bind("Record", "Fps", 60, "录制帧率");
            cfgWidth = Config.Bind("Record", "Width", 1920, "输出宽度。0 = 跟随游戏窗口");
            cfgHeight = Config.Bind("Record", "Height", 1080, "输出高度。0 = 跟随游戏窗口");
            cfgFlip = Config.Bind("Record", "FlipVertical", false, "录出来的画面上下颠倒时改成 true（和显卡 API 有关，D3D11/D3D12 一般不需要）");
            cfgVideoArgs = Config.Bind("Record", "VideoEncoderArgs", "-c:v h264_mf -hw_encoding 1 -rate_control quality -quality 90",
                "录制时的视频编码参数（ffmpeg）。默认用显卡硬件 H.264；没有可用硬件编码时改成 -c:v libopenh264 -b:v 20M");
            cfgAudioArgs = Config.Bind("Record", "AudioEncoderArgs", "-c:a aac -b:a 256k", "合成时的音频编码参数");
            cfgGameBarDir = Config.Bind("Record", "GameBarCaptureDir", "", "Game Bar 的保存目录。留空 = 用户目录 Videos\\Captures");
            cfgCompress = Config.Bind("Compress", "CompressWhenFinished", true, "全部录完后自动压缩");
            cfgCompressArgs = Config.Bind("Compress", "CompressArgs", "-pix_fmt nv12 -c:v hevc_mf -hw_encoding 1 -rate_control quality -quality 60 -tag:v hvc1 -c:a aac -b:a 160k",
                "压缩用的 ffmpeg 参数（输出到 Compressed 子目录）。quality 越小文件越小");
            cfgDeleteOriginal = Config.Bind("Compress", "DeleteOriginalAfterCompress", false, "压缩成功后删除原始录像");
            cfgZip = Config.Bind("Compress", "PackZip", false, "压缩后再把 Compressed 目录打包成一个 zip（只归档，不再压缩）");
            cfgOverlay = Config.Bind("UI", "ShowOverlay", true, "不录制时在左上角显示状态（录制中自动隐藏，不会录进视频）");
            keyStart = Config.Bind("Hotkeys", "StartStop", "F8", "开始；运行中再按 = 录完当前这段后停止");
            keySkip = Config.Bind("Hotkeys", "AbortCurrent", "F7", "放弃当前这段（不保存，记为跳过）并继续下一段");
            keyReset = Config.Bind("Hotkeys", "ResetProgress", "F6", "清空进度记录（空闲时）");
            keyOverlay = Config.Bind("Hotkeys", "ToggleOverlay", "F9", "显示/隐藏状态");
            keyCompress = Config.Bind("Hotkeys", "CompressNow", "F10", "立刻压缩输出目录里已有的录像（空闲时）");

            string pluginDir = Path.GetDirectoryName(Info.Location);
            ffmpegPath = Path.Combine(pluginDir, "ffmpeg", "ffmpeg.exe");
            outDir = string.IsNullOrWhiteSpace(cfgOutputDir.Value)
                ? Path.Combine(Paths.GameRootPath, "GalleryRecordings")
                : Environment.ExpandEnvironmentVariables(cfgOutputDir.Value.Trim());
            progressPath = Path.Combine(outDir, "progress.txt");
            LoadProgress();

            // 游戏切场景时可能销毁 BepInEx 的管理对象，所以逻辑放在自己建的常驻对象上跑
            var go = new GameObject("UnityGalleryRecorderHost");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            host = go.AddComponent<Host>();
            host.Owner = this;
            host.StartCoroutine(CaptureLoop());

            ctx.Log = Log;
            ctx.Host = host;
            ctx.CoreConfig = Config;
            ctx.forceAuto = cfgForceAuto;
            ctx.autoChoice = cfgAutoChoice;
            ctx.choiceDelay = cfgChoiceDelay;
            ctx.isRecording = () => (recorder != null && recorder.IsRecording) || (gameBar != null && gameBar.IsRecording);
            ctx.markProgress = () => lastProgressAt = Time.realtimeSinceStartup;

            Log.LogInfo($"{NAME} {VERSION} 已加载。输出目录：{outDir}；ffmpeg：{(File.Exists(ffmpegPath) ? "OK" : "缺失！")}；已完成 {done.Count} 段。");
            foreach (var a in pendingAdapters) Attach(a);
            pendingAdapters.Clear();
        }

        private void Attach(GalleryAdapter a)
        {
            if (adapter != null)
            {
                Log.LogWarning($"已经有扩展包 {adapter.GameName}，忽略 {a.GameName}。一个游戏只需要装一个扩展包。");
                return;
            }
            a.Context = ctx;
            try
            {
                a.Initialize();
                adapter = a;
                status = $"按 {keyStart.Value} 开始";
                Log.LogInfo($"扩展包：{a.GameName}");
            }
            catch (Exception ex) { Log.LogError($"扩展包 {a.GameName} 初始化失败：{ex}"); }
        }

        /// <summary>常驻宿主：转发 Update / OnGUI，被销毁时才放弃录制。</summary>
        internal sealed class Host : MonoBehaviour
        {
            public GalleryRecorderPlugin Owner;
            private void Update() { Owner?.DoUpdate(); }
            private void OnGUI() { Owner?.DoGUI(); }
            private void OnDestroy() { Owner?.Shutdown(); }
        }

        // ================= 主循环 =================
        private void DoUpdate()
        {
            try { EnsureAudioTap(); Tick(); }
            catch (Exception ex) { Log.LogError(ex); }
        }

        private IEnumerator CaptureLoop()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                if (recorder != null && recorder.IsRecording)
                {
                    try { recorder.CaptureFrame(); }
                    catch (Exception ex) { Log.LogError(ex); }
                }
            }
        }

        private void Tick()
        {
            HandleHotkeys();
            if (adapter == null) return;
            float now = Time.realtimeSinceStartup;

            switch (phase)
            {
                case Phase.Idle:
                    break;

                case Phase.Launching:
                    status = "启动中：" + Describe();
                    if (adapter.IsPlaying)
                    {
                        StartRecording();
                        phase = Phase.Playing; phaseSince = now; playStartedAt = now; lastProgressAt = now;
                        forceStopTried = false;
                    }
                    else if (now - phaseSince > cfgLaunchTimeout.Value)
                    {
                        Log.LogWarning($"{cfgLaunchTimeout.Value:0} 秒内没有开始播放，跳过：{Describe()}");
                        MarkDone(current, "launch-timeout");
                        Advance();
                    }
                    break;

                case Phase.Playing:
                    if (!adapter.IsPlaying)
                    {
                        StopRecording(save: !skippedCurrent);
                        phase = Phase.Finishing; phaseSince = now;
                        break;
                    }
                    status = $"录制中 [{queueIndex + 1}/{queue.Count}] {Describe()}";
                    adapter.WhilePlaying();
                    CheckStuck(now);
                    break;

                case Phase.Finishing:
                    status = "收尾：" + Describe();
                    if (adapter.IsReadyForNext || now - phaseSince > cfgFinishTimeout.Value)
                    {
                        if (skippedCurrent) { skippedCurrent = false; Advance(); break; }
                        bool again = false;
                        try { again = adapter.ShouldReplay(current, run); } catch (Exception ex) { Log.LogError(ex); }
                        if (again && run < cfgMaxRuns.Value)
                        {
                            run++;
                            Log.LogInfo($"还有其他分支，再录一遍（第 {run} 遍）：{current}");
                            phase = Phase.Waiting; phaseSince = now;
                            break;
                        }
                        if (again) Log.LogWarning($"分支数达到上限 {cfgMaxRuns.Value}，不再重录：{current}");
                        MarkDone(current, "ok");
                        Advance();
                    }
                    break;

                case Phase.Waiting:
                    float wait = cfgDelayBetween.Value - (now - phaseSince);
                    status = $"{Mathf.Max(0f, wait):0.0}s 后开始（{queueIndex + 1}/{queue.Count}）";
                    if (stopAfterCurrent) { EndSession("已停止（按 " + keyStart.Value + " 继续）"); break; }
                    if (!adapter.IsReadyForNext)
                    {
                        if (readyWaitSince < 0f) readyWaitSince = now;
                        if (now - readyWaitSince < cfgFinishTimeout.Value)
                        {
                            status = "等待游戏回到空闲状态…"; phaseSince = now; break;
                        }
                        if (!readyWaitWarned) { readyWaitWarned = true; Log.LogWarning("等待空闲超时，直接开始下一段。"); }
                    }
                    if (wait <= 0f) { readyWaitSince = -1f; readyWaitWarned = false; LaunchCurrent(); }
                    break;

                case Phase.Compressing:
                    status = compressStatus ?? "压缩中…";
                    if (compressTask != null && compressTask.IsCompleted)
                    {
                        phase = Phase.Idle;
                        status = compressStatus ?? "完成";
                        Log.LogInfo(status);
                    }
                    break;
            }
        }

        private void CheckStuck(float now)
        {
            float stuck = cfgStuckTimeout.Value;
            bool isStuck = adapter.TracksProgress && stuck > 0f && now - lastProgressAt > stuck;
            bool timeUp = now - playStartedAt > cfgMaxMinutes.Value * 60f;
            if (!isStuck && !timeUp) return;
            Log.LogWarning((timeUp ? "超过最长时长" : "长时间无进展") + "，强制结束：" + Describe());
            lastProgressAt = now; playStartedAt = now;
            TryForceStop();
        }

        private void TryForceStop()
        {
            bool ok = false;
            try { ok = adapter.ForceStop(); } catch (Exception ex) { Log.LogError(ex); }
            if (!ok && !forceStopTried)
                Log.LogWarning($"扩展包 {adapter.GameName} 不支持强制结束，等这段自然播完（也可以在游戏里手动退出）。");
            forceStopTried = true;
        }

        private void HandleHotkeys()
        {
            if (Hotkeys.Pressed(keyOverlay.Value)) cfgOverlay.Value = !cfgOverlay.Value;
            if (adapter == null) return;

            if (Hotkeys.Pressed(keyStart.Value))
            {
                if (phase == Phase.Idle) BeginSession();
                else if (phase != Phase.Compressing)
                {
                    stopAfterCurrent = !stopAfterCurrent;
                    Log.LogInfo(stopAfterCurrent ? "将在当前这段录完后停止。" : "取消停止，继续录制。");
                }
            }
            if (Hotkeys.Pressed(keySkip.Value) && (phase == Phase.Playing || phase == Phase.Launching) && !skippedCurrent)
            {
                Log.LogWarning("手动放弃：" + Describe());
                StopRecording(save: false);
                MarkDone(current, "skipped");
                skippedCurrent = true;
                if (phase == Phase.Playing) TryForceStop();
                else Advance();
            }
            if (Hotkeys.Pressed(keyReset.Value) && phase == Phase.Idle)
            {
                done.Clear();
                Recorder.TryDelete(progressPath);
                status = "进度已清空";
                Log.LogInfo(status);
            }
            if (Hotkeys.Pressed(keyCompress.Value) && phase == Phase.Idle)
                StartCompress();
        }

        // ================= 会话 / 队列 =================
        private void BeginSession()
        {
            string reason;
            bool can;
            try { can = adapter.CanBeginSession(out reason); }
            catch (Exception ex) { can = false; reason = ex.Message; Log.LogError(ex); }
            if (!can) { status = reason ?? "现在不能开始"; Log.LogWarning(status); return; }
            if (!UseGameBar && !File.Exists(ffmpegPath))
            {
                status = "找不到 ffmpeg：" + ffmpegPath;
                Log.LogError(status);
                return;
            }

            IList<GalleryEntry> all;
            try { all = adapter.BuildQueue() ?? new List<GalleryEntry>(); }
            catch (Exception ex) { Log.LogError(ex); status = "列出回想失败，见日志"; return; }
            var seen = new HashSet<string>();
            var list = all.Where(e => e != null && !string.IsNullOrEmpty(e.Key) && seen.Add(e.Key)).ToList();

            queue.Clear();
            queue.AddRange(list.Where(e => !done.Contains(e.Key)));
            finalizing.Clear();
            queueIndex = 0;
            stopAfterCurrent = false;
            Log.LogInfo($"共 {list.Count} 段，已完成 {list.Count - queue.Count} 段，本次要录 {queue.Count} 段。");
            foreach (var e in queue) Log.LogInfo("  · " + e);
            if (queue.Count == 0)
            {
                status = $"没有需要录的了（{keyReset.Value} 清空进度可重录）";
                return;
            }
            Application.runInBackground = true;
            Directory.CreateDirectory(outDir);
            run = 1;
            LaunchCurrent();
        }

        private void LaunchCurrent()
        {
            current = queue[queueIndex];
            skippedCurrent = false;
            phase = Phase.Launching;
            phaseSince = Time.realtimeSinceStartup;
            Log.LogInfo($"[{queueIndex + 1}/{queue.Count}] 开始播放：{Describe()}");
            bool ok = false;
            try { ok = adapter.Launch(current, run); }
            catch (Exception ex) { Log.LogError(ex); }
            if (!ok)
            {
                Log.LogWarning("启动失败，跳过：" + Describe());
                MarkDone(current, "launch-error");
                Advance();
            }
        }

        /// <summary>当前这段处理完，进入下一段或结束。</summary>
        private void Advance()
        {
            current = null;
            run = 1;
            queueIndex++;
            if (queueIndex < queue.Count)
            {
                phase = Phase.Waiting;
                phaseSince = Time.realtimeSinceStartup;
                return;
            }
            Log.LogInfo($"全部 {queue.Count} 段已处理完。");
            try { adapter.OnSessionEnd(); } catch (Exception ex) { Log.LogError(ex); }
            if (cfgCompress.Value) StartCompress();
            else { phase = Phase.Idle; status = "全部录完：" + outDir; }
        }

        private void EndSession(string msg)
        {
            stopAfterCurrent = false;
            phase = Phase.Idle;
            status = msg;
            Log.LogInfo(msg);
            try { adapter.OnSessionEnd(); } catch (Exception ex) { Log.LogError(ex); }
        }

        private string Describe() => current == null ? "(无)" : current + (run > 1 ? $" [第 {run} 遍]" : "");

        // ================= 录制 =================
        private bool UseGameBar => string.Equals(cfgMode.Value?.Trim(), "GameBar", StringComparison.OrdinalIgnoreCase);

        private void StartRecording()
        {
            string file = Path.Combine(outDir, FileNameFor(current, run) + ".mp4");
            if (UseGameBar)
            {
                gameBar = gameBar ?? new GameBarRecorder(Log);
                gameBar.Start(file, ExpandDir(cfgGameBarDir.Value));
                return;
            }
            var rs = new RecorderSettings
            {
                FfmpegPath = ffmpegPath,
                Fps = Mathf.Clamp(cfgFps.Value, 1, 240),
                Width = cfgWidth.Value,
                Height = cfgHeight.Value,
                FlipVertical = cfgFlip.Value,
                VideoEncoderArgs = cfgVideoArgs.Value,
                AudioEncoderArgs = cfgAudioArgs.Value,
            };
            recorder = new Recorder(Log, rs);
            try { recorder.Start(file); }
            catch (Exception ex) { Log.LogError("启动录制失败：" + ex); }
        }

        private static string ExpandDir(string d)
        {
            if (string.IsNullOrWhiteSpace(d))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "Captures");
            return Environment.ExpandEnvironmentVariables(d.Trim());
        }

        private void StopRecording(bool save)
        {
            if (gameBar != null && gameBar.IsRecording)
            {
                var gt = gameBar.Stop(save);
                if (save) finalizing.Add(gt);
                return;
            }
            if (recorder == null || !recorder.IsRecording) return;
            if (!save) { recorder.Abort(); return; }
            finalizing.Add(recorder.StopAsync());
        }

        private void EnsureAudioTap()
        {
            if (recorder == null || !recorder.IsRecording) return;
            var listener = FindAnyObjectByType<AudioListener>();
            if (listener != null && listener.GetComponent<AudioTap>() == null)
            {
                listener.gameObject.AddComponent<AudioTap>();
                Log.LogInfo("已挂接音频：" + listener.gameObject.name);
            }
        }

        // ================= 压缩 =================
        private static List<string> ListRecordings(string dir) => Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.mp4").Where(f => !f.Contains(".tmp.")).OrderBy(f => f).ToList()
            : new List<string>();

        private void StartCompress()
        {
            var pending = finalizing.ToArray();
            if (pending.Length == 0 && ListRecordings(outDir).Count == 0) { phase = Phase.Idle; status = "没有可压缩的录像"; return; }
            phase = Phase.Compressing;
            string ff = ffmpegPath, args = FixCompressArgs(cfgCompressArgs.Value), dir = outDir;
            bool del = cfgDeleteOriginal.Value, zip = cfgZip.Value;
            compressStatus = "等待录像合成完成…";
            compressTask = Task.Run(() =>
            {
                try { Task.WaitAll(pending); } catch { }
                // 必须在最后一段合成完之后再列文件，否则最后一段会漏压
                var files = ListRecordings(dir);
                Log.LogInfo($"开始压缩 {files.Count} 个文件…");
                string cdir = Path.Combine(dir, "Compressed");
                Directory.CreateDirectory(cdir);
                int ok = 0; long before = 0, after = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    string src = files[i];
                    if (!File.Exists(src)) continue;
                    string dst = Path.Combine(cdir, Path.GetFileName(src));
                    compressStatus = $"压缩中 {i + 1}/{files.Count}：{Path.GetFileName(src)}";
                    Log.LogInfo(compressStatus);
                    string tmp = dst + ".part.mp4";
                    string logPath = Path.Combine(cdir, Path.GetFileNameWithoutExtension(src) + ".ffmpeg.log");
                    int code = Ffmpeg.Run(ff, $"-hide_banner -loglevel warning -y -i \"{src}\" -map 0 {args} -movflags +faststart \"{tmp}\"", logPath);
                    if (code == 0 && File.Exists(tmp))
                    {
                        Recorder.TryDelete(dst);
                        File.Move(tmp, dst);
                        Recorder.TryDelete(logPath);
                        before += new FileInfo(src).Length; after += new FileInfo(dst).Length;
                        ok++;
                        if (del) Recorder.TryDelete(src);
                    }
                    else { Recorder.TryDelete(tmp); Log.LogError($"压缩失败 (exit {code})：{src}，见 {logPath}"); }
                }
                string msg = $"压缩完成 {ok}/{files.Count}，{before / 1048576.0:0} MB → {after / 1048576.0:0} MB，目录：{cdir}";
                if (zip && ok > 0)
                {
                    compressStatus = "打包 zip…";
                    string zipPath = Path.Combine(dir, "GalleryRecordings_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");
                    try
                    {
                        using (var za = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                            foreach (var f in Directory.GetFiles(cdir, "*.mp4").OrderBy(f => f))
                                za.CreateEntryFromFile(f, Path.GetFileName(f), System.IO.Compression.CompressionLevel.NoCompression);
                        msg += "；已打包：" + zipPath;
                    }
                    catch (Exception ex) { Log.LogError("打包失败：" + ex.Message); }
                }
                compressStatus = msg;
            });
        }

        /// <summary>MediaFoundation 编码器只接受 nv12；配置里没写 -pix_fmt 时自动补上。</summary>
        private static string FixCompressArgs(string a)
        {
            a = a ?? "";
            if (a.Contains("_mf") && !a.Contains("-pix_fmt")) a = "-pix_fmt nv12 " + a;
            if (a.Contains("hevc") && !a.Contains("-tag:v")) a += " -tag:v hvc1";
            return a;
        }

        // ================= 进度 / 命名 =================
        private string FileNameFor(GalleryEntry e, int r)
        {
            string raw = string.IsNullOrWhiteSpace(e.FileName) ? e.Key : e.FileName.Trim();
            var sb = new StringBuilder();
            var bad = Path.GetInvalidFileNameChars();
            foreach (char c in raw) sb.Append(bad.Contains(c) || char.IsControl(c) ? '_' : c);
            string name = sb.ToString();
            if (name.Length > 120) name = name.Substring(0, 120);
            if (r > 1) name += adapter.RunSuffix(r);
            return name;
        }

        private void MarkDone(GalleryEntry e, string result)
        {
            if (e == null) return;
            done.Add(e.Key);
            try
            {
                Directory.CreateDirectory(outDir);
                File.AppendAllText(progressPath, e.Key + "\t" + result + "\t" + DateTime.Now.ToString("s") + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) { Log.LogWarning("保存进度失败：" + ex.Message); }
        }

        private void LoadProgress()
        {
            try
            {
                if (!File.Exists(progressPath)) return;
                foreach (var line in File.ReadAllLines(progressPath))
                {
                    var k = line.Split('\t')[0].Trim();
                    if (k.Length > 0) done.Add(k);
                }
            }
            catch (Exception ex) { Log.LogWarning("读取进度失败：" + ex.Message); }
        }

        // ================= 浮层 =================
        private GUIStyle style;

        private void DoGUI()
        {
            if (!cfgOverlay.Value) return;
            if (phase == Phase.Launching || phase == Phase.Playing || phase == Phase.Finishing) return; // 不录进视频
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = 18 };
                style.normal.textColor = Color.white;
            }
            string text = adapter == null
                ? "[回想录制] 没有找到对应这个游戏的扩展包"
                : $"[回想录制 {keyStart.Value}] {status}";
            var r = new Rect(12, 8, 1400, 30);
            GUI.color = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, style);
            GUI.color = Color.white;
            GUI.Label(r, text, style);
        }

        /// <summary>只在宿主对象真的被销毁（游戏退出）时调用。</summary>
        private void Shutdown()
        {
            if (recorder != null && recorder.IsRecording) recorder.Abort();
            if (gameBar != null && gameBar.IsRecording) gameBar.Stop(false);
        }
    }
}
