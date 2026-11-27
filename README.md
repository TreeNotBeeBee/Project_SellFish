# sell fish

**sell fish 是一款简洁的 Windows 窗口控制软件，目前仍在开发中。**

从下拉框选取窗口，或用鼠标直接点选后，就可以隐藏窗口和它的任务栏按钮、置顶、调节不透明度、反色、灰度或让鼠标穿透。还可以把窗口里可访问的文本或输入框放到独立浮窗中。所有功能都由 Windows 桌面程序提供。

## 使用

从 [GitHub Releases](https://github.com/TreeNotBeeBee/Project_SellFish/releases) 下载 Windows x64 压缩包。解压整个文件夹后运行 **sell fish.exe**。升级前先退出旧版，让它恢复已绑定的窗口。新版沿用原来的主题和快捷键设置。

![基础页，第一行选择窗口，第二行切换五种效果，第三行输入不透明度](Assets/BasicPanelPreview.png)

1. 在 **基础** 页打开下拉框选定窗口，或点 **点击选择**，再单击目标窗口。点在窗口里的按钮或输入框上也能选中整个窗口，这一击只用于选择。按 **Esc** 可取消。sell fish 会排除自己的面板、迷你工具栏和分离浮窗。绑定后，要换目标时先在下拉框里选 **释放并恢复窗口**。
2. 勾选 **隐藏**，或按隐藏快捷键（初始为 **Ctrl + Shift + H**）。再次按同一组合即可显示。**页面穿透**（初始为 **Ctrl + Shift + P**）让鼠标直接操作下方的窗口，再按一次恢复目标的鼠标交互。
3. 打开 **快捷键** 页，可以分别设置隐藏、页面穿透、增加和减少不透明度。点击对应的框，按下并松开想用的键即可保存。点框旁的 **⌫**，或在录入时单按 **Backspace**，即可清除这一项。下次启动时仍为空。**Esc** 取消本次录入。可以将 **左 Ctrl** 单独分配给隐藏，将 **右 Ctrl** 单独分配给穿透。单个修饰键在松开时触发，与其他键或鼠标一起使用会取消本次单键动作。新录入的 Ctrl、Alt、Shift、Win 组合区分左右，旧配置继续响应两侧。
4. 在基础页第三行的 **不透明度** 框输入 0 至 100 的整数，稍停、按 Enter 或离开输入框即可生效。输入 **0** 可让目标完全透明，输入 **100** 可恢复全不透明。也可以为增加和减少分别设置全局快捷键，每按一次调整 **10 个百分点**。两项快捷键初始均为空。对钉钉等会自行重设透明度的窗口，程序可用独立图层实时混合目标后面的画面。
5. **反色** 和 **灰度** 可同时开启。隐藏时保留选项，再次显示时恢复组合效果。
6. 拖动面板空白处可移动窗口，在 **设置 → 主题** 中可切换黑白外观。点右上角 **—** 收起为迷你工具栏。按住工具栏左端的标志拖动，点最右侧的展开按钮回到刚才的页面。主面板和迷你工具栏都可以拖到另一台显示器。拖到桌面边缘或任务栏时，窗口会停在当前屏幕的可用区域内。
7. 在绑定下拉框选择 **释放并恢复窗口**，或点击 **×** 退出，程序会恢复目标原来的置顶、透明度、可见性和鼠标穿透状态。

**找回面板。** sell fish 运行时，任务栏右下角的通知区域会出现鱼篓图标。点 **∧** 可以查看折叠的图标。单击鱼篓即可在鼠标所在的屏幕中央展开主面板，回到刚才的分页。再次运行 `sell fish.exe` 也能找回已经打开的面板。

### 分离 Windows 控件

在 **基础** 绑定目标，切到 **进阶**，点击 **选择控件**，再单击目标窗口里的文字或输入框。按 Esc 取消。可以从同一窗口选择多个控件，分别移动、复制和关闭它们。挑选时需要让控件露出来，并暂时关闭目标的鼠标穿透。

**输入与发送**　从「选择控件」点选输入框后，直接在浮窗打字。**Enter** 发送，**Shift+Enter** 换行，中文输入法选词时回车确认候选。也可以从右上角 **⋯** 菜单点「发送」。程序会先核对原文、写回草稿，再调用原窗口的发送按钮。如果提示找不到发送按钮，在 **⋯ → 选择发送按钮** 中点选原窗口里的按钮，再回浮窗按 Enter。普通文本框可从 **⋯ → 写回原控件** 保存编辑。两边同时修改时，先复制要保留的文字，再从菜单点「重新载入」。

**阅读网页回答**　在「进阶」点 **选择控件**，再点回答正文中的一行，浮窗会读取这段文字所在的区域。也可以直接用 **选择输出区域**。在浮窗的 **读取范围** 中，可以选择单篇回答、消息列表或更大的正文区域，范围取决于网页公开的结构。选定区域内追加段落或改写文字时，浮窗会更新。停在底部时会跟随新增内容，向上阅读或选中文字时保留阅读位置。

以上操作使用 Windows UI Automation。网页需要向系统公开可访问的正文、可写输入框及可调用的发送按钮。切换标签、关闭或刷新页面可能让原控件失效，这时需要重新点选。密码框不支持分离。释放绑定或退出面板会关闭整组浮窗。

从 1.3 首个预览版升级时，如果安装过实验性网页扩展，可在浏览器的扩展管理页移除 **sell fish · 网页控件分离**，然后在旧版解压目录运行 `.\Uninstall-BrowserBridge.ps1` 清理注册。新版解压到新文件夹即可运行。

### 工具窗口置顶

**设置 → sell fish 始终置顶** 同时控制主面板、迷你工具栏和所有分离浮窗，包括之后打开的浮窗，重启后仍生效。**基础 → 始终置顶** 单独控制绑定的目标。收起再展开面板会回到刚才的分页。

### 自定义迷你工具栏

在 **设置 → 工具栏样式** 中选择 **初始** 或 **输入法**。初始样式用鱼篓标志和功能图标。输入法样式使用状态栏图案，跟随面板的黑白主题。下面是输入法样式选满四项时的样子。

![输入法样式的深色工具栏](Assets/InputMethodPreview.png)

打开 **设置 → 工具栏功能**，按希望从左到右显示的顺序勾选 **始终置顶、反色、灰度、隐藏、页面穿透**，最多可选四项。取消勾选会移除该按钮，再次勾选会把它排到末尾。也可以全部取消，让工具栏只保留拖动标志和展开按钮。两种样式共用这份顺序，改动自动保存。输入法图标的悬停提示会显示实际分配的功能。

![工具栏功能子页面，已勾选四项，底部列出实际顺序](Assets/ToolbarActionsPreview.png)

新安装默认显示隐藏、页面穿透和始终置顶。升级会保留原有图标样式和功能顺序。以前使用经典样式的配置会转为「初始」，原列表超过四项时保留前四项有效功能。

隐藏使用系统的窗口隐藏操作，普通窗口从桌面及任务栏的运行窗口列表消失。已固定的应用启动入口、同应用其他窗口和应用自己创建的通知区图标属于不同对象。应用采用独立任务栏代理窗口时，显示行为取决于该应用。

## 构建

克隆此仓库或下载 Release 的源码包，在 Windows x64 上安装与 `global.json` 匹配的 .NET SDK，然后于项目目录运行

```powershell
.\build.ps1
```

输出在 `dist\WindowPilot-win-x64`，包含桌面程序和运行所需的库。首次构建需要从 NuGet 还原依赖。SDK 和包版本由 `global.json` 与项目文件管理。构建后运行 `.\pack.ps1` 生成 Windows 程序包，运行 `.\pack-source.ps1` 生成源码包。

## 验证

在带 Explorer 任务栏的 Windows 桌面上，依次运行以下测试。它们创建自己的测试窗口，结束后关闭。窗口置顶、点击与遮挡检查会短暂使用前台桌面。

```powershell
dotnet run --project .\tests\NativeTests.csproj -c Release
.\tests\Run-EffectsProbe.ps1
dotnet run --project .\tests\DetachedProbe\DetachedProbe.csproj -c Release
```

- **窗口与滤镜**分别检查隐藏、任务栏、状态恢复，以及桌面显示颜色、鼠标命中和遮挡。[窗口测试说明](tests/README-native.md) · [滤镜测试说明](tests/EffectsProbe/README.md)
- **分离浮窗**使用独立进程的标准 Windows 控件，验证中文写回、冲突保留、密码框保护、独立关闭和窗口置顶。结果位于 `%TEMP%\sell-fish-detached-probe.txt`。
- **网页输入和输出**使用真实 Chrome 或 Edge 打开本机 ProseMirror 测试页，核对写回的每个字符、页面实际收到的消息数，以及网页替换多段内容后浮窗的实时更新。[运行浏览器控件测试](tests/BrowserControlsProbe/README.md)

可选的面板和快捷键测试使用带 Tk 的 Windows Python、pywinauto 和 pywin32，在空闲的交互桌面上依次运行。

```powershell
python -X utf8 tests/ui_smoke.py "dist/WindowPilot-win-x64/sell fish.exe"
python -X utf8 tests/window_picker_smoke.py "dist/WindowPilot-win-x64/sell fish.exe"
python -X utf8 tests/shortcut_smoke.py "dist/WindowPilot-win-x64/sell fish.exe"
python -X utf8 tests/advanced_ui_smoke.py "dist/WindowPilot-win-x64/sell fish.exe"
python -X utf8 tests/mini_toolbar_smoke.py "dist/WindowPilot-win-x64/sell fish.exe" --drag
python -X utf8 tests/work_area_smoke.py "dist/WindowPilot-win-x64/sell fish.exe"
python -X utf8 tests/tray_smoke.py "dist/WindowPilot-win-x64/sell fish.exe" --shell-click
```

## 维护

| 文件 | 职责 |
| --- | --- |
| MainWindow.xaml / MainWindow.xaml.cs | 绑定、释放和界面交互；协调隐藏、置顶、透明度及滤镜的状态 |
| WindowService.cs | 枚举、核验及操作真实目标窗口，保存原始状态并执行恢复 |
| GlobalShortcut.cs | 隐藏、穿透和不透明度增减快捷键的录入、清除、冲突检查、系统注册及保存 |
| CompactWindow.cs / MiniToolbar.cs / ImeToolbar.xaml | 面板收起与展开、拖动、两种工具栏外观、按钮状态同步 |
| MiniToolbarSettings.cs | 工具栏样式、已勾选功能的顺序、旧版配置迁移 |
| WorkAreaBounds.cs | 读取各显示器的可用区域，计算面板可以移动到的边界 |
| NotificationAreaIcon.cs | 显示鱼篓托盘图标、响应找回面板操作、在资源管理器重启后重新注册图标 |
| Detached / MainWindow.Advanced.cs | Windows UI Automation 控件选择、浮窗和高级分页 |
| BackdropOpacityLayer.cs | 用独立的鼠标穿透图层混合实时背景，兼容自行重设透明度的目标 |
| EffectSession.cs / NativeEffectOverlay.cs | 捕获绑定窗口、执行 Win2D 滤镜并将结果显示在目标位置 |
| App.xaml.cs | 应用启动和同一会话内的单实例保护 |

设置和临时恢复记录位于 `%LOCALAPPDATA%\WindowPilot`。若控制面板意外终止，可重新打开同一程序，尝试恢复仍在运行的原窗口。对于权限高于控制面板的目标窗口，Windows 可能拒绝操作。

颜色效果依赖目标应用提供可捕获的画面，受保护内容和独立弹出窗口需要按应用验证。目标窗口的输入、桌面叠放顺序及恢复状态也是回归测试的一部分。对于特殊绘制、无法安全还原透明度的窗口，面板禁用相应视觉控件。

启用滤镜且窗口可见时，程序将处理后的画面叠放在目标位置。常规模式保留原窗口 1/255 的不透明度以接收鼠标输入，低不透明度时可能有极轻微的原色残留。稳定透明模式通过单独的背景层遮盖原色。当前滤镜有 GPU 到内存的图像复制，大尺寸窗口播放视频时的性能取决于分辨率与显卡。

官方参考：[Windows 应用 SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/release-channels)、[窗口显示与隐藏](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-showwindow)、[任务栏窗口按钮](https://learn.microsoft.com/windows/win32/shell/taskbar)、[窗口捕获](https://learn.microsoft.com/windows/apps/develop/media-authoring-processing/screen-capture)、[Win2D 效果](https://microsoft.github.io/Win2D/WinUI3/html/N_Microsoft_Graphics_Canvas_Effects.htm)。
