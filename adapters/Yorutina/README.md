# 扩展包：Yorutina

- 引擎：Unity 6 Mono + Naninovel，BepInEx 5.4.23.6（5.4.23.2 自带的 Doorstop 会让游戏启动即崩）。
- 用法：进入**标题画面**后按 F8。
- 录制范围：画廊里的全部条目（默认包括未解锁的；游戏本身也有“全部解锁”按钮）。

## 做了什么

| 步骤 | 实现 |
| --- | --- |
| 列出回想 | `GlobalSave.galleryList`，按 `scenarioNumber` 排序，按类型过滤 |
| 启动 | `VideoSiteUtil.ShowPlayVideoDialog(..., isGallery: true)`，Harmony 跳过“播放确认”对话框 |
| 是否在播放 | `NaniManager.isNovelPlaying`；回到画廊的回调触发后才启动下一段 |
| 播放中 | Naninovel AUTO 打开、快进关闭；Naninovel 选项和游戏自己的 `ChoiceDialogPanel` 都自动选第一项 |
| 卡死检测 | Naninovel `PlayedCommandsCount` 长时间不变 |
| 强制结束 | `NaniManager.BackToGame()` |

## 设置（`BepInEx\config\unitygalleryrecorder.adapter.yorutina.cfg`）

| 项 | 默认 | 说明 |
| --- | --- | --- |
| `Types` | 全部 7 类 | `Video_Yorutina, Video_Hidden, Video_Mob, Story, Love, Cm, PartTime` |
| `OnlyUnlocked` | false | 只录已解锁的 |

输出文件名：`<场景编号4位>_<ID>_<标题>.mp4`。
