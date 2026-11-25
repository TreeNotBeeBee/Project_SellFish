# Chromium 原生控件回归

这个测试在独立的 Windows 桌面和临时浏览器配置中运行真实的 Windows UI Automation、sell fish 输入浮窗和输出区域浮窗。网页夹具在回环地址提供 ProseMirror，包含多段文字、替换子树的流式输出、发送按钮和一个干扰按钮。它检查编辑器文档中的原始字符、提交次数和浮窗显示的结果。测试不需要浏览器扩展或远程调试端口。

在 Windows x64 上准备仓库指定的 .NET SDK、Python（`pywin32` 和 `pywinauto`）、Node.js 和 Chrome 或 Edge。从仓库根目录运行：

```powershell
dotnet build tests/DetachedProbe/DetachedProbe.csproj -c Release
$workDir = Join-Path $env:TEMP "sell-fish-browser-probe"
npm install --prefix $workDir --no-audit --no-fund esbuild prosemirror-view prosemirror-state prosemirror-schema-basic
$env:NODE_PATH = Join-Path $workDir "node_modules"
$bundle = Join-Path $workDir "editor.js"
& (Join-Path $workDir "node_modules/.bin/esbuild.cmd") tests/BrowserControlsProbe/editor.js --bundle "--outfile=$bundle"
$probe = (Get-ChildItem tests/DetachedProbe/bin/Release -Recurse -File -Filter DetachedProbe.exe | Select-Object -First 1).FullName
$chrome = "$env:ProgramFiles/Google/Chrome/Application/chrome.exe"
python -X utf8 tests/BrowserControlsProbe/run.py --browser $chrome --bundle $bundle --probe $probe
python -X utf8 tests/BrowserControlsProbe/run.py --browser $chrome --bundle $bundle --probe $probe --automatic
```

第一轮显式关联用户所选的发送按钮，第二轮让程序从原输入框所在的表单中识别。将 `--browser` 换成 Edge 可执行文件路径可运行同一组测试。用 `--baseline` 替换 `--probe $probe` 会输出未经 sell fish 处理的 UIA 值和网页文档值，可用于复核占位换行。生成的第三方 JavaScript 仅供测试使用。
