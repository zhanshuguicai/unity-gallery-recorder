// 扩展包：Yorutina（Unity 6 Mono + Naninovel）
// 画廊入口 VideoSiteUtil.ShowPlayVideoDialog(isGallery: true)，用 Harmony 跳过“播放确认”对话框；
// 播放状态看 NaniManager.isNovelPlaying；播放中强制 Naninovel AUTO，自动选 Naninovel 选项和游戏自己的选择框。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Naninovel;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace GalleryRecorder.Adapters.Yorutina
{
    [BepInPlugin(GUID, "Gallery Recorder - Yorutina", "0.1.0")]
    [BepInDependency(GalleryRecorderPlugin.GUID)]
    [BepInProcess("Yorutina.exe")]
    public class YorutinaAdapterPlugin : BaseUnityPlugin
    {
        public const string GUID = "unitygalleryrecorder.adapter.yorutina";

        private void Awake()
        {
            var a = new YorutinaAdapter
            {
                Types = Config.Bind("Yorutina", "Types", "Video_Yorutina,Video_Hidden,Video_Mob,Story,Love,Cm,PartTime",
                    "要录的类型，逗号分隔。可选：Video_Yorutina, Video_Hidden, Video_Mob, Story, Love, Cm, PartTime"),
                OnlyUnlocked = Config.Bind("Yorutina", "OnlyUnlocked", false,
                    "只录已解锁的回想。false = 画廊里全部条目都录（游戏本身也提供“全部解锁”按钮）"),
            };
            GalleryRecorderPlugin.Register(a);
        }
    }

    public sealed class YorutinaAdapter : GalleryAdapter
    {
        internal static YorutinaAdapter Instance;
        internal static bool AutoConfirmPlayDialog;

        public ConfigEntry<string> Types;
        public ConfigEntry<bool> OnlyUnlocked;

        private readonly List<AsyncOperationHandle> thumbHandles = new List<AsyncOperationHandle>();
        private bool playCompleted, awaitingCompletion, sessionActive;
        private int lastCommandCount = -1;
        private float choiceSeenAt = -1f;
        private readonly List<IChoiceHandlerActor> choiceActors = new List<IChoiceHandlerActor>();
        private readonly List<Choice> choiceBuf = new List<Choice>();

        public override string GameName => "Yorutina";

        public override void Initialize()
        {
            Instance = this;
            new Harmony(YorutinaAdapterPlugin.GUID).PatchAll(typeof(Patches));
        }

        private static NaniManager Nani => SingletonMonoBehaviour<NaniManager>.Instance;

        public override bool CanBeginSession(out string reason)
        {
            reason = null;
            if (Nani == null || SingletonMonoBehaviour<GlobalSave>.Instance == null || CommonDialogPanel.Instance == null
                || SingletonMonoBehaviour<BaseScene>.Instance == null)
            { reason = "游戏还没准备好，请先进入标题画面"; return false; }
            if (Nani.isNovelPlaying) { reason = "正在播放剧情，请先回到标题画面再开始"; return false; }
            return true;
        }

        public override IList<GalleryEntry> BuildQueue()
        {
            var save = SingletonMonoBehaviour<GlobalSave>.Instance;
            save.InitializeGalleryList();
            var types = new HashSet<string>(Types.Value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0), StringComparer.OrdinalIgnoreCase);
            sessionActive = true;
            return (save.galleryList ?? new List<GalleryData>())
                .Where(g => g != null && g.advData != null && g.advData.advTitleId != AdvTitleId.None)
                .Where(g => types.Contains(g.advData.type.ToString()))
                .Where(g => !OnlyUnlocked.Value || g.state == AdvState.Unlocked)
                .GroupBy(g => g.advData.advTitleId).Select(gr => gr.First())
                .OrderBy(g => g.advData.scenarioNumber).ThenBy(g => (int)g.advData.advTitleId)
                .Select(g =>
                {
                    string title = null;
                    try { title = GalleryTitleText.GetGalleryTitleText(g.advData.advTitleId); } catch { }
                    string id = g.advData.advTitleId.ToString();
                    return new GalleryEntry
                    {
                        Key = id,
                        Title = title,
                        FileName = $"{g.advData.scenarioNumber:D4}_{id}" + (string.IsNullOrWhiteSpace(title) ? "" : "_" + title.Trim()),
                        Data = g,
                    };
                })
                .ToList();
        }

        public override bool Launch(GalleryEntry entry, int run)
        {
            var g = (GalleryData)entry.Data;
            playCompleted = false;
            awaitingCompletion = true;
            lastCommandCount = -1;
            choiceSeenAt = -1f;
            AutoConfirmPlayDialog = true;
            try
            {
                // 与画廊里点“播放”走同一条路：isGallery = true，不播 CM
                VideoSiteUtil.ShowPlayVideoDialog(g.advData, thumbHandles, true, () => { playCompleted = true; awaitingCompletion = false; }, false, null, true);
                return true;
            }
            catch (Exception ex)
            {
                AutoConfirmPlayDialog = false;
                awaitingCompletion = false;
                Log.LogError(ex);
                return false;
            }
        }

        public override bool IsPlaying
        {
            get
            {
                var n = Nani;
                if (n != null && n.isNovelPlaying) { AutoConfirmPlayDialog = false; return true; }
                return false;
            }
        }

        public override bool IsReadyForNext
        {
            get
            {
                var n = Nani;
                return n != null && !n.isNovelPlaying && (playCompleted || !awaitingCompletion);
            }
        }

        public override bool TracksProgress => true;

        public override void WhilePlaying()
        {
            if (!Engine.Initialized) return;
            var player = Engine.GetService<IScriptPlayer>();
            if (player == null) return;

            if (Context.ForceAuto)
            {
                if (player.Skipping) player.SetSkip(false);
                if (!player.AutoPlaying) player.SetAutoPlay(true);
            }
            if (Context.AutoChoice) AutoPickNaninovelChoice();

            int cmds = player.PlayedCommandsCount;
            if (cmds != lastCommandCount || (player.Executing && player.Playing))
            {
                lastCommandCount = cmds;
                Context.MarkProgress();
            }
        }

        public override bool ForceStop()
        {
            var n = Nani;
            if (n == null || !n.isNovelPlaying) return true;
            _ = n.BackToGame();
            return true;
        }

        public override void OnSessionEnd() { sessionActive = false; }

        private void AutoPickNaninovelChoice()
        {
            var mgr = Engine.GetService<IChoiceHandlerManager>();
            if (mgr == null) return;
            float now = Time.realtimeSinceStartup;
            choiceActors.Clear();
            mgr.CollectActors(choiceActors);
            foreach (var actor in choiceActors)
            {
                choiceBuf.Clear();
                actor.CollectChoices(choiceBuf);
                if (choiceBuf.Count == 0) continue;
                if (choiceSeenAt < 0f) { choiceSeenAt = now; return; }
                if (now - choiceSeenAt < Context.ChoiceDelay) return;
                choiceSeenAt = -1f;
                Log.LogInfo("自动选择（Naninovel）：" + choiceBuf[0].Id);
                actor.HandleChoice(choiceBuf[0].Id);
                Context.MarkProgress();
                return;
            }
            choiceSeenAt = -1f;
        }

        /// <summary>游戏自己的选择框（例如 BadTimeUp 结局）。</summary>
        internal void OnGameChoiceShown(ChoiceDialogPanel panel, List<(string text, Action action)> choices)
        {
            if (!sessionActive || !Context.AutoChoice || !IsPlaying || choices == null || choices.Count == 0) return;
            Context.Host.StartCoroutine(PickGameChoice(panel, choices[0]));
        }

        private IEnumerator PickGameChoice(ChoiceDialogPanel panel, (string text, Action action) choice)
        {
            yield return new WaitForSecondsRealtime(Context.ChoiceDelay);
            Log.LogInfo("自动选择：" + choice.text);
            Context.MarkProgress();
            panel.Hide(choice.action);
        }
    }

    [HarmonyPatch]
    internal static class Patches
    {
        /// <summary>自动确认“播放视频”对话框：不显示对话框，直接执行播放回调。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CommonDialogPanel), nameof(CommonDialogPanel.ShowPlayVideo))]
        private static bool ShowPlayVideoPrefix(Action onPlay)
        {
            if (!YorutinaAdapter.AutoConfirmPlayDialog) return true;
            YorutinaAdapter.AutoConfirmPlayDialog = false;
            onPlay?.Invoke();
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChoiceDialogPanel), nameof(ChoiceDialogPanel.Show))]
        private static void ChoiceShowPostfix(ChoiceDialogPanel __instance, List<(string text, Action action)> choiceList)
        {
            YorutinaAdapter.Instance?.OnGameChoiceShown(__instance, choiceList);
        }
    }
}
