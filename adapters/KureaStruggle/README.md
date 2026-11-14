# 扩展包：Kurea Struggle

- 引擎：Unity 6 Mono，自制对话系统。
- 用法：进入**回想房**，站着不动（不在对话中）时按 F8。
- 录制范围：回想房里所有已解锁的回想，从上到下、从左到右。

> 如果之前装过旧的 `RecollectionAutoPlay.dll`，请先删掉，它也用 F8。

## 做了什么

| 步骤 | 实现 |
| --- | --- |
| 列出回想 | 回想房里每个 `HSceneRecollectionInteractable`（含一个 NPC 对应多段的情况），只取已解锁的 |
| 启动 | `HSceneRecollectionUtility.TryStartReplay` |
| 是否在播放 | `HScenePlaybackContext.IsReplayActive`；回到回想房、自由行动、没有对话时才启动下一段 |
| 播放中 | `DialogueManager.SetAutoAdvanceEnabled(true)`；选项面板出现后按分支顺序自动选 |
| 选项分支 | 深度优先：每遍记录选了第几项，播完后从最后一个还有下一项的选项分叉，同一段再录一遍，直到所有组合录完（上限见核心配置 `MaxRunsPerEntry`） |
| 强制结束 | 游戏没有可靠的退出接口，不支持；F7 会丢弃这段录像，等它自然播完 |

## 设置（`BepInEx\config\unitygalleryrecorder.adapter.kureastruggle.cfg`）

| 项 | 默认 | 说明 |
| --- | --- | --- |
| `ExploreAllBranches` | true | 有选项的回想把每个分支都录一遍；false = 每段只录一遍、总选第一项 |
| `StopBgmOnLoad` | true | 开始回想时停止房间 BGM（NPC 自己有设置时以 NPC 为准） |

输出文件名：`<序号3位>_<回想名>.mp4`，同一段的第 2 遍起加 `_分支N`。
