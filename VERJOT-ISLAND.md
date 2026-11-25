# Verjot Island（片言 · 灵动岛版）

Verjot 跑在灵动岛上：岛体来自 [Atoll](https://github.com/Fuwni/dynamic-island-windows)（MIT，C# / WPF / .NET 10），
Verjot 的会议录音和速记做成岛上的页面，并新增“通知”页，统一处理 **Windows 系统通知、微信、企业微信**。

> 本目录是 Atoll 的克隆，在 `verjot-island` 分支上改动。上游代码保持原名（命名空间 `DynamicIsland`），Verjot 的新代码都在 `Verjot/` 目录。Verjot 原仓库（WinForms 版）克隆在 `E:\zdwork\Verjot`，未改动。

## 运行

| 事项 | 说明 |
| --- | --- |
| 程序 | `bin\Debug\net10.0-windows10.0.19041.0\Verjot.exe`（Debug 版，内存约 230 MB；Release 版未发布） |
| 运行时 | .NET 10 Desktop Runtime（本机已装 10.0.12） |
| SDK | 本机原本没有。装在 `E:\zdwork\tools\dotnet`（10.0.404，便携目录，没有改系统 PATH） |
| 构建 | `E:\zdwork\tools\dotnet\dotnet.exe build -c Release` |
| 单元测试 | `E:\zdwork\tools\dotnet\dotnet.exe test tests/DynamicIsland.Tests`（上游 280 项，当前全部通过） |
| 数据 | 设置 `%APPDATA%\Verjot\settings.json`，日志 `%LOCALAPPDATA%\Verjot\log.txt` |

首次运行需要在 Windows 里允许访问通知：设置 → 通知 → 允许应用访问通知。通知页顶部会有提示，点击可直达。

## 用法

| 操作 | 方式 |
| --- | --- |
| 看通知 | 鼠标移到岛上 → “通知”页。按来源筛选（全部 / 微信 / 企业微信 / 系统 / 其他），悬停卡片出现“打开 / 记下 / 复制 / 移除”，左滑删除 |
| 新通知 | 岛体弹开成预览（来源图标 + 标题 + 正文），底部细线走完后收起，未读数显示在胶囊右侧 |
| 点通知 | 和 Windows 的通知一样：点一下就打开发通知的程序，同时这条标为已读。按顺序尝试：已知窗口（最小化的先还原）→ 微信/企业微信自己的协议 → 打包应用的激活接口 → 程序自己的 exe（只在没在运行时）。都打不开会提示“没能打开”，不会静默消失 |
| 看长消息 | 卡片上悬停出现“展开”，或把鼠标停在正文上看完整内容；正文被截断不影响点击打开 |
| 录会议 | “会议”页，或 `Ctrl+Alt+L`。`Ctrl+Alt+K` 标记重点。录音时胶囊常驻“红色麦克风 + 计时 + 声音条” |
| 速记 | “速记”页，或 `Ctrl+Alt+J`。回车保存 |
| 文件位置 | 笔记 `Documents\Verjot\速记\YYYY-MM-DD.md`；会议 `Documents\Verjot\VerjotYYYYMMDDHHMM\VerjotYYYYMMDDHHMM.mp3`（与 Verjot 原版同一套约定）。可在 `settings.json` 的 `VerjotFolder` 里改 |

页签只剩六个：首页、通知、会议、聊天、速记、剪贴板（另有设置）。2026-11-25 起按用户要求把 Atoll 的其余部分整体删除（删代码而非隐藏）：音乐、计时器、天气、日历、系统页、桌面小组件、截图/OCR/二维码、录屏、游戏模式/FPS、勿扰、Claude Code/Codex 钩子、命令行面板、文件拖放、季节主题、均衡器、Discord。依赖也随之砍掉：WebView2、ScreenRecorderLib、ZXing、TraceEvent；只保留 NAudio。相关测试同步删除（剩余 20 项全部通过）。

首页重做为：会议卡片（状态 + 开始/结束）+ 快速记一笔 + 打开便签 + 最近通知。

聊天页对接用户自己的 Hermes：填 OpenAI 兼容的 `/v1/chat/completions` 地址、密钥（可选）、模型名。Hermes 侧需要在 config.yaml 里启用 `api_server`（目前是注释状态）。对话历史只在内存里。

## 通知是怎么采到的

| 来源 | 机制 | 能拿到什么 |
| --- | --- | --- |
| Windows 系统通知 | `UserNotificationListener` 每秒轮询（系统 API，需要授权） | 应用名、标题、正文、图标 |
| 发送方缺失的传统程序 | 从系统通知数据库的副本里按通知 ID 查发送方（`WpnDatabase.cs`，用系统自带 `winsqlite3.dll`） | 补出 AppUserModelId，再映射到进程、图标和产品名 |
| 微信、企业微信 | 任务栏按钮闪动事件（`RegisterShellHookWindow` → `HSHELL_FLASH`） | 只有“有新消息”，没有正文 |

为什么微信和企业微信只有“有新消息”：实测（本机微信 4.2.0.53、企业微信 5.0.13.8030）这两个程序不发系统 Toast（通知数据库里微信只有“分享扩展”一条记录，企业微信没有记录），而且它们的界面是 Qt，UI 自动化树只有外壳节点，读不到会话列表。要读正文只能改微信进程内存，有封号和稳定性风险，本版不做。

其它限制：
- 闪动事件只在应用窗口在任务栏上时才有；缩到托盘里的窗口不会闪。
- 传统程序发送方的数据库结构没有官方文档，Windows 更新后可能失效；失效时只影响“显示谁发的”，不影响通知本身。
- 同一应用 8 秒内的连续闪动合并成一条。

## 代码结构

```
Verjot/
  Services/
    ToastReader.cs        读取系统通知（每个 Toast 只报一次）
    WpnDatabase.cs        按通知 ID 查发送方
    SenderResolver.cs     发送方 → 名称、图标、进程、来源分类（微信 / 企业微信 / 系统 / 其他）
    NotificationHub.cs    通知列表、未读计数
    MeetingRecorder.cs   会议录音：麦克风 + 电脑声音 → 混音 → MP3（192 kbps，Windows 自带编码器）
    VerjotFiles.cs        笔记与会议的目录、每天一个 Markdown、最近会议
    V.cs                  新页面的中英文文案
  Views/
    NoticeList.cs         通知卡片列表（弹簧展开、滑动删除）
    LevelWave.cs          声音条
  MainWindow.Alerts.cs    通知页、预览、队列、点击回到应用
  MainWindow.Meeting.cs   会议页、胶囊里的实时活动、快捷键
  MainWindow.VerjotPages.cs  默认页签、速记页的文件入口
  MainWindow.VerjotShots.cs  诊断运行（仅 Debug）
```

对上游文件的改动都很小，集中在：`MainWindow.xaml`（通知页、会议页、Toast 结构）、`MainWindow.xaml.cs`（页签数组、快捷键、托盘菜单、退出时保存录音）、`MainWindow.Live.cs`（实时活动、轮询入口）、`MainWindow.Activity.cs`（闪动事件）、`Services/NotesStore.cs`（改为读写 Verjot 的 Markdown）、`DynamicIsland.csproj`（程序名、版本、版权）。

约定：
- 新增界面文案用 `V.T("中文", "English")`，不走上游的俄语键翻译表。
- 上游的 `--run` 测试机制被复用：`Verjot.exe --run verjotshots <目录> --lang zh` 会渲染各页面截图到目录，并真实录一段音验证 MP3 导出。

## 验证记录（2026-11-25，本机）

| 项目 | 结果 |
| --- | --- |
| 构建 | Debug 通过，0 警告 0 错误 |
| 上游单元测试 | 280 / 280 通过 |
| 通知读取 | 探测工具和真实运行各一次，`RequestAccessAsync` 返回 `Allowed`；系统 Toast 发出后 1 秒内出现在日志 |
| 发送方识别 | `app:desktop` 类程序（如 WorkBuddy）`AppInfo` 抛异常，走数据库回退后识别到进程 |
| 微信 / 企业微信闪动 | 用探测工具把 `HSHELL_FLASH` 消息按窗口句柄投给运行中的应用，日志各出现一条“有新消息”。**没有用真实消息验证过**（当时两个窗口都缩到了托盘，没有任务栏按钮） |
| 点击打开程序 | `--run focuscheck`：最小化的记事本被切到前台并保持（150–1200 ms 采样的前台窗口都是它）；微信从托盘被 `weixin://` 拉起并成为前台窗口。多次运行都通过。测试机上 Hermes（Chrome 窗口）有时会立刻把前台抢回去，这是外部程序的行为，与岛无关 |
| 点击已读 | 点预览或卡片都会先标为已读（未读绿条消失、胶囊未读数减少），再执行打开 |
| 录音导出 | 真实录制 6.5 秒 → MP3 213–232 KB，`work/` 清理，重点标记写入同名 `.md` |
| 界面 | 诊断截图：通知页、Toast 预览、录音中的胶囊、会议页、速记页渲染正常 |
| 点击通知回到应用 | 第一版 P/Invoke 漏写入口名，抛过 `EntryPointNotFoundException`；根因是岛体不抢焦点，原来的 `SetForegroundWindow` 被系统拒绝，表现就是“点了、消息消失、程序没打开”。已改为借用前台线程 + 备用 Alt 键 + 打包应用激活接口，并用 `--run focuscheck` 验证 |
| 性能 | 日志出现过两次“UI 线程 4 秒无响应”（都是 `last step: tick`），同一份日志里 `fullscreen watch` 的最大耗时为 46 s，指向系统睡眠/锁屏造成的时间跳变；活跃使用中没有复现，原因仍未最终确认 |

## 还没做的

- 微信、企业微信的**正文**（见上）。可行的下一步是对它们自己的弹窗做截图 OCR，需要真实消息来调。
- 原版 Verjot 的便签编辑器（RichEdit、贴边标签、图片粘贴）**还没移植**（已排期）：现在“打开便签”按钮先用系统默认编辑器打开当天的 Markdown 文件；速记页仍是纯文本列表。这是下一个要做的。
- 聊天对接 Hermes 已完成岛内一侧；Hermes 侧启用 api_server 后即可用。流式输出（打字机效果）还没做。
- 转写 API 没有移植。
- 没有安装包，没有发布配置，Release 版没跑过。
- 安装包、第三方声明文件、应用图标之外的产物都还是 Atoll 的。

## 许可

- 上游 Atoll：MIT，版权归 mykolaichechelnytskyi，`LICENSE` 原样保留。
- Verjot：MIT（原仓库 `E:\zdwork\Verjot\LICENSE`）。
- 两边都兼容，可以合并分发。注意 fly-island（GPL-3.0）、浮屿 Fuyu（AGPL-3.0）的代码没有用，也不能用。
