// Chromium for Testing supports documented command-line loading of unpacked extensions.
// Exercise the real service-worker→isolated document port, without registering a native host.
import {createRequire} from "node:module";
import {mkdtemp, readFile, writeFile, mkdir, rm} from "node:fs/promises";
import {join,dirname,resolve,basename} from "node:path";
import {tmpdir} from "node:os";
import assert from "node:assert/strict";
const require = createRequire(import.meta.url);
const {chromium} = require(process.env.SELL_FISH_PLAYWRIGHT || "playwright-core");
const origin = process.env.SELL_FISH_FIXTURE_ORIGIN || "http://127.0.0.1:17851";
assert.equal(new URL(origin).hostname,"127.0.0.1");
const temp = await mkdtemp(join(tmpdir(),"sell-fish-mv3-"));
let context;
try {
  const ext = join(temp,"extension");
  await mkdir(ext);
  for (const file of ["worker.js","page.js","manifest.json"]) {
    let source = await readFile(new URL("../../browser-extension/" + file,import.meta.url),"utf8");
    if (file === "manifest.json") {
      const manifest = JSON.parse(source);
      manifest.host_permissions = ["http://127.0.0.1/*"];
      source = JSON.stringify(manifest);
    } else {
      source = source.replace('const PAGE_ORIGIN = "https://chatgpt.com";', "const PAGE_ORIGIN = " + JSON.stringify(origin) + ";");
    }
    await writeFile(join(ext,file),source);
  }
  context = await chromium.launchPersistentContext(join(temp,"profile"), {
    executablePath: process.env.SELL_FISH_TESTING_CHROMIUM,
    channel:"chromium", headless:true, args:["--disable-extensions-except=" + ext,"--load-extension=" + ext]
  });
  const worker = context.serviceWorkers()[0] || await context.waitForEvent("serviceworker");
  assert.equal(new URL(worker.url()).hostname,"kahblpkceponikaklinoblfmgdnahdfc");
  const page = await context.newPage();
  await page.goto(origin);
  const result = await worker.evaluate(async origin => {
    const [tab] = (await chrome.tabs.query({})).filter(t => t.url?.startsWith(origin + "/"));
    const [doc] = await chrome.scripting.executeScript({target:{tabId:tab.id,frameIds:[0]},func:() => location.origin});
    return new Promise((resolve,reject) => {
      const timer = setTimeout(() => reject(Error("No actual content port response")),8000);
      const session = crypto.randomUUID();
      const entry = {session,tabId:tab.id,path:"/",documentId:doc.documentId,native:{
        postMessage(frame) {
          clearTimeout(timer);
          resolve(frame);
        }, disconnect() {}
      }};
      sessions.set(session,entry);
      void startPage(entry);
    });
  },origin);
  assert.equal(result.type,"state");
  assert.equal(result.conversation,"new");
  assert.equal(result.draft,"");
  console.log("PASS: MV3 packaged identity, real service worker, exact document injection, isolated-world port and state");
} finally {
  await context?.close();
  assert.equal(dirname(resolve(temp)),resolve(tmpdir()));
  assert.match(basename(temp),/^sell-fish-mv3-[\w-]+$/);
  await rm(temp,{recursive:true,force:true,maxRetries:4,retryDelay:300});
}
