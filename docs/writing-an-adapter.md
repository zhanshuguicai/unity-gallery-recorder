# 写一个扩展包

扩展包是一个独立的 BepInEx 5 插件，依赖核心，在 `Awake` 里把自己注册给核心。核心负责录制、队列、进度、超时、压缩和热键，扩展包只回答几个和游戏有关的问题。

## 1. 先摸清游戏

用 [dnSpyEx](https://github.com/dnSpyEx/dnSpy) 或 [ILSpy](https://github.com/icsharpcode/ILSpy) 打开 `<游戏>_Data\Managed\Assembly-CSharp.dll`，找到这几样：

| 要找的 | 例子 |
| --- | --- |
| 回想 / 画廊的数据列表 | `GlobalSave.galleryList`、回想房里的 NPC 组件 |
| 画廊里点“播放”最终调用的方法 | `VideoSiteUtil.ShowPlayVideoDialog(...)`、`HSceneRecollectionUtility.TryStartReplay(...)` |
| “正在播放”的标志 | `NaniManager.isNovelPlaying`、`HScenePlaybackContext.IsReplayActive` |
| 自动前进（AUTO） | Naninovel 的 `IScriptPlayer.SetAutoPlay`、`DialogueManager.SetAutoAdvanceEnabled` |
| 选项 | 选项面板、按钮回调 |
| 怎么从回想里退出（可选） | `BackToGame()` 之类 |

常见引擎 / 框架的线索：

- **Naninovel**：`Engine.GetService<IScriptPlayer>()`（AUTO、快进、已执行命令数），`IChoiceHandlerManager`（选项）。
- **自制对话系统**：找带 `Auto`、`Choice`、`Skip` 字样的单例 Manager。
- 点击播放后如果弹出确认框，用 Harmony 前缀跳过它（见 Yorutina 扩展包）。

## 2. 建项目

复制 `adapters/Yorutina` 改名，或者照这个最小结构：

```
adapters/MyGame/
  GalleryRecorder.MyGame.csproj
  MyGameAdapter.cs
  README.md
```

csproj 里引用核心项目和游戏的 DLL（路径来自 `local.props` 里你新加的 `MyGameDir`）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>GalleryRecorder.MyGame</AssemblyName>
    <GameDir>$(MyGameDir)</GameDir>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\GalleryRecorder.Core\GalleryRecorder.Core.csproj" Private="false" />
    <Reference Include="$(GameDir)\BepInEx\core\BepInEx.dll" Private="false" />
    <Reference Include="$(GameDir)\*_Data\Managed\Assembly-CSharp.dll" Private="false" />
    <Reference Include="$(GameDir)\*_Data\Managed\UnityEngine.CoreModule.dll" Private="false" />
  </ItemGroup>
</Project>
```

然后 `dotnet sln add adapters/MyGame/GalleryRecorder.MyGame.csproj`。

## 3. 实现 GalleryAdapter

```csharp
[BepInPlugin("unitygalleryrecorder.adapter.mygame", "Gallery Recorder - MyGame", "0.1.0")]
[BepInDependency(GalleryRecorderPlugin.GUID)]
[BepInProcess("MyGame.exe")]          // 只在这个游戏里加载
public class MyGameAdapterPlugin : BaseUnityPlugin
{
    private void Awake() => GalleryRecorderPlugin.Register(new MyGameAdapter());
}

public sealed class MyGameAdapter : GalleryAdapter
{
    public override string GameName => "MyGame";

    public override bool CanBeginSession(out string reason)
    {
        reason = "请先进入标题画面";
        return TitleScene.Instance != null;
    }

    public override IList<GalleryEntry> BuildQueue() =>
        Gallery.AllEntries.Select(e => new GalleryEntry
        {
            Key = e.Id,                       // 稳定唯一，用于断点续录
            Title = e.DisplayName,
            FileName = $"{e.Order:000}_{e.DisplayName}",
            Data = e,
        }).ToList();

    public override bool Launch(GalleryEntry entry, int run) => Gallery.Play((GalleryItem)entry.Data);

    public override bool IsPlaying => Story.IsPlaying;

    public override void WhilePlaying()
    {
        if (Context.ForceAuto) Story.Auto = true;
    }
}
```

### 生命周期

```
F8 → CanBeginSession → BuildQueue（核心按 progress.txt 过滤掉已完成的）
每一段：
  等 IsReadyForNext（+ DelayBetweenSeconds）
  Launch(entry, run)
  等 IsPlaying 变 true              ← 开始录制
  每帧 WhilePlaying()
  IsPlaying 变 false                ← 停止录制
  等 IsReadyForNext
  ShouldReplay(entry, run) 为 true → 同一段 run+1 再录（文件名加 RunSuffix）
  否则记为完成，下一段
全部完成 → OnSessionEnd → 压缩
```

### 可以覆盖的成员

| 成员 | 默认 | 用途 |
| --- | --- | --- |
| `Initialize()` | 空 | 打 Harmony 补丁、检查反射字段 |
| `IsReadyForNext` | `!IsPlaying` | 播完后要回到画廊 / 房间并空闲才能启动下一段时覆盖 |
| `TracksProgress` + `Context.MarkProgress()` | 不检测 | 有“脚本在推进”的信号时打开，核心据此判断卡死 |
| `ForceStop()` | 返回 false | F7 放弃 / 卡死 / 超时时退出回想；做不到就返回 false，核心会等它自然结束 |
| `ShouldReplay(entry, run)` | false | 选项分支：记录本遍选了什么，还有没走过的分支就返回 true |
| `RunSuffix(run)` | `_分支{run}` | 重录时的文件名后缀 |
| `OnSessionEnd()` | 空 | 清理状态 |

`Context` 里有：`Log`、`Host`（常驻 MonoBehaviour，可跑协程）、`ForceAuto` / `AutoChoice` / `ChoiceDelay`（核心配置）、`IsRecording`、`MarkProgress()`。

### 注意

- 所有回调都在 Unity 主线程。
- `Key` 不要用标题（会随语言变化），用游戏内部的 ID。
- 游戏切场景时对象会被销毁，`GalleryEntry.Data` 里只放跨场景仍然有效的数据（ScriptableObject、纯数据类、ID）。
- 访问私有字段用反射时，在 `Initialize` 里检查是否为 null，游戏更新后好排查。
- 不要把游戏的 DLL、反编译出来的代码、截图提交进仓库。

## 4. 测试清单

1. 日志（`BepInEx\LogOutput.log`）里出现 `扩展包：MyGame`。
2. F8 后日志列出的条目数和画廊一致。
3. 第一段能自动开始播放，`GalleryRecordings` 里出现视频，画面方向正确、有声音。
4. 播完能自动开始下一段；F7 能放弃当前段。
5. 中途退出游戏，再按 F8 从没录的那段继续。
6. 全部录完后 `Compressed\` 里有压缩版。
