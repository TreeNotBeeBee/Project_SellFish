# 窗口轻控 · WindowPilot

Windows 桌面小工具。下拉列表选定一个窗口后，所有操作都固定作用于它，直到释放绑定。窗口标题变化或用户切换到别的应用都不会改变目标。

## 使用

解压完整的 Windows x64 发行包，运行其中的 **WindowPilot.exe**。控制面板可以最小化，快捷键继续工作。

1. 从第一行下拉框选定窗口。**始终置顶** 控制目标的叠放顺序，**主题** 可切换并记住控制面板的黑色或白色外观。拖动面板空白处可以移动面板，右击空白处可最小化或退出。
2. 勾选 **隐藏**，或按旁边显示的全局快捷键（初始为 **Ctrl + Shift + H**）。再次按同一组合即可显示。**页面穿透**（初始为 **Ctrl + Shift + P**）让鼠标直接操作下方的窗口，再按一次恢复目标的鼠标交互。
3. 点击任一快捷键输入框，再按下并松开想使用的组合键，就会自动保存。按 Esc 或离开空输入框可取消录入。旧版的隐藏快捷键设置会自动沿用。
4. 在 **不透明度** 框中输入 0 至 100 的整数。稍停、按 Enter 或离开输入框即可生效。旁边的小按钮可在 0% 与上一次数值之间切换。工具在绑定期间会检查目标自行重置的窗口透明度。
5. **反色** 和 **灰度** 可同时开启。隐藏时保留选项，再次显示时恢复组合效果。
6. 在绑定下拉框中选择 **释放并恢复窗口**，或正常退出控制面板，程序恢复绑定前的置顶、透明度、可见性及鼠标穿透状态。

隐藏使用系统的窗口隐藏操作，普通窗口从桌面及任务栏的运行窗口列表消失。已固定的应用启动入口、同应用其他窗口和应用自己创建的通知区图标属于不同对象。应用采用独立任务栏代理窗口时，显示行为取决于该应用。

## 构建

构建和验证使用 **WindowPilot-source.zip** 中的源码。解压后，在 Windows x64 上安装与 `global.json` 匹配的 .NET SDK，于项目目录运行：

```powershell
.\build.ps1
```

输出在 `dist\WindowPilot-win-x64`。首次构建需要从 NuGet 还原依赖。SDK 和包版本由 `global.json` 与项目文件管理。

## 验证

在带 Explorer 任务栏的交互式 Windows 桌面上分别运行以下两项。它们创建自己的测试窗口，结束后关闭。窗口置顶、点击与遮挡测试需要短暂使用前台桌面。

```powershell
dotnet run --project .\tests\NativeTests.csproj -c Release
.\tests\Run-EffectsProbe.ps1
```

第一项检查任务栏按钮、隐藏、置顶、快捷切换后的状态还原和异常恢复。第二项检查滤镜呈现的颜色、组合透明度、鼠标命中和窗口之间的遮挡。详见 [窗口测试说明](tests/README-native.md) 和 [滤镜测试说明](tests/EffectsProbe/README.md)。

可选的 `tests/ui_smoke.py` 从真实面板验证选择窗口、修改全局快捷键、冲突处理、完全透明、滤镜切换及退出。它使用带 Tk 的 Windows Python、pywinauto 和 pywin32，运行于独立测试桌面。完成构建后执行 `python -X utf8 tests/ui_smoke.py dist/WindowPilot-win-x64/WindowPilot.exe`。

## 维护

| 文件 | 职责 |
| --- | --- |
| MainWindow.xaml / MainWindow.xaml.cs | 绑定、释放和界面交互；协调隐藏、置顶、透明度及滤镜的状态 |
| WindowService.cs | 枚举、核验及操作真实目标窗口，保存原始状态并执行恢复 |
| GlobalShortcut.cs | 两个快捷键框的录入、冲突检查、系统注册及保存 |
| CompactWindow.cs | 面板拖动、大小、黑白主题和窗口菜单 |
| EffectSession.cs / NativeEffectOverlay.cs | 捕获绑定窗口、执行 Win2D 滤镜并将结果显示在目标位置 |
| App.xaml.cs | 应用启动和同一会话内的单实例保护 |

设置和临时恢复记录位于 `%LOCALAPPDATA%\WindowPilot`。若控制面板意外终止，可重新打开同一程序，尝试恢复仍在运行的原窗口。对于权限高于控制面板的目标窗口，Windows 可能拒绝操作。

颜色效果依赖目标应用提供可捕获的画面，受保护内容和独立弹出窗口需要按应用验证。目标窗口的输入、桌面叠放顺序及恢复状态也是回归测试的一部分。对于特殊绘制、无法安全还原透明度的窗口，面板禁用相应视觉控件。

启用滤镜且窗口可见时，程序保留原窗口 1/255 的不透明度以接收鼠标输入，再把处理后的画面叠放在其上方。低不透明度时可能有极轻微的原色残留。调到 0% 时，原窗口和滤镜画面都完全透明。当前渲染实现有 GPU 到内存的图像复制，大尺寸窗口播放视频时的性能取决于分辨率与显卡。

官方参考：[Windows 应用 SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/release-channels)、[窗口显示与隐藏](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-showwindow)、[任务栏窗口按钮](https://learn.microsoft.com/windows/win32/shell/taskbar)、[窗口捕获](https://learn.microsoft.com/windows/apps/develop/media-authoring-processing/screen-capture)、[Win2D 效果](https://microsoft.github.io/Win2D/WinUI3/html/N_Microsoft_Graphics_Canvas_Effects.htm)。
