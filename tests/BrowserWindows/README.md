# 网页双浮窗集成测试

在已构建的 Windows x64 发行包旁运行此测试。需要本机安装 Edge、Chrome、Windows Python、Node.js，以及项目可访问的 `playwright-core`。可用环境变量 `SELL_FISH_PLAYWRIGHT` 指定其模块目录。建议使用可交互的空闲 Windows 桌面。

```powershell
$scratch = Join-Path $env:TEMP ('sell-fish-gui-test-' + [guid]::NewGuid())
.\tests\BrowserWindows\Prepare.ps1 -Release .\dist\WindowPilot-win-x64 -Scratch $scratch
python -X utf8 .\tests\BrowserWindows\exercise.py (Join-Path $scratch 'app\sell fish.exe')
```

工具使用独立配置、两个已安装浏览器各自的无头实例和仅监听本机的测试网页。网页具有独立编辑状态、一次发送计数和流式回答。桌面测试启动器复用正式面板、浮窗、连接器和浏览器消息协议，程序化输入两行中文。它验证后台页面、真实控件内容、置顶标志、焦点、独立关闭和释放。

浏览器工具栏点击、扩展消息端口和前台窗口配对在此测试中由夹具模拟。登录后的真实网站、人工输入法候选词和扩展安装可另行验收。运行 `tests/BrowserBridge/Run.ps1` 可单独检查正式连接器的来源验证、消息大小限制、发送去重及断线处理。
