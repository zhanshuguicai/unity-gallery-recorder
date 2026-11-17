// 游戏内录屏：画面用 ScreenCapture + AsyncGPUReadback 推给 ffmpeg，声音在 AudioListener 上截取最终混音写 WAV，
// 停止后把两者合成一个 mp4。时间轴按真实时间对齐：画面按固定帧率补帧/丢帧，声音按实际播放写入。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace GalleryRecorder
{
    internal sealed class RecorderSettings
    {
        public string FfmpegPath;
        public int Fps = 60;
        public int Width = 1920;   // 0 = 跟随窗口
        public int Height = 1080;
        public bool FlipVertical = false;
        public string VideoEncoderArgs;
        public string AudioEncoderArgs;
        public int MaxQueuedFrames = 12;
    }

    internal sealed class Recorder
    {
        private readonly ManualLogSource log;
        private readonly RecorderSettings s;

        public bool IsRecording { get; private set; }
        public string CurrentOutput { get; private set; }
        public int DroppedReadbacks { get; private set; }

        private int outW, outH;
        private RenderTexture screenRT, outRT;
        private double t0;
        private long framesScheduled;   // 已排进队列的帧数（含重复帧）
        private long capturesRequested; // 已发起的抓帧次数，用来节流
        private int pendingReadbacks;
        private string tmpVideo, tmpAudio, ffLog;

        private FfmpegPipe pipe;
        private WavWriter wav;

        public Recorder(ManualLogSource log, RecorderSettings settings)
        {
            this.log = log;
            s = settings;
        }

        public void Start(string outputPath)
        {
            if (IsRecording) throw new InvalidOperationException("已经在录制");
            CurrentOutput = outputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            string baseNoExt = Path.Combine(Path.GetDirectoryName(outputPath), Path.GetFileNameWithoutExtension(outputPath));
            tmpVideo = baseNoExt + ".video.tmp.mp4";
            tmpAudio = baseNoExt + ".audio.tmp.wav";
            ffLog = baseNoExt + ".ffmpeg.log";
            TryDelete(tmpVideo); TryDelete(tmpAudio);

            outW = s.Width > 0 ? s.Width : Screen.width;
            outH = s.Height > 0 ? s.Height : Screen.height;
            outW &= ~1; outH &= ~1;
            outRT = new RenderTexture(outW, outH, 0, RenderTextureFormat.ARGB32) { name = "GalleryRecorderOut" };
            outRT.Create();

            string args =
                $"-hide_banner -loglevel warning -y -f rawvideo -pix_fmt rgba -s {outW}x{outH} -framerate {s.Fps} -i - " +
                "-vf \"scale=out_color_matrix=bt709:out_range=tv,format=nv12\" " +
                "-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv " +
                $"{s.VideoEncoderArgs} -an \"{tmpVideo}\"";
            pipe = new FfmpegPipe(s.FfmpegPath, args, ffLog, outW * outH * 4, s.MaxQueuedFrames, log);

            wav = new WavWriter(tmpAudio, AudioSettings.outputSampleRate);
            framesScheduled = 0;
            capturesRequested = 0;
            pendingReadbacks = 0;
            DroppedReadbacks = 0;
            t0 = Time.realtimeSinceStartupAsDouble;
            AudioTap.Current = wav;
            IsRecording = true;
            log.LogInfo($"开始录制 {outW}x{outH}@{s.Fps} → {outputPath}");
        }

        /// <summary>在 WaitForEndOfFrame 之后调用。</summary>
        public void CaptureFrame()
        {
            if (!IsRecording) return;
            double now = Time.realtimeSinceStartupAsDouble;
            // 节流：游戏帧率高于目标帧率时不必每帧抓
            long due = (long)Math.Floor((now - t0) * s.Fps) + 1;
            if (capturesRequested >= due) return;
            if (pendingReadbacks > 6) { DroppedReadbacks++; return; } // GPU 读回堆积，跳过这一帧，后面补帧
            capturesRequested = due;

            int sw = Screen.width, sh = Screen.height;
            if (sw <= 0 || sh <= 0) return;
            if (screenRT == null || screenRT.width != sw || screenRT.height != sh)
            {
                if (screenRT != null) { screenRT.Release(); UnityEngine.Object.Destroy(screenRT); }
                screenRT = new RenderTexture(sw, sh, 0, RenderTextureFormat.ARGB32) { name = "GalleryRecorderScreen" };
                screenRT.Create();
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(screenRT);
            if (s.FlipVertical) Graphics.Blit(screenRT, outRT, new Vector2(1f, -1f), new Vector2(0f, 1f));
            else Graphics.Blit(screenRT, outRT);

            var p = pipe;
            double stamp = now;
            pendingReadbacks++;
            AsyncGPUReadback.Request(outRT, 0, TextureFormat.RGBA32, req =>
            {
                pendingReadbacks--;
                if (req.hasError || p != pipe || p == null) return;
                long target = (long)Math.Floor((stamp - t0) * s.Fps) + 1;
                long repeat = target - framesScheduled;
                if (repeat <= 0) return;
                framesScheduled = target;
                var data = req.GetData<byte>();
                p.Enqueue(data, (int)repeat);
            });
        }

        /// <summary>停止并在后台合成。返回的 Task 完成时最终文件已写好。</summary>
        public Task<bool> StopAsync()
        {
            if (!IsRecording) return Task.FromResult(false);
            IsRecording = false;
            AudioTap.Current = null;
            AsyncGPUReadback.WaitAllRequests(); // 让最后几帧的回调先跑完
            double dur = Time.realtimeSinceStartupAsDouble - t0;
            // 用最后一帧补齐到结束时刻，保证画面和声音一样长
            long target = (long)Math.Floor(dur * s.Fps);
            if (target > framesScheduled) { pipe.RepeatLast((int)(target - framesScheduled)); framesScheduled = target; }

            var p = pipe; var w = wav;
            pipe = null; wav = null;
            string v = tmpVideo, a = tmpAudio, final = CurrentOutput, logPath = ffLog;
            string ff = s.FfmpegPath, aArgs = s.AudioEncoderArgs;
            if (outRT != null) { outRT.Release(); UnityEngine.Object.Destroy(outRT); outRT = null; }
            log.LogInfo($"停止录制，时长 {dur:0.0}s，帧 {framesScheduled}，读回跳过 {DroppedReadbacks}。后台合成中…");

            return Task.Run(() =>
            {
                try
                {
                    bool vOk = p.Finish();
                    long audioBytes = w.Close();
                    if (!vOk || !File.Exists(v)) { log.LogError("视频编码失败，见 " + logPath); return false; }
                    TryDelete(final);
                    string args = audioBytes > 0
                        ? $"-hide_banner -loglevel warning -y -i \"{v}\" -i \"{a}\" -map 0:v:0 -map 1:a:0 -c:v copy {aArgs} -movflags +faststart \"{final}\""
                        : $"-hide_banner -loglevel warning -y -i \"{v}\" -c:v copy -movflags +faststart \"{final}\"";
                    int code = Ffmpeg.Run(ff, args, logPath);
                    if (code != 0 || !File.Exists(final)) { log.LogError($"合成失败 (exit {code})，见 {logPath}"); return false; }
                    TryDelete(v); TryDelete(a); TryDelete(logPath);
                    log.LogInfo("已保存：" + final);
                    return true;
                }
                catch (Exception ex) { log.LogError(ex); return false; }
            });
        }

        /// <summary>丢弃当前录制（不合成）。</summary>
        public void Abort()
        {
            if (!IsRecording) return;
            IsRecording = false;
            AudioTap.Current = null;
            var p = pipe; var w = wav;
            pipe = null; wav = null;
            string v = tmpVideo, a = tmpAudio, lg = ffLog;
            if (outRT != null) { outRT.Release(); UnityEngine.Object.Destroy(outRT); outRT = null; }
            Task.Run(() => { try { p.Finish(); w.Close(); } catch { } TryDelete(v); TryDelete(a); TryDelete(lg); });
            log.LogWarning("已放弃当前录制：" + CurrentOutput);
        }

        internal static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>把 RGBA 帧写进 ffmpeg 的 stdin。队列满时把新帧并进上一帧的重复次数，不阻塞主线程。</summary>
    internal sealed class FfmpegPipe
    {
        private sealed class Frame { public byte[] Data; public int Repeat; }

        private readonly Process proc;
        private readonly Stream stdin;
        private readonly Thread thread;
        private readonly LinkedList<Frame> queue = new LinkedList<Frame>();
        private readonly Stack<byte[]> pool = new Stack<byte[]>();
        private readonly object gate = new object();
        private readonly int frameBytes, maxQueue;
        private readonly ManualLogSource log;
        private byte[] lastWritten;
        private bool closing, broken;
        private readonly StreamWriter logWriter;

        public FfmpegPipe(string exe, string args, string logPath, int frameBytes, int maxQueue, ManualLogSource log)
        {
            this.frameBytes = frameBytes;
            this.maxQueue = Math.Max(2, maxQueue);
            this.log = log;
            logWriter = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            logWriter.WriteLine("ffmpeg " + args);
            proc = Ffmpeg.Create(exe, args, true);
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (logWriter) logWriter.WriteLine(e.Data); };
            proc.Start();
            proc.BeginErrorReadLine();
            try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            stdin = proc.StandardInput.BaseStream;
            thread = new Thread(Loop) { IsBackground = true, Name = "GalleryRecorder-ffmpeg" };
            thread.Start();
        }

        public void Enqueue(Unity.Collections.NativeArray<byte> data, int repeat)
        {
            lock (gate)
            {
                if (closing || broken) return;
                if (queue.Count >= maxQueue && queue.Last != null)
                {
                    queue.Last.Value.Repeat += repeat; // 编码跟不上：重复上一帧代替这一帧
                    return;
                }
                byte[] buf = pool.Count > 0 ? pool.Pop() : new byte[frameBytes];
                data.CopyTo(buf);
                queue.AddLast(new Frame { Data = buf, Repeat = repeat });
                Monitor.Pulse(gate);
            }
        }

        public void RepeatLast(int n)
        {
            if (n <= 0) return;
            lock (gate)
            {
                if (queue.Last != null) queue.Last.Value.Repeat += n;
                else if (lastWritten != null) { queue.AddLast(new Frame { Data = lastWritten, Repeat = n }); lastWritten = null; }
                Monitor.Pulse(gate);
            }
        }

        private void Loop()
        {
            while (true)
            {
                Frame f;
                lock (gate)
                {
                    while (queue.Count == 0 && !closing) Monitor.Wait(gate);
                    if (queue.Count == 0) break;
                    f = queue.First.Value;
                    queue.RemoveFirst();
                }
                try
                {
                    for (int i = 0; i < f.Repeat; i++) stdin.Write(f.Data, 0, frameBytes);
                }
                catch (Exception ex)
                {
                    lock (gate) { broken = true; queue.Clear(); }
                    log.LogError("写入 ffmpeg 失败：" + ex.Message);
                    break;
                }
                lock (gate)
                {
                    if (lastWritten != null && lastWritten != f.Data) pool.Push(lastWritten);
                    lastWritten = f.Data;
                }
            }
            try { stdin.Flush(); stdin.Close(); } catch { }
        }

        /// <summary>写完剩余帧、关闭 stdin、等待 ffmpeg 退出。</summary>
        public bool Finish()
        {
            lock (gate) { closing = true; Monitor.PulseAll(gate); }
            thread.Join();
            if (!proc.WaitForExit(10 * 60 * 1000)) { try { proc.Kill(); } catch { } }
            proc.WaitForExit();
            int code = proc.ExitCode;
            lock (logWriter) logWriter.Dispose();
            return code == 0 && !broken;
        }
    }

    /// <summary>挂在 AudioListener 上，拿到最终混音。</summary>
    internal sealed class AudioTap : MonoBehaviour
    {
        public static volatile WavWriter Current;

        private void OnAudioFilterRead(float[] data, int channels)
        {
            var w = Current;
            if (w != null) w.Push(data, channels);
        }
    }

    /// <summary>16-bit PCM WAV，音频线程只入队，后台线程写盘。</summary>
    internal sealed class WavWriter
    {
        private readonly FileStream fs;
        private readonly int sampleRate;
        private volatile int channels;
        private readonly Queue<byte[]> queue = new Queue<byte[]>();
        private readonly object gate = new object();
        private readonly Thread thread;
        private bool closing;
        private long dataBytes;

        public WavWriter(string path, int sampleRate)
        {
            this.sampleRate = sampleRate;
            fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
            fs.Write(new byte[44], 0, 44);
            thread = new Thread(Loop) { IsBackground = true, Name = "GalleryRecorder-wav" };
            thread.Start();
        }

        public void Push(float[] data, int ch)
        {
            if (channels == 0) channels = ch;
            if (ch != channels) return;
            var buf = new byte[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                float f = data[i];
                if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
                short v = (short)(f * 32767f);
                buf[2 * i] = (byte)v;
                buf[2 * i + 1] = (byte)(v >> 8);
            }
            lock (gate) { if (closing) return; queue.Enqueue(buf); Monitor.Pulse(gate); }
        }

        private void Loop()
        {
            while (true)
            {
                byte[] b;
                lock (gate)
                {
                    while (queue.Count == 0 && !closing) Monitor.Wait(gate);
                    if (queue.Count == 0) break;
                    b = queue.Dequeue();
                }
                fs.Write(b, 0, b.Length);
                dataBytes += b.Length;
            }
        }

        /// <summary>返回写入的 PCM 字节数。</summary>
        public long Close()
        {
            lock (gate) { closing = true; Monitor.PulseAll(gate); }
            thread.Join();
            int ch = channels == 0 ? 2 : channels;
            fs.Seek(0, SeekOrigin.Begin);
            using (var bw = new BinaryWriter(fs, Encoding.ASCII, true))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write((uint)Math.Min(uint.MaxValue, 36 + dataBytes));
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16); bw.Write((short)1); bw.Write((short)ch);
                bw.Write(sampleRate); bw.Write(sampleRate * ch * 2);
                bw.Write((short)(ch * 2)); bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write((uint)Math.Min(uint.MaxValue, dataBytes));
            }
            fs.Dispose();
            return dataBytes;
        }
    }

    internal static class Ffmpeg
    {
        public static Process Create(string exe, string args, bool stdin)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdin,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            return new Process { StartInfo = psi };
        }

        /// <summary>同步运行 ffmpeg，stderr 追加到日志文件，返回退出码。</summary>
        public static int Run(string exe, string args, string logPath)
        {
            using (var p = Create(exe, args, false))
            using (var lw = new StreamWriter(logPath, true, new UTF8Encoding(false)) { AutoFlush = true })
            {
                lw.WriteLine("ffmpeg " + args);
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (lw) lw.WriteLine(e.Data); };
                p.Start();
                p.BeginErrorReadLine();
                p.WaitForExit();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        /// <summary>读媒体文件时长（秒），读不到返回 0。</summary>
        public static double Duration(string exe, string file)
        {
            var sb = new StringBuilder();
            using (var p = Create(exe, $"-hide_banner -i \"{file}\"", false))
            {
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginErrorReadLine();
                p.WaitForExit();
                p.WaitForExit();
            }
            var m = System.Text.RegularExpressions.Regex.Match(sb.ToString(), @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!m.Success) return 0;
            return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把 files 按顺序无损拼接成 output（concat，-c copy），每段一个章节（标题 = 文件名）。
        /// 所有分段必须是同一套编码参数（同一次录制 / 同一次压缩出来的就是）。
        /// </summary>
        public static bool Merge(string exe, IList<string> files, string output, string extraArgs, Action<string> progress, ManualLogSource log)
        {
            if (files.Count == 0) return false;
            string list = output + ".list.txt", meta = output + ".chapters.txt", tmp = output + ".part.mp4", logPath = output + ".ffmpeg.log";
            var lsb = new StringBuilder();
            var msb = new StringBuilder(";FFMETADATA1\n");
            double t = 0;
            for (int i = 0; i < files.Count; i++)
            {
                progress?.Invoke($"整合：读取时长 {i + 1}/{files.Count}");
                double d = Duration(exe, files[i]);
                lsb.Append("file '").Append(files[i].Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
                long start = (long)Math.Round(t * 1000); t += d;
                msb.Append("[CHAPTER]\nTIMEBASE=1/1000\nSTART=").Append(start).Append("\nEND=").Append((long)Math.Round(t * 1000))
                   .Append("\ntitle=").Append(MetaEscape(Path.GetFileNameWithoutExtension(files[i]))).Append('\n');
            }
            File.WriteAllText(list, lsb.ToString(), new UTF8Encoding(false));
            File.WriteAllText(meta, msb.ToString(), new UTF8Encoding(false));
            string msg = $"整合中：{Path.GetFileName(output)}（{files.Count} 段，{t / 3600:0.0} 小时）";
            progress?.Invoke(msg);
            log.LogInfo(msg);
            int code = Run(exe, $"-hide_banner -loglevel warning -y -f concat -safe 0 -i \"{list}\" -i \"{meta}\" -map 0 -map_metadata 1 -map_chapters 1 -c copy {extraArgs} -movflags +faststart \"{tmp}\"", logPath);
            Recorder.TryDelete(list); Recorder.TryDelete(meta);
            if (code == 0 && File.Exists(tmp))
            {
                Recorder.TryDelete(output);
                File.Move(tmp, output);
                Recorder.TryDelete(logPath);
                log.LogInfo($"整合完成：{output}，{new FileInfo(output).Length / 1073741824.0:0.00} GB");
                return true;
            }
            Recorder.TryDelete(tmp);
            log.LogError($"整合失败 (exit {code})：{output}，见 {logPath}");
            return false;
        }

        private static string MetaEscape(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) { if (c == '=' || c == ';' || c == '#' || c == '\\' || c == '\n') sb.Append('\\'); sb.Append(c); }
            return sb.ToString();
        }
    }
}
