// 扩展包：Kurea Struggle（Unity 6 Mono，自制对话系统）
// 回想在“回想房”场景里：房间里每个 NPC（HSceneRecollectionInteractable）对应一段或多段回想，
// 用 HSceneRecollectionUtility.TryStartReplay 启动；HScenePlaybackContext.IsReplayActive 表示正在播放。
// 回想里有选项时，同一段会重播，按深度优先把每个选项组合都录一遍。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using PCompany.Events;
using PCompany.Managers;
using PCompany.UI;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GalleryRecorder.Adapters.KureaStruggle
{
    [BepInPlugin(GUID, "Gallery Recorder - Kurea Struggle", "0.1.0")]
    [BepInDependency(GalleryRecorderPlugin.GUID)]
    [BepInProcess("Kurea Struggle.exe")]
    public class KureaAdapterPlugin : BaseUnityPlugin
    {
        public const string GUID = "unitygalleryrecorder.adapter.kureastruggle";

        private void Awake()
        {
            GalleryRecorderPlugin.Register(new KureaStruggleAdapter
            {
                ExploreBranches = Config.Bind("KureaStruggle", "ExploreAllBranches", true,
                    "回想里有选项时（如不同结局），同一段回想重复播放，直到每个选项组合都录到"),
                StopBgm = Config.Bind("KureaStruggle", "StopBgmOnLoad", true, "开始回想时停止房间 BGM（NPC 自己有设置时以 NPC 为准）"),
            });
        }
    }

    public sealed class KureaStruggleAdapter : GalleryAdapter
    {
        private const string RoomSceneName = "[G] RecollectionRoom";
        private const int MaxChoiceDepth = 30;

        public ConfigEntry<bool> ExploreBranches;
        public ConfigEntry<bool> StopBgm;

        private sealed class Item
        {
            public HSceneRecollectionEntry Entry;
            public bool StopBgm;
        }

        // 分支探索：branchPrefix = 这一遍前几次选项要选的序号；runPicks/runCounts = 这一遍实际选的序号/可选数量
        private List<int> branchPrefix = new List<int>();
        private readonly List<int> runPicks = new List<int>();
        private readonly List<int> runCounts = new List<int>();
        private readonly List<string> runPickTexts = new List<string>();
        private object lastHandledChoiceTcs, choiceSeenTcs;
        private float choiceSeenAt = -1f;

        private static readonly BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo fDatabase = typeof(HSceneRecollectionInteractable).GetField("database", BF);
        private static readonly FieldInfo fGrouped = typeof(HSceneRecollectionInteractable).GetField("groupedEntryIds", BF);
        private static readonly FieldInfo fStopBgm = typeof(HSceneRecollectionInteractable).GetField("stopBgmOnLoad", BF);
        private static readonly FieldInfo fChoicePanel = typeof(DialogueManager).GetField("choicePanel", BF);
        private static readonly FieldInfo fChoiceTcs = typeof(ChoicePanel).GetField("_choiceTcs", BF);
        private static readonly FieldInfo fChoiceButtons = typeof(ChoicePanel).GetField("_choiceButtons", BF);
        private static readonly FieldInfo fOnClicked = typeof(ChoiceButton).GetField("_onClicked", BF);
        private static readonly FieldInfo fChoiceText = typeof(ChoiceButton).GetField("choiceText", BF);

        public override string GameName => "Kurea Struggle";

        public override void Initialize()
        {
            if (fDatabase == null || fGrouped == null || fChoicePanel == null || fChoiceTcs == null || fChoiceButtons == null)
                Log.LogWarning("找不到部分游戏内部字段，游戏版本可能已更新，回想列表或自动选项可能不可用。");
        }

        public override bool CanBeginSession(out string reason)
        {
            reason = null;
            if (SceneManager.GetActiveScene().name != RoomSceneName) { reason = "请先进入回想房再开始"; return false; }
            if (!IsRoomIdle()) { reason = "回想房还没空闲（对话、加载中？），稍后再按"; return false; }
            return true;
        }

        /// <summary>回想房里所有 NPC 的已解锁回想。顺序：从上到下、从左到右；同一 NPC 的多段按其列表顺序。</summary>
        public override IList<GalleryEntry> BuildQueue()
        {
            var result = new List<GalleryEntry>();
            var seen = new HashSet<string>();
            var npcs = UnityEngine.Object.FindObjectsByType<HSceneRecollectionInteractable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(i => i != null && i.InteractionEnabled)
                .OrderByDescending(i => Mathf.Round(i.transform.position.y * 100f))
                .ThenBy(i => i.transform.position.x);

            foreach (var npc in npcs)
            {
                bool stopBgm = StopBgm.Value;
                if (fStopBgm != null) { try { stopBgm = (bool)fStopBgm.GetValue(npc); } catch { } }
                foreach (var entry in ResolveEntries(npc))
                {
                    if (entry == null || !HSceneRecollectionUtility.IsUnlocked(entry)) continue;
                    string key = MakeKey(entry);
                    if (!seen.Add(key)) continue;
                    string name;
                    try { name = entry.GetDisplayName(); } catch { name = key; }
                    result.Add(new GalleryEntry
                    {
                        Key = key,
                        Title = name,
                        FileName = $"{result.Count + 1:000}_{name}",
                        Data = new Item { Entry = entry, StopBgm = stopBgm },
                    });
                }
            }
            return result;
        }

        public override bool Launch(GalleryEntry entry, int run)
        {
            if (run <= 1) branchPrefix = new List<int>();
            runPicks.Clear();
            runCounts.Clear();
            runPickTexts.Clear();
            lastHandledChoiceTcs = null;
            choiceSeenTcs = null;
            choiceSeenAt = -1f;
            var item = (Item)entry.Data;
            return HSceneRecollectionUtility.TryStartReplay(item.Entry, true, item.StopBgm);
        }

        public override bool IsPlaying => HScenePlaybackContext.IsReplayActive;

        public override bool IsReadyForNext => IsRoomIdle();

        public override void WhilePlaying()
        {
            if (Context.ForceAuto)
            {
                var dm = DialogueManager.Instance;
                if (dm != null && !dm.IsAutoAdvanceEnabled) dm.SetAutoAdvanceEnabled(true);
            }
            if (Context.AutoChoice) HandleChoices();
        }

        public override bool ShouldReplay(GalleryEntry entry, int run)
        {
            if (runPicks.Count > 0) Log.LogInfo("本遍选项路径：" + string.Join(" → ", runPickTexts.ToArray()));
            if (!ExploreBranches.Value) return false;
            // 从最后一个还有下一个选项的位置分叉
            for (int i = Math.Min(runPicks.Count, MaxChoiceDepth) - 1; i >= 0; i--)
            {
                if (runPicks[i] + 1 < runCounts[i])
                {
                    branchPrefix = runPicks.Take(i).ToList();
                    branchPrefix.Add(runPicks[i] + 1);
                    return true;
                }
            }
            return false;
        }

        // ---------------- 选项 ----------------
        private void HandleChoices()
        {
            var dm = DialogueManager.Instance;
            if (dm == null || fChoicePanel == null) return;
            var panel = fChoicePanel.GetValue(dm) as ChoicePanel;
            if (panel == null || !panel.isActiveAndEnabled) return;
            object tcs = fChoiceTcs?.GetValue(panel);
            var buttons = fChoiceButtons?.GetValue(panel) as List<ChoiceButton>;
            if (tcs == null || buttons == null || buttons.Count == 0 || tcs == lastHandledChoiceTcs) return;

            float now = Time.realtimeSinceStartup;
            if (tcs != choiceSeenTcs) { choiceSeenTcs = tcs; choiceSeenAt = now; return; }
            if (now - choiceSeenAt < Context.ChoiceDelay) return;

            var usable = buttons.Where(b => b != null && b.gameObject.activeInHierarchy && IsInteractable(b)).ToList();
            if (usable.Count == 0) return;

            int depth = runPicks.Count;
            int pick = 0;
            if (ExploreBranches.Value && depth < branchPrefix.Count) pick = Mathf.Clamp(branchPrefix[depth], 0, usable.Count - 1);
            if (depth >= MaxChoiceDepth) pick = usable.Count - 1; // 防止循环选项无限展开

            string text = ButtonText(usable[pick]);
            runPicks.Add(pick);
            runCounts.Add(usable.Count);
            runPickTexts.Add(text);
            lastHandledChoiceTcs = tcs;
            Log.LogInfo($"自动选择：第 {pick + 1}/{usable.Count} 项「{text}」");
            if (fOnClicked?.GetValue(usable[pick]) is Action act) act();
            else usable[pick].onClick.Invoke();
        }

        private static bool IsInteractable(ChoiceButton b)
        {
            try
            {
                var p = b.GetType().GetProperty("CanInteract", BF);
                if (p != null) return (bool)p.GetValue(b, null);
            }
            catch { }
            return b.CanInteract;
        }

        private static string ButtonText(ChoiceButton b)
        {
            try
            {
                var t = (fChoiceText?.GetValue(b) as TMP_Text) ?? b.GetComponentInChildren<TMP_Text>(true);
                if (t != null) return Regex.Replace(t.text ?? "", "<.*?>", "").Trim();
            }
            catch { }
            return "?";
        }

        // ---------------- 游戏状态 ----------------
        /// <summary>在回想房、不在加载、自由行动、不在对话、没有挂起的回想请求。</summary>
        private static bool IsRoomIdle()
        {
            if (SceneManager.GetActiveScene().name != RoomSceneName) return false;
            var lm = LoadingManager.Instance;
            if (lm != null && lm.IsSceneLoading) return false;
            var gm = GameManager.Instance;
            if (gm == null || gm.CurrentState != GameManager.GameState.FreeRoam || gm.PlayerController == null) return false;
            if (HScenePlaybackContext.IsPlaybackActive || HScenePlaybackContext.HasPendingRequest) return false;
            var dm = DialogueManager.Instance;
            if (dm != null && dm.CurrentDialogue != null) return false;
            return true;
        }

        private static IEnumerable<HSceneRecollectionEntry> ResolveEntries(HSceneRecollectionInteractable npc)
        {
            var db = fDatabase?.GetValue(npc) as HSceneRecollectionDatabase;
            if (fGrouped?.GetValue(npc) is List<string> grouped && grouped.Any(s => !string.IsNullOrWhiteSpace(s)))
            {
                foreach (var id in grouped)
                {
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (HSceneRecollectionUtility.TryFindEntry(id.Trim(), db, out var e) && e != null) yield return e;
                }
                yield break;
            }
            if (npc.TryResolveEntry(out var single) && single != null) yield return single;
        }

        private static string MakeKey(HSceneRecollectionEntry e)
        {
            if (!string.IsNullOrWhiteSpace(e.entryId)) return e.entryId.Trim();
            return (e.dialogueKey ?? "") + "#" + (e.useStartIndex ? e.startIndex : -1);
        }
    }
}
