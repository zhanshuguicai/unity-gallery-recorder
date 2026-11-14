// 扩展包接口：每个游戏写一个 GalleryAdapter 子类，在自己的 BepInEx 插件 Awake 里调用 GalleryRecorderPlugin.Register 注册。
// 核心负责：队列、录制、进度、断点续录、卡死/超时、压缩、热键、状态浮层。
// 扩展包只负责和游戏打交道：列出回想、启动一段、判断是否在播放、播放中保持自动前进/处理选项。
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace GalleryRecorder
{
    /// <summary>一段要录的回想。</summary>
    public sealed class GalleryEntry
    {
        /// <summary>稳定且唯一的标识，用于进度记录（progress.txt）。不要用会随语言变化的标题。</summary>
        public string Key;

        /// <summary>日志和状态里显示的名字。</summary>
        public string Title;

        /// <summary>输出文件名（不含扩展名）。核心会替换非法字符；留空则用 Key。</summary>
        public string FileName;

        /// <summary>扩展包自用的数据，核心不读。</summary>
        public object Data;

        public override string ToString() => string.IsNullOrEmpty(Title) || Title == Key ? Key : $"{Key}「{Title}」";
    }

    /// <summary>核心提供给扩展包的环境。</summary>
    public sealed class AdapterContext
    {
        internal AdapterContext() { }

        public ManualLogSource Log { get; internal set; }

        /// <summary>常驻宿主对象（DontDestroyOnLoad），可以用它跑协程。</summary>
        public MonoBehaviour Host { get; internal set; }

        /// <summary>核心的配置文件。扩展包自己的设置请用扩展包插件自己的 Config。</summary>
        public ConfigFile CoreConfig { get; internal set; }

        /// <summary>播放时是否强制自动前进（核心配置 Playback.ForceAuto）。</summary>
        public bool ForceAuto => forceAuto?.Value ?? true;

        /// <summary>出现选项时是否自动选择（核心配置 Playback.AutoChoice）。</summary>
        public bool AutoChoice => autoChoice?.Value ?? true;

        /// <summary>选项出现后停留多少秒再选（核心配置 Playback.ChoiceDelaySeconds）。</summary>
        public float ChoiceDelay => choiceDelay?.Value ?? 1.5f;

        /// <summary>当前是否正在录制。</summary>
        public bool IsRecording => isRecording?.Invoke() ?? false;

        /// <summary>播放有进展时调用（例如执行了新的脚本命令、选了选项），用于卡死检测。只有 TracksProgress 为 true 时才有意义。</summary>
        public void MarkProgress() => markProgress?.Invoke();

        internal ConfigEntry<bool> forceAuto, autoChoice;
        internal ConfigEntry<float> choiceDelay;
        internal System.Func<bool> isRecording;
        internal System.Action markProgress;
    }

    /// <summary>
    /// 一个游戏的适配。核心的调用顺序：
    /// Initialize → (按 F8) CanBeginSession → BuildQueue →
    /// 每段：等 IsReadyForNext → Launch → 等 IsPlaying 变 true（开始录制）→ 每帧 WhilePlaying →
    /// IsPlaying 变 false（停止录制）→ 等 IsReadyForNext → ShouldReplay（true 则同一段再录一遍）→ 下一段。
    /// 所有方法都在 Unity 主线程调用。
    /// </summary>
    public abstract class GalleryAdapter
    {
        public AdapterContext Context { get; internal set; }
        protected ManualLogSource Log => Context.Log;

        /// <summary>游戏名，用于日志和状态显示。</summary>
        public abstract string GameName { get; }

        /// <summary>注册后调用一次。可以在这里打 Harmony 补丁。</summary>
        public virtual void Initialize() { }

        /// <summary>按 F8 时调用：现在能不能开始（例如必须在标题画面 / 回想房）。不能时给出原因。</summary>
        public abstract bool CanBeginSession(out string reason);

        /// <summary>列出要录的回想，按录制顺序。已完成的由核心根据进度过滤，这里返回全部即可。</summary>
        public abstract IList<GalleryEntry> BuildQueue();

        /// <summary>
        /// 启动一段回想。run 从 1 开始，ShouldReplay 返回 true 后同一段会以 run+1 再次启动。
        /// 返回 false 表示启动失败，核心会记为失败并跳到下一段。
        /// </summary>
        public abstract bool Launch(GalleryEntry entry, int run);

        /// <summary>回想是否正在播放。由 false 变 true 时核心开始录制，由 true 变 false 时停止录制。</summary>
        public abstract bool IsPlaying { get; }

        /// <summary>回到可以启动下一段的状态（画廊 / 回想房空闲）。默认 = 不在播放。</summary>
        public virtual bool IsReadyForNext => !IsPlaying;

        /// <summary>播放中每帧调用：保持自动前进、关闭快进、处理选项等。</summary>
        public virtual void WhilePlaying() { }

        /// <summary>true = 扩展包会调用 Context.MarkProgress()，核心据此做“长时间无进展”检测。</summary>
        public virtual bool TracksProgress => false;

        /// <summary>强制结束当前回想（F7 放弃 / 卡死 / 超时）。做不到返回 false，核心会等它自然结束。</summary>
        public virtual bool ForceStop() => false;

        /// <summary>一段播完、回到空闲后调用。返回 true = 还有其他分支没录，同一段再录一遍。</summary>
        public virtual bool ShouldReplay(GalleryEntry entry, int run) => false;

        /// <summary>同一段第 run 遍（run ≥ 2）的文件名后缀。</summary>
        public virtual string RunSuffix(int run) => "_分支" + run;

        /// <summary>整轮结束（全部完成或手动停止）时调用。</summary>
        public virtual void OnSessionEnd() { }
    }
}
