# 网页分离回归测试

在 Windows x64、已安装 Chrome 和 Edge 的机器上运行。测试使用独立的临时浏览器配置。夹具模拟可编辑对话、流式回答和切换会话，断言检查实际 DOM 及编辑器模型。

启动只监听本机的夹具，在项目目录运行

~~~powershell
node tests/browser/fixture-server.mjs --port 17851
~~~

在另一个终端安装测试依赖并运行

~~~powershell
$deps = Join-Path $env:TEMP "sell-fish-browser-tests"
npm install --prefix $deps --no-audit --no-fund playwright-core esbuild prosemirror-view prosemirror-state prosemirror-schema-basic
$env:SELL_FISH_PLAYWRIGHT = Join-Path $deps "node_modules/playwright-core"
node tests/browser/worker-smoke.mjs
node tests/browser/adapter-smoke.mjs
node tests/browser/rich-edit-smoke.mjs
~~~

第一项测试配对、授权、标签及文档边界和卸载。第二项在实际 Chrome 和 Edge 中测试标签切换、最小化、中文多行草稿、输入法组词期间拒绝发送、冲突、重复指令、流式文本及消息提交。第三项使用真实 ProseMirror 数据模型检查多行文本、空行、替换和清空。

另有 MV3 打包测试，检查固定扩展 ID、实际服务工作线程、内容脚本及指定文档的消息端口。安装与 Playwright 匹配的 Chromium 后运行。也可以将 `SELL_FISH_TESTING_CHROMIUM` 设为支持加载解压扩展的 Chromium 测试可执行文件路径。

~~~powershell
& (Join-Path $deps "node_modules/.bin/playwright-core.cmd") install chromium
node tests/browser/mv3-smoke.mjs
~~~

上述测试均在本机夹具上完成。线上验收需要在一次性 ChatGPT 会话中另外确认网站接受草稿和消息，原对话只新增一条用户消息、回答窗持续更新，并能在另一个标签或窗口保持当前焦点。
