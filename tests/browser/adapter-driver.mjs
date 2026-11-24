// Run the production DOM adapter in a retail browser against the disposable local
// ChatGPT-shaped fixture. Only the extension's MessagePort transport is simulated.
import {createRequire} from "node:module";
import {readFile} from "node:fs/promises";
import {resolve} from "node:path";
import {pathToFileURL} from "node:url";
import {createInterface} from "node:readline";
const require = createRequire(import.meta.url);
const {chromium} = require(process.env.SELL_FISH_PLAYWRIGHT || "playwright-core");
const products = {
  chrome: (process.env.ProgramFiles || "C:\\Program Files") + "\\Google\\Chrome\\Application\\chrome.exe",
  edge: (process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)") + "\\Microsoft\\Edge\\Application\\msedge.exe"
};

export async function openFixture({product, origin, session, receive, path = "/"}) {
  const url = new URL(origin);
  if (url.protocol !== "http:" || url.hostname !== "127.0.0.1" || url.origin !== origin ||
      !products[product] || !path.startsWith("/") || path.startsWith("//"))
    throw Error("Expected --browser=edge|chrome and --origin=http://127.0.0.1:PORT");
  const browser = await chromium.launch({executablePath:products[product], headless:true});
  try {
    const context = await browser.newContext();
    const page = await context.newPage();
    await page.goto(origin + path);
    await page.exposeFunction("receiveSellFishMessage", receive);
    await page.evaluate(() => {
      function listeners() {
        const list = new Set();
        return {addListener: fn => list.add(fn), removeListener: fn => list.delete(fn),
          dispatch: message => [...list].forEach(fn => fn(message))};
      }
      const onConnect = listeners();
      window.chrome ??= {};
      chrome.runtime = {onConnect, id:"fixture"};
      window.fixturePort = {sender:{id:"fixture"}, name:"sell-fish-page", onMessage:listeners(),
        onDisconnect:listeners(), postMessage: message => window.receiveSellFishMessage(message)};
    });
    const source = await readFile(new URL("../../browser-extension/page.js", import.meta.url), "utf8");
    await page.addScriptTag({content:source.replace('const PAGE_ORIGIN = "https://chatgpt.com";',
      "const PAGE_ORIGIN = " + JSON.stringify(origin) + ";")});
    await page.evaluate(session => {
      chrome.runtime.onConnect.dispatch(window.fixturePort);
      window.fixturePort.onMessage.dispatch({type:"attach", session, expectedPath: location.pathname});
    }, session);
    let other;
    async function background() {
      other ??= await context.newPage();
      await other.bringToFront();
      const cdp = await context.newCDPSession(page);
      try {
        const {windowId} = await cdp.send("Browser.getWindowForTarget");
        await cdp.send("Browser.setWindowBounds", {windowId,bounds:{windowState:"minimized"}});
      } finally { await cdp.detach(); }
    }
    async function command(message) {
      await page.evaluate(message => window.fixturePort.onMessage.dispatch(message), {...message, session});
    }
    return {browser, page, command, background, close: () => browser.close()};
  } catch (error) {
    await browser.close();
    throw error;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const args = Object.fromEntries(process.argv.slice(2).map(arg => {
    const split = arg.indexOf("=");
    return [arg.slice(2,split), arg.slice(split + 1)];
  }));
  const session = args.session || "fixture";
  const fixture = await openFixture({product:args.browser || "chrome",
    origin:args.origin || "http://127.0.0.1:17851", path:args.path || "/", session,
    receive:message => console.log(JSON.stringify(message))});
  try {
    for await (const line of createInterface({input:process.stdin})) {
      const command = JSON.parse(line);
      if (command.test === "close") break;
      if (command.test === "background") await fixture.background();
      else if (command.test === "count") console.log(JSON.stringify({test:"count",count:Number(await fixture.page.locator("#send-count").textContent())}));
      else await fixture.command(command);
    }
  } finally { await fixture.close(); }
}
