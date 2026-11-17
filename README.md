# Unity Gallery Recorder

给 Unity（Mono）游戏用的 BepInEx 5 插件框架（另有 [RPG Maker MV 版](rpgmaker-mv)）：**自动依次播放游戏画廊 / 回想里的每一段，每段单独录成一个视频，全部录完后统一压缩。**

框架分两层：

- **核心** `GalleryRecorder.Core`：录制、队列、进度（断点续录）、卡死 / 超时处理、压缩、热键、状态浮层。和具体游戏无关。
- **扩展包**：每个游戏一个，只负责“列出回想、启动一段、判断是否在播放、播放中自动前进和处理选项”。几十到几百行代码。

> A BepInEx 5 framework for Unity (Mono) games that auto-plays every gallery / recollection entry, records each one to its own video, and compresses them at the end. Game-specific logic lives in small adapter packs.

## 已支持的游戏

| 扩展包 | 游戏 | 说明 |
| --- | --- | --- |
| [Yorutina](adapters/Yorutina) | Yorutina | 标题画面按 F8；录画廊里全部条目 |
| [KureaStruggle](adapters/KureaStruggle) | Kurea Struggle | 进入回想房后按 F8；有选项的回想会把每个分支都录一遍 |

RPG Maker MV（NW.js）游戏用 [rpgmaker-mv/](rpgmaker-mv) 下的 js 版，流程相同：

| 扩展包 | 游戏 | 说明 |
| --- | --- | --- |
| [SAO2](rpgmaker-mv/adapters/SAO2) | SAO~气息遮断魔法陷阱 Ⅱ | 读档进入地图后按 F8；录每段回想的“仅限事件” |

想支持新游戏：看 [写一个扩展包](docs/writing-an-adapter.md)（Unity），或 [RPG Maker MV 版说明](rpgmaker-mv/README.md#写一个扩展包)。欢迎提交 PR。

## 功能

- **内置录制**（默认）：在游戏内部抓取画面和游戏声音，用 ffmpeg 的显卡硬件编码（`h264_mf`）实时编码。只录游戏本身，窗口被挡住、放在别的虚拟桌面也能录；画面按真实时间补帧 / 丢帧，保证音画同步。
- **Game Bar 录制**（备选）：用 Win+Alt+R 调用 Windows 自带的 Xbox Game Bar，录完自动把视频移到输出目录并改名。游戏窗口必须在前台。
- 播放时自动打开 AUTO、自动选择选项（扩展包可以实现“每个分支都录一遍”）。
- 进度写在 `progress.txt`，中途退出后再按 F8 会从没录的那段继续。
- 全部录完后用 `hevc_mf`（显卡 HEVC）压缩到 `Compressed\`，可选打包 zip。
- **整合版**：最后再把所有分段按顺序拼成一个视频，原版、压缩版各一个，放在 `Merged\`（`全部回想.mp4`、`全部回想_压缩.mp4`）。无损拼接（不重新编码，几分钟就完），每段是一个章节，播放器里可以按章节跳。分散版照常保留。

### 压缩对画质和声音的影响

| | 原版 | 压缩版 |
| --- | --- | --- |
| 视频 | H.264，高码率 | HEVC（H.265），`quality 60` |
| 声音 | AAC 256 kbps | AAC 160 kbps |
| 体积 | 1 | 约 1/2（Unity 实测）到 1/10（RPG Maker 静态画面多） |

- 画面：正常观看基本看不出差别；暂停放大时细线条、暗部渐变会略糊一点。分辨率和帧率不变。
- 声音：160 kbps AAC 对游戏语音和 BGM 已经足够，一般听不出区别。
- 想要更清楚：把压缩参数里的 `quality` 调高（例如 75），体积会变大。长期保存建议原版也留一份。

## 安装

1. 给游戏装 [BepInEx 5](https://github.com/BepInEx/BepInEx/releases)（x64，**5.4.23.6 或更新**；有的游戏在旧版 Doorstop 下启动就崩）。先运行一次游戏，让它生成 `BepInEx\plugins` 等目录。
2. 把核心和对应游戏的扩展包放进 `BepInEx\plugins\GalleryRecorder\`：
   ```
   <游戏目录>\BepInEx\plugins\GalleryRecorder\
     GalleryRecorder.Core.dll
     GalleryRecorder.<游戏>.dll
     ffmpeg\ffmpeg.exe
   ```
3. ffmpeg 需要自己放（体积大、有单独的许可证，不随本项目分发）。可以用脚本下载：
   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\get-ffmpeg.ps1 -Dest "<游戏目录>\BepInEx\plugins\GalleryRecorder\ffmpeg"
   ```
   也可以手动放任意一个带 `h264_mf` / `hevc_mf` 编码器的 Windows 版 ffmpeg。
4. 建议在 `BepInEx\config\BepInEx.cfg` 里设置 `HideManagerGameObject = true`。

只用 Game Bar 模式时可以不装 ffmpeg，但最后的压缩需要它。

> **把游戏放在固态硬盘上录。** 录制时游戏要边读素材边播，插件同时在写录像、后台转码，这些都挤在游戏所在的那块盘上。游戏放在 USB 移动机械硬盘时，实测掉帧很严重（Boss 战只剩 30 fps、长剧情 15 fps、时不时卡住几秒）；把整个游戏复制到 NVMe 固态后明显好转。游戏原本在机械硬盘上的话，先把整个游戏目录复制到固态再装插件、录制；输出目录（`OutputDir` / `outputDir`）也放在固态上。

## 使用

| 热键 | 作用 |
| --- | --- |
| F8 | 开始；运行中再按 = 录完当前这段后停止 |
| F7 | 放弃当前这段（不保存，记为跳过），继续下一段 |
| F6 | 清空进度（空闲时） |
| F9 | 显示 / 隐藏左上角状态（录制时自动隐藏，不会录进视频） |
| F10 | 立刻压缩输出目录里已有的录像（空闲时；完成后接着做整合版） |
| F11 | 立刻把已有的录像拼成整合版（空闲时） |

输出默认在 `<游戏目录>\GalleryRecordings\`。热键、录制参数、压缩参数都在 `BepInEx\config\unitygalleryrecorder.core.cfg` 里改，扩展包自己的设置在各自的 cfg 里。

### 常见问题

- **画面上下颠倒**：把 `[Record] FlipVertical` 改成相反的值。和显卡 API 有关，D3D11 / D3D12 一般是 `false`。
- **没有声音**：内置录制截取的是游戏内部的最终混音。在 Windows 音量合成器里把游戏静音不影响录制，但在游戏设置里调低音量会。
- **压缩全部失败**：MediaFoundation 编码器（`*_mf`）只接受 nv12 输入，压缩参数里要有 `-pix_fmt nv12`（插件会自动补上）。
- **没有硬件编码器**：把 `VideoEncoderArgs` 改成 `-c:v libopenh264 -b:v 20M`（需要 ffmpeg 带 openh264），或 `-c:v libx264 -crf 18`。
- **放到别的虚拟桌面录**：内置录制模式可以；Game Bar 模式不行。
- **掉帧 / 一卡一卡**：先看游戏和输出目录是不是在机械硬盘（尤其 USB 移动硬盘）上，是的话整个复制到固态再录，见上面“安装”最后一条。

## 从源码编译

需要 .NET SDK（6 或更新）。游戏的 DLL 不随仓库提供，编译时直接引用你本机的游戏目录：

```powershell
copy local.props.example local.props   # 改成你自己的游戏路径
dotnet build -c Release
```

- 只编译核心：`dotnet build src/GalleryRecorder.Core -c Release`（需要任意一个装了 BepInEx 5 的 Unity Mono 游戏作为引用来源，见 `RefGameDir`）。
- `local.props` 里 `Deploy=true` 时，编译后自动复制到对应游戏的 `BepInEx\plugins\GalleryRecorder`。
- `tools\package.ps1` 会把编译结果打成可直接解压到游戏目录的 zip（输出到 `dist\`）。

## 目录

```
src/GalleryRecorder.Core/   核心插件（Unity / BepInEx）
adapters/<游戏>/            各游戏扩展包（每个都是独立的 BepInEx 插件）
rpgmaker-mv/                RPG Maker MV 版：core/、adapters/<游戏>/、tools/install.ps1
docs/                       文档
tools/                      ffmpeg 下载、打包脚本
```

## 许可

本项目代码采用 [MIT](LICENSE)。ffmpeg、BepInEx、Harmony 以及各游戏本身的版权和许可归各自所有者，本仓库不包含它们的任何文件。本工具仅供录制你自己拥有的游戏内容用于个人存档，请遵守游戏的使用条款。
