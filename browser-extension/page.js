// Injected into the isolated extension world of exactly one user-selected document.
(() => {
  const PAGE_ORIGIN = "https://chatgpt.com";
  if (location.origin !== PAGE_ORIGIN || window !== top || globalThis.sellFishPageInstalled) return;
  globalThis.sellFishPageInstalled = true;
  const encoder = new TextEncoder();
  const clean = text => text.replace(/\r\n?/g, "\n");
  const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
  const visible = el => el && !el.hidden && el.getClientRects().length > 0;
  const enabled = el => visible(el) && !el.disabled && el.getAttribute("aria-disabled") !== "true";

  function route() {
    if (location.origin !== PAGE_ORIGIN) return null;
    const path = location.pathname.replace(/\/$/, "");
    const existing = path.match(/^(?:\/g\/[^/]+)?\/c\/([a-z\d-]{8,128})$/i);
    if (existing) return {key: "c/" + existing[1], path};
    if (path === "" || /^\/g\/[^/]+$/.test(path)) return {key: "new", path};
    return null;
  }

  function editorElement() {
    return [...document.querySelectorAll('#prompt-textarea, [data-testid="prompt-textarea"], form[data-type="unified-composer"] [contenteditable="true"]')]
      .find(el => visible(el) && (el instanceof HTMLTextAreaElement || el.isContentEditable));
  }
  function actionButton(id) {
    return [...document.querySelectorAll('button[data-testid="' + id + '"]')].find(visible);
  }
  function editorText(editor) {
    if (editor instanceof HTMLTextAreaElement) return clean(editor.value);
    // innerText inserts extra separators between CSS paragraphs. Walk the editing
    // document so that blank lines and ProseMirror's visual trailing BR round-trip.
    function line(node) {
      if (node.nodeType === Node.TEXT_NODE) return node.data;
      if (node.nodeName === "BR") return node.classList.contains("ProseMirror-trailingBreak") ? "" : "\n";
      let text = "";
      const children = [...node.childNodes];
      if (children.length === 1 && children[0].nodeName === "BR") return "";
      children.forEach((child, index) => {
        if (index && /^(DIV|P)$/.test(child.nodeName)) text += "\n";
        text += line(child);
      });
      return text;
    }
    return clean(line(editor));
  }

  function enterText(editor, value) {
    if (editor instanceof HTMLTextAreaElement) {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(editor, value);
      editor.dispatchEvent(new InputEvent("input", {bubbles: true, inputType: "insertText", data: value}));
      return;
    }
    const previous = document.activeElement;
    const selection = document.getSelection();
    const saved = selection.rangeCount && previous !== editor ? selection.getRangeAt(0).cloneRange() : null;
    try {
      editor.focus({preventScroll: true});
      const range = document.createRange();
      range.selectNodeContents(editor);
      selection.removeAllRanges();
      selection.addRange(range);
      // Use the browser's editing operation so rich editors receive their normal
      // input transaction. No reads or writes to the system clipboard.
      if (!document.execCommand(value ? "insertText" : "delete", false, value) && editorText(editor) !== value) {
        const data = new DataTransfer();
        data.setData("text/plain", value);
        editor.dispatchEvent(new ClipboardEvent("paste", {bubbles: true, cancelable: true, clipboardData: data}));
      }
    } finally {
      if (previous !== editor && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus({preventScroll: true});
        if (saved && saved.commonAncestorContainer.isConnected) {
          selection.removeAllRanges();
          selection.addRange(saved);
        }
      }
    }
  }

  function answerBlocks() {
    const groups = [...document.querySelectorAll('[data-message-author-role="assistant"]')];
    const result = [];
    let budget = 220000;
    let shortened = false;
    for (let index = groups.length - 1; index >= 0 && !shortened; index--) {
      const group = groups[index];
      const roots = [...group.querySelectorAll(".markdown")];
      if (!roots.length) roots.push(group);
      const groupBlocks = [];
      for (const root of roots) {
        const selector = "p,pre,ul,ol,h1,h2,h3,h4,h5,h6,blockquote,table";
        const elements = [...root.querySelectorAll(selector)].filter(el => !root.contains(el.parentElement?.closest(selector)));
        for (const el of elements.length ? elements : [root]) {
          let kind = "paragraph", text;
          if (el.matches("pre")) { kind = "code"; text = (el.querySelector("code") || el).textContent; }
          else if (el.matches("ul,ol")) {
            kind = "list";
            text = [...el.children].filter(li => li.tagName === "LI").map((li, i) =>
              (el.tagName === "OL" ? (Number(el.getAttribute("start") || 1) + i) + ". " : "• ") + li.innerText.trim()).join("\n");
          } else { text = el.innerText.trim(); }
          if (text) groupBlocks.push({kind, text});
        }
      }
      for (let i = groupBlocks.length - 1; i >= 0; i--) {
        const block = groupBlocks[i];
        const cost = encoder.encode(JSON.stringify(block)).length + 1;
        if (cost > budget || result.length >= 1800) {
          if (budget > 3000 && result.length < 1800)
            result.unshift({kind: block.kind, text: block.text.slice(-Math.floor((budget - 100) / 6))});
          shortened = true;
          break;
        }
        result.unshift(block);
        budget -= cost;
      }
    }
    if (shortened) result.unshift({kind: "paragraph", text: "较早的内容请在原网页查看。"});
    return result;
  }

  function userTurns() { return [...document.querySelectorAll('[data-message-author-role="user"]')]; }
  const userText = el => clean((el.querySelector(".whitespace-pre-wrap") || el).innerText).trim();
  function attach(port, session, expectedPath) {
    let pinned = route();
    let closed = false, composing = false, sending = false, uncertain = false;
    let adoption = null, timer = 0, last = "", queue = Promise.resolve(), queued = 0;
    let witness = null;
    const completed = new Map();
    const submitted = new Set();
    const listeners = new AbortController();
    const observer = new MutationObserver(schedule);
    function finish() {
      closed = true;
      clearTimeout(timer);
      listeners.abort();
      observer.disconnect();
    }
    function emit(payload) {
      if (closed) return;
      try { port.postMessage({...payload, session}); } catch { finish(); }
    }
    function pause(message) {
      emit({type: "paused", message});
      finish();
    }
    function sameConversation() {
      if (closed) return false;
      const current = route();
      if (pinned && current?.key === pinned.key && current.path === pinned.path) {
        // Temporary chats sometimes retain the blank route. A replaced transcript must
        // not inherit the editor. Prefer a message ID so ordinary DOM rerenders work.
        if (pinned.key === "new") {
          const turns = userTurns();
          if (witness && !turns.some(el => el === witness.node ||
              (witness.id && el.dataset.messageId === witness.id))) {
            pause("临时对话已更换，请重新连接。");
            return false;
          }
          const lastTurn = turns.at(-1);
          if (lastTurn) witness = {node:lastTurn, id:lastTurn.dataset.messageId};
        }
        return true;
      }
      // The website assigns a canonical URL to a new conversation after first send.
      // Only our witnessed first user turn can authorize that *one* transition.
      if (pinned?.key === "new" && current?.key.startsWith("c/") && adoption && Date.now() < adoption.expires) {
        const turns = userTurns();
        if (turns.length === 1 && userText(turns[0]) === adoption.text &&
            (!adoption.messageId || adoption.messageId === turns[0].dataset.messageId)) {
          pinned = current;
          adoption = null;
          return true;
        }
        // Router may update URL before rendering the new turn. No writes while pending.
        if (turns.length === 0) return false;
      }
      pause("已经离开最初连接的对话，请在目标对话中重新连接。");
      return false;
    }
    function refresh() {
      timer = 0;
      if (!sameConversation()) return;
      const editor = editorElement();
      if (!editor) { pause("网页没有可用的聊天输入框，请等待页面就绪后重新连接。"); return; }
      const draft = editorText(editor);
      if (encoder.encode(draft).length > 65536) { pause("网页草稿过长，请在原网页继续编辑。"); return; }
      const message = {type: "state", conversation: pinned.key, draft, blocks: answerBlocks(),
        canSend: !sending && !uncertain && !composing && !!enabled(actionButton("send-button")),
        generating: !!actionButton("stop-button")};
      const json = JSON.stringify(message);
      if (json !== last) { last = json; emit(message); }
    }
    function schedule() {
      if (!closed && !timer) timer = setTimeout(refresh, 85);
    }
    function check() {
      if (!sameConversation()) throw new Error("对话正在切换，操作已取消。");
      if (composing) throw new Error("请先结束原网页正在进行的输入法组词。");
      const editor = editorElement();
      if (!editor) throw new Error("原输入框已不可用，请重新连接。");
      return editor;
    }
    async function execute(command) {
      if (command.type === "stop") {
        check();
        const stop = actionButton("stop-button");
        if (!enabled(stop)) throw new Error("当前没有可停止的回答。");
        stop.click();
        return;
      }
      const editor = check();
      const text = command.text;
      if (typeof command.expected !== "string" || typeof text !== "string" || encoder.encode(text).length > 65536)
        throw new Error("草稿格式或长度超出支持范围。");
      const current = editorText(editor);
      if (current !== command.expected && current !== text)
        throw new Error("原网页的草稿已改变，请核对后重试。");
      if (command.type === "send" && (uncertain || !text.trim() || actionButton("stop-button")))
        throw new Error(uncertain ? "上次发送结果尚未确认，请查看原网页再重新连接。" : "当前无法发送。");
      if (current !== text) {
        enterText(editor, text);
        await sleep(100);
        if (editorText(check()) !== text) throw new Error("原网页未接受这次编辑，请查看原输入框。");
      }
      if (command.type === "draft") return;
      const button = actionButton("send-button");
      if (!enabled(button) || editorText(check()) !== text) throw new Error("原网页尚未允许发送，草稿已保留。");
      const before = userTurns();
      const oldIds = new Set(before.map(el => el.dataset.messageId).filter(Boolean));
      sending = true;
      if (pinned.key === "new" && !before.length)
        adoption = {text: text.trim(), expires: Date.now() + 30000};
      try {
        submitted.add(command.id);
        button.click(); // The sole submit attempt. Every further step only observes the page.
        const until = Date.now() + 12000;
        while (!closed && Date.now() < until) {
          const valid = sameConversation();
          if (valid) {
            const turns = userTurns();
            const match = turns.at(-1);
            const fresh = match && !before.includes(match) &&
              (match.dataset.messageId ? !oldIds.has(match.dataset.messageId) : turns.length > before.length);
            if (fresh && userText(match) === text.trim()) {
              if (adoption) adoption.messageId = match.dataset.messageId;
              return;
            }
          }
          await sleep(100);
        }
        uncertain = true;
        throw new Error("网页尚未确认这条消息。请查看原对话；程序不会再次发送。");
      } finally { sending = false; }
    }
    function command(message) {
      if (closed || message.session !== session || !["draft", "send", "stop"].includes(message.type) ||
          typeof message.id !== "string" || !message.id || message.id.length > 100) return;
      if (queued >= 24) {
        emit({type:"result", id: message.id, ok:false, message:"操作排队中，请稍后再试。"});
        return;
      }
      queued++;
      queue = queue.then(async () => {
        if (closed) return;
        if (completed.has(message.id)) { emit(completed.get(message.id)); return; }
        if (submitted.has(message.id)) {
          emit({type: "result", id: message.id, ok: false, message: "本次发送已经执行，请查看原对话。"});
          return;
        }
        let ok = true, detail = "";
        try { await execute(message); } catch (error) { ok = false; detail = error.message; }
        const reply = {type: "result", id: message.id, ok, message: detail};
        completed.set(message.id, reply);
        if (completed.size > 256) completed.delete(completed.keys().next().value);
        refresh();
        emit(reply);
      }).finally(() => { queued--; });
    }
    port.onMessage.addListener(command);
    port.onDisconnect.addListener(finish);
    if (location.pathname !== expectedPath || !pinned || !editorElement()) {
      pause("请在要连接的对话中等待输入框就绪，再点击扩展。");
      return;
    }
    const options = {capture: true, signal: listeners.signal};
    document.addEventListener("input", schedule, options);
    document.addEventListener("compositionstart", event => {
      if (editorElement()?.contains(event.target)) { composing = true; schedule(); }
    }, options);
    document.addEventListener("compositionend", () => { composing = false; schedule(); }, options);
    document.addEventListener("click", event => {
      if (!event.isTrusted) return;
      const link = event.target.closest?.('a[href]');
      if (link) adoption = null;
      if (pinned?.key === "new" && (event.target.closest?.('[data-testid="create-new-chat-button"]') ||
          (link && link.origin === location.origin && link.pathname === "/" && link.target !== "_blank")))
        pause("已选择另一段对话，请重新连接。");
    }, options);
    window.addEventListener("popstate", () => { adoption = null; schedule(); }, options);
    window.navigation?.addEventListener("currententrychange", schedule, options);
    window.addEventListener("pagehide", () => pause("网页已刷新或离开，请重新连接。"), options);
    document.addEventListener("freeze", () => pause("浏览器已冻结原标签，请重新连接。"), options);
    observer.observe(document.body, {subtree:true, childList:true, characterData:true, attributes:true,
      attributeFilter:["disabled", "aria-disabled", "hidden", "data-testid", "contenteditable"]});
    refresh();
  }
  chrome.runtime.onConnect.addListener(port => {
    if (port.name !== "sell-fish-page" || port.sender?.id && port.sender.id !== chrome.runtime.id) return;
    const start = message => {
      if (message.type !== "attach" || typeof message.session !== "string") return;
      port.onMessage.removeListener(start);
      attach(port, message.session, message.expectedPath);
    };
    port.onMessage.addListener(start);
  });
})();
