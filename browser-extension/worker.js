const PAGE_ORIGIN = "https://chatgpt.com";
const HOST = "com.sellfish.bridge";
const sessions = new Map();

function indication(tabId, badge, title) {
  Promise.all([
    chrome.action.setBadgeText({tabId, text: badge}),
    chrome.action.setTitle({tabId, title}),
    chrome.action.setBadgeBackgroundColor({tabId, color: badge === "!" ? "#934738" : "#17675e"})
  ]).catch(() => {}); // The tab may already have closed.
}

function end(entry, message, notify = true) {
  if (sessions.get(entry.session) !== entry) return;
  sessions.delete(entry.session);
  clearTimeout(entry.timeout);
  if (notify) {
    try { entry.native.postMessage({type: "paused", session: entry.session, message}); } catch {}
  }
  entry.page?.disconnect();
  // Native Messaging ports deliver already queued messages before disconnect.
  entry.native.disconnect();
  if (![...sessions.values()].some(other => other.tabId === entry.tabId))
    indication(entry.tabId, message ? "!" : "", message || "连接当前 ChatGPT 对话到 sell fish");
}

async function startPage(entry) {
  if (entry.started) return;
  entry.started = true;
  clearTimeout(entry.timeout);
  try {
    await chrome.scripting.executeScript({
      target: {tabId: entry.tabId, documentIds: [entry.documentId]}, files: ["page.js"]
    });
    if (!sessions.has(entry.session)) return;
    const port = chrome.tabs.connect(entry.tabId, {
      name: "sell-fish-page", documentId: entry.documentId
    });
    entry.page = port;
    port.onMessage.addListener(message => {
      if (!sessions.has(entry.session) || message.session !== entry.session) return;
      if (message.type === "paused") { end(entry, message.message); return; }
      if (message.type === "state" || message.type === "result") {
        try { entry.native.postMessage(message); } catch { end(entry, "桌面程序已断开。", false); }
      }
    });
    port.onDisconnect.addListener(() => {
      void chrome.runtime.lastError;
      end(entry, "网页已刷新、关闭或卸载，请重新连接。");
    });
    port.postMessage({type: "attach", session: entry.session, expectedPath: entry.path});
    indication(entry.tabId, "ON", "当前 ChatGPT 对话已连接到 sell fish");
  } catch {
    end(entry, "原标签页已改变，或扩展无权访问当前文档。请回到原对话重新连接。");
  }
}

chrome.action.onClicked.addListener(async tab => {
  if (!Number.isInteger(tab.id)) return;
  let entry;
  try {
    if (!tab.active || new URL(tab.url).origin !== PAGE_ORIGIN)
      throw new Error("请在要连接的 ChatGPT 对话页点击扩展。");
    const focused = await chrome.windows.getLastFocused();
    if (focused.id !== tab.windowId || !focused.focused)
      throw new Error("请先激活已绑定的浏览器窗口。");
    const [doc] = await chrome.scripting.executeScript({
      target: {tabId: tab.id, frameIds: [0]}, func: () => location.origin
    });
    if (!doc?.documentId || doc.result !== PAGE_ORIGIN)
      throw new Error("网页已改变，请重新点击扩展。");
    const browser = navigator.userAgent.includes("Edg/") ? "edge" : "chrome";
    const session = crypto.randomUUID();
    const native = chrome.runtime.connectNative(HOST);
    entry = {session, native, tabId: tab.id, documentId: doc.documentId, path: new URL(tab.url).pathname, page: null};
    sessions.set(session, entry);
    native.onMessage.addListener(message => {
      if (!sessions.has(session) || message.session !== session) return;
      if (message.type === "accept") { void startPage(entry); return; }
      if (message.type === "reject" || message.type === "disconnect") {
        end(entry, message.message || "", false);
        return;
      }
      if (entry.page && ["draft", "send", "stop"].includes(message.type)) {
        try { entry.page.postMessage(message); } catch { end(entry, "网页已断开。"); }
      }
    });
    native.onDisconnect.addListener(() => {
      const problem = chrome.runtime.lastError;
      end(entry, problem ? "无法连接本机 sell fish，请检查网页连接设置及主机注册。" : "本机连接已结束。", false);
    });
    entry.timeout = setTimeout(() => end(entry, "连接确认超时，请在进阶页重新连接。"), 15000);
    native.postMessage({type: "pair", session, browser, windowId: tab.windowId, tabId: tab.id, documentId: doc.documentId});
  } catch (error) {
    if (entry) end(entry, error.message);
    else indication(tab.id, "!", error.message || "无法连接当前标签页。");
  }
});

chrome.tabs.onRemoved.addListener(tabId => {
  for (const entry of sessions.values()) if (entry.tabId === tabId) end(entry, "原标签页已关闭。");
});
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (!change.discarded && !(change.url && !change.url.startsWith(PAGE_ORIGIN + "/"))) return;
  for (const entry of sessions.values()) if (entry.tabId === tabId) end(entry, "原网页已离开或被浏览器卸载。");
});
