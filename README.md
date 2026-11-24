# sell fish

**sell fish 是一款简洁的 Windows 窗口控制软件，目前仍在开发中。**

从下拉框选定一个窗口后，可以隐藏窗口和它的任务栏按钮、置顶、调节不透明度、反色、灰度或让鼠标穿透。切换到别的应用后，操作仍作用于原来的窗口。

## 使用

从 [GitHub Releases](https://github.com/TreeNotBeeBee/Project_SellFish/releases) 下载 Windows x64 压缩包。解压整个文件夹后运行 **sell fish.exe**。升级前先退出旧版，让它恢复已绑定的窗口。新版沿用原来的主题和快捷键设置。

1. 在 **基础** 页从下拉框选定窗口。**始终置顶** 控制目标的叠放顺序，**主题** 可切换并记住黑色或白色外观。拖动空白处可移动控制面板。
2. 勾选 **隐藏**，或按旁边显示的全局快捷键（初始为 **Ctrl + Shift + H**）。再次按同一组合即可显示。**页面穿透**（初始为 **Ctrl + Shift + P**）让鼠标直接操作下方的窗口，再按一次恢复目标的鼠标交互。
3. 点击快捷键框，按下再松开想要的键，程序会自动保存。可以将 **左 Ctrl** 单独分配给隐藏，将 **右 Ctrl** 单独分配给穿透。单个修饰键在松开时触发，与另一个键一起按或配合鼠标点击时会取消本次单键动作。新录入的 Ctrl、Alt、Shift、Win 组合区分左右，旧配置继续响应两侧。按 Esc 取消录入。
4. 在 **不透明度** 框中输入 0 至 100 的整数。稍停、按 Enter 或离开输入框即可生效。旁边的按钮可在 0% 与上一次数值之间切换。对钉钉等会自行重设透明度的窗口，程序可用独立图层实时混合目标后面的画面。
5. **反色** 和 **灰度** 可同时开启。隐藏时保留选项，再次显示时恢复组合效果。
6. 点右上角 **—** 可收起为迷你工具栏，点 **□** 展开。两种模式共用主题和绑定，尺寸固定，都可以拖动空白处。控制面板不占任务栏位置，再次运行程序会定位到已打开的面板。
7. 在绑定下拉框选择 **释放并恢复窗口**，或点击 **×** 退出，程序会恢复目标原来的置顶、透明度、可见性和鼠标穿透状态。

### 分离 Windows 控件

在 **基础** 绑定目标，切到 **进阶**，点击 **选择控件**，再单击目标窗口里的文字或输入框。按 Esc 取消。可以从同一窗口选择多个控件，分别移动、复制和关闭它们。挑选时需要让控件露出来，并暂时关闭目标的鼠标穿透。

浮窗会跟随原控件的文本变化。对公开可写值的输入框，可编辑后点 **写回原控件**。两边同时修改时，先复制要保留的文字，再重新载入原文。实际可用的操作取决于应用向 Windows UI Automation 公开的能力。密码框不支持分离。

### 在 Chrome 和 Edge 中分离 ChatGPT

回答和输入各使用一个桌面浮窗。浏览器切换标签或最小化后，只要源标签仍在运行，扩展会继续跟随已配对的对话。Windows 标准控件可单独使用，无需安装扩展。

1. 将完整的 Windows 压缩包解压到打算保留的位置。在这个文件夹打开 PowerShell，运行 `.\Install-BrowserBridge.ps1`，为当前 Windows 用户注册本地连接程序。移动文件夹后重新运行一次。
2. 在 Chrome 打开 `chrome://extensions`，或在 Edge 打开 `edge://extensions`。开启 **开发者模式**，点击 **加载已解压的扩展程序**，选择解压目录中的 `browser-extension`。可把 **sell fish** 固定在浏览器工具栏上。两个浏览器使用同一份扩展，在要使用的浏览器里分别加载即可。
3. 打开要使用的 `chatgpt.com` 对话，在 sell fish 的 **基础** 页绑定这个浏览器窗口。进入 **进阶 → 连接网页**，再回到该对话，点一次 sell fish 扩展图标。连接提示出现后，在进阶页分别点 **回答窗** 和 **输入窗**。
4. 回答窗支持滚动及选择复制文本。滚回底部可继续跟随生成。输入窗支持中文组词，按 **Shift+Enter** 换行，按 **Enter** 或点 **发送** 提交。原网页保留完整界面，附件、语音和模型选择可在原页操作。
5. 切到另一段对话、刷新、关闭或丢弃标签会暂停或断开连接。输入窗会保留本地草稿。发送结果不明确时，先到原页核对。要在新对话继续，请重新点 **连接网页** 并在目标标签确认。释放绑定或退出会关闭这组浮窗。

连接程序通过当前 Windows 用户的本地管道传递数据。扩展在点击图标后才连接指定标签。移除时先在浏览器扩展页移除 sell fish，再从安装目录运行 `.\Uninstall-BrowserBridge.ps1`。

### 工具窗口置顶

**设置 → sell fish 始终置顶** 同时控制主面板、迷你工具栏和所有分离浮窗，包括之后打开的浮窗，重启后仍生效。**基础 → 始终置顶** 单独控制绑定的目标。收起再展开面板会回到刚才的分页。

### 自定义迷你工具栏

默认从左到右为 **隐藏、页面穿透、始终置顶**。首次启动会生成 `%LOCALAPPDATA%\WindowPilot\mini-toolbar.json`。可调整数组顺序或删除不需要的项目，下次收起面板时生效。展开和退出始终可用。

```json
["hide", "clickThrough", "topmost"]
```

隐藏使用系统的窗口隐藏操作，普通窗口从桌面及任务栏的运行窗口列表消失。已固定的应用启动入口、同应用其他窗口和应用自己创建的通知区图标属于不同对象。应用采用独立任务栏代理窗口时，显示行为取决于该应用。

## 构建

克隆此仓库或下载 Release 的源码包，在 Windows x64 上安装与 `global.json` 匹配的 .NET SDK，然后于项目目录运行

```powershell
.\build.ps1
```

输出在 `dist\WindowPilot-win-x64`，包含桌面程序、浏览器连接程序、注册和卸载脚本，以及可直接在 Chrome 或 Edge 加载的 `browser-extension` 目录。首次构建需要从 NuGet 还原依赖。SDK 和包版本由 `global.json` 与项目文件管理。构建后运行 `.\pack.ps1` 生成 Windows 程序包。传入 `-ExtensionArchivePath dist\sell-fish-browser-extension.zip` 可同时生成独立扩展包。`.\pack-source.ps1` 可另行生成源码包。

## 验证

在带 Explorer 任务栏的 Windows 桌面上，依次运行以下测试。它们创建自己的测试窗口，结束后关闭。窗口置顶、点击与遮挡检查会短暂使用前台桌面。

```powershell
dotnet run --project .\tests\NativeTests.csproj -c Release
.\tests\Run-EffectsProbe.ps1
dotnet run --project .\tests\DetachedProbe\DetachedProbe.csproj -c Release
.\tests\BrowserBridge\Run.ps1
```

- **窗口与滤镜**分别检查隐藏、任务栏、状态恢复，以及桌面显示颜色、鼠标命中和遮挡。[窗口测试说明](tests/README-native.md) · [滤镜测试说明](tests/EffectsProbe/README.md)
- **分离浮窗**使用独立进程的标准 Windows 控件，验证中文写回、冲突保留、密码拒绝、独立关闭、流式文本跟随、向上滚动和前台焦点。结果位于 `%TEMP%\sell-fish-detached-probe.txt`。
- **浏览器连接**检查原生消息来源、配对、命令及断线处理。网页适配器和双浮窗另有本地测试，在实际浏览器中使用可控对话页面。线上 ChatGPT 需要在独立测试对话中另外检查中文输入、发送和生成结果。[浏览器测试说明](tests/browser/README.md) · [双浮窗测试说明](tests/BrowserWindows/README.md)

可选的面板和快捷键测试使用带 Tk 的 Windows Python、pywinauto 和 pywin32，在空闲的交互桌面上依次运行。

```powershell
python -X utf8 tests/ui_smoke.py "dist/WindowPilot-win-x64/sell fish.exe"
python -X utf8 tests/shortcut_smoke.py "dist/WindowPilot-win-x64/sell fish.exe"
python -X utf8 tests/advanced_ui_smoke.py "dist/WindowPilot-win-x64/sell fish.exe"
```

## 维护

| 文件 | 职责 |
| --- | --- |
| MainWindow.xaml / MainWindow.xaml.cs | 绑定、释放和界面交互；协调隐藏、置顶、透明度及滤镜的状态 |
| WindowService.cs | 枚举、核验及操作真实目标窗口，保存原始状态并执行恢复 |
| GlobalShortcut.cs | 两个快捷键框的录入、冲突检查、系统注册及保存 |
| CompactWindow.cs / MiniToolbar.cs | 双模式面板、固定尺寸、拖动、黑白主题和可配置工具栏 |
| Detached / MainWindow.Advanced.cs | Windows UI Automation 控件选择、浮窗和高级分页 |
| BrowserBridge / browser-extension | 浏览器配对、本地消息传输、对话状态和网页适配 |
| BackdropOpacityLayer.cs | 用独立的鼠标穿透图层混合实时背景，兼容自行重设透明度的目标 |
| EffectSession.cs / NativeEffectOverlay.cs | 捕获绑定窗口、执行 Win2D 滤镜并将结果显示在目标位置 |
| App.xaml.cs | 应用启动和同一会话内的单实例保护 |

设置和临时恢复记录位于 `%LOCALAPPDATA%\WindowPilot`。若控制面板意外终止，可重新打开同一程序，尝试恢复仍在运行的原窗口。对于权限高于控制面板的目标窗口，Windows 可能拒绝操作。

颜色效果依赖目标应用提供可捕获的画面，受保护内容和独立弹出窗口需要按应用验证。目标窗口的输入、桌面叠放顺序及恢复状态也是回归测试的一部分。对于特殊绘制、无法安全还原透明度的窗口，面板禁用相应视觉控件。

启用滤镜且窗口可见时，程序将处理后的画面叠放在目标位置。常规模式保留原窗口 1/255 的不透明度以接收鼠标输入，低不透明度时可能有极轻微的原色残留。稳定透明模式通过单独的背景层遮盖原色。当前滤镜有 GPU 到内存的图像复制，大尺寸窗口播放视频时的性能取决于分辨率与显卡。

官方参考：[Windows 应用 SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/release-channels)、[窗口显示与隐藏](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-showwindow)、[任务栏窗口按钮](https://learn.microsoft.com/windows/win32/shell/taskbar)、[窗口捕获](https://learn.microsoft.com/windows/apps/develop/media-authoring-processing/screen-capture)、[Win2D 效果](https://microsoft.github.io/Win2D/WinUI3/html/N_Microsoft_Graphics_Canvas_Effects.htm)。
