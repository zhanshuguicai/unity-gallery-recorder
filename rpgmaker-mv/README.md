# RPG Maker MV 版

给 RPG Maker MV（NW.js）游戏用的同一套流程：**自动依次播放每段回想，每段单独录成 mp4，全部录完后统一压缩。** 结构和 Unity 版一样，分成核心和每个游戏一个的扩展包，不过这里都是普通的 MV 插件（js）。

| 扩展包 | 游戏 | 说明 |
| --- | --- | --- |
| [SAO2](adapters/SAO2) | SAO~气息遮断魔法陷阱 Ⅱ | 读档进入地图后按 F8；录每段回想的“仅限事件” |

## 怎么录

- 画面：`canvas.captureStream()` 取游戏画布，声音：把所有连到扬声器的 WebAudio 节点再连一份到录音节点。都在游戏内部完成，只录游戏本身，不录浮层，窗口被挡住或放在别的虚拟桌面也能录。
- 编码：浏览器的 MediaRecorder 先录成 `.rec.webm`，每段录完后台用 ffmpeg 转成 mp4（默认 `h264_mf` 显卡编码）。
- 全部录完：等所有转码结束，再用 `hevc_mf` 压缩到 `Compressed\`。

## 安装

```powershell
powershell -ExecutionPolicy Bypass -File rpgmaker-mv\tools\install.ps1 -GameDir "<游戏目录>" -Adapter SAO2 -Ffmpeg "<ffmpeg.exe>"
```

脚本会：

1. 把 `GalleryRecorder_Core.js` 和扩展包复制到 `www\js\plugins\`，并登记到 `www\js\plugins.js` 末尾（第一次运行时备份为 `plugins.js.gr-backup`）。
2. 在根目录 `package.json` 加 `chromium-args`，关掉 Chromium 对后台 / 被遮挡窗口的降帧（备份为 `package.json.gr-backup`）。不加的话游戏放到别的虚拟桌面会停住。
3. 把 ffmpeg 复制到 `<游戏目录>\GalleryRecorder\ffmpeg\`（需要带 `h264_mf` / `hevc_mf`；可以用 `tools\get-ffmpeg.ps1` 下载）。

卸载：同一个脚本加 `-Uninstall`（去掉插件登记和 js；`package.json` 改动用备份还原）。

## 使用

| 热键 | 作用 |
| --- | --- |
| F8 | 开始；运行中再按 = 录完当前这段后停止 |
| F7 | 放弃当前这段（不保存，记为跳过），继续下一段 |
| F6 | 清空进度（空闲时） |
| F9 | 显示 / 隐藏左上角状态（DOM 浮层，不会录进视频） |
| F10 | 立刻压缩输出目录里已有的录像（空闲时） |

- 输出：`<游戏目录>\GalleryRecordings\`，进度在 `progress.txt`，中途退出后再按 F8 从没录的那段继续。
- 设置：`<游戏目录>\GalleryRecorder\config.json`（第一次启动生成），日志：同目录 `log.txt`。
- **存档保护**：回放剧情会改动开关和变量。会话一开始，插件就把所有写入 `www\save` 的操作拦下来，直到重启游戏（`blockSaves`）。录完请直接关掉游戏。

### config.json

| 项 | 默认 | 说明 |
| --- | --- | --- |
| `outputDir` | 空 | 空 = `<游戏目录>\GalleryRecordings` |
| `fps` | 60 | 画布采样帧率，也是转码后的帧率 |
| `videoBitrate` | 24000000 | 中间文件码率 |
| `muxArgs` | h264_mf quality 90 | 中间文件转 mp4 的编码参数（浏览器能直接录 H.264 时改为直接复制） |
| `delayBetweenSeconds` | 2 | 两段之间的间隔 |
| `launchTimeoutSeconds` | 60 | 准备阶段（切换到回想房等）超时 |
| `stuckTimeoutSeconds` | 180 | 播放中这么久没有进展视为卡住，结束这段 |
| `maxMinutesPerEntry` | 30 | 单段上限 |
| `autoAdvance` / `autoAdvanceFallbackSeconds` | true / 8 | 自动翻页；对话停在等待输入超过这么久时强制翻页 |
| `blockSaves` | true | 见上面的存档保护 |
| `compress` / `compressArgs` | true / hevc_mf quality 60 | 全部录完后的压缩 |
| `deleteOriginalAfterCompress` | false | 压缩成功后删掉原始 mp4 |
| `hotkeys` | F8/F7/F6/F9/F10 | `KeyboardEvent.key` 的名字 |

## 写一个扩展包

扩展包是放在核心之后的 MV 插件，调用 `GalleryRecorder.register(adapter)`：

```js
GalleryRecorder.register({
    gameName: '游戏名',
    initialize(ctx) {},                 // ctx.log / ctx.config / ctx.markProgress()
    canBeginSession() { return null; }, // 不能开始时返回原因
    buildQueue() { return [{ key, title, fileName, data }]; },
    onSessionBegin() {},
    prepare(entry, run) { return true; },   // 每帧调用直到返回 true，然后开始录制（用来切地图、等空闲）
    launch(entry, run) {},                  // 开始播放
    whilePlaying() {},                      // 每帧
    isFinished() { return false; },         // 这段播完了
    progressToken() { return ''; },         // 可选：值一直不变 = 卡住
    forceStop() { return true; },           // 超时 / F7 时强制结束
    shouldReplay(entry, run) { return false; }, // 可选：同一段再录一遍（分支）
    onEntryEnd(entry, status) {},
    onSessionEnd() {}
});
```

参考 [adapters/SAO2](adapters/SAO2/GalleryRecorder_SAO2.js)。
