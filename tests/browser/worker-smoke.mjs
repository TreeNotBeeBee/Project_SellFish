import assert from "node:assert/strict";
import {readFile} from "node:fs/promises";
import vm from "node:vm";
import {randomUUID} from "node:crypto";
import {setTimeout as delay} from "node:timers/promises";

function event() {
  const listeners = [];
  return {addListener(fn) {listeners.push(fn);}, async fire(...args) {
    for (const fn of listeners) await fn(...args);
  }};
}
function port() {
  return {sent:[], onMessage:event(), onDisconnect:event(), closed:false,
    postMessage(message) {this.sent.push(message);}, disconnect() {this.closed = true;}};
}
const native = [], pages = [], scripts = [], badges = [];
const action = {onClicked:event(), setBadgeText:async val => badges.push(val),
  setTitle:async () => {}, setBadgeBackgroundColor:async () => {}};
const tabs = {onRemoved:event(), onUpdated:event(), connect(id, options) {
  const page = port();
  pages.push({id,options,page});
  return page;
}};
const api = {action,tabs,windows:{getLastFocused:async () => ({id:23,focused:true})},
  scripting:{executeScript:async target => {
    scripts.push(target);
    return [{documentId:"doc-2",result:"https://chatgpt.com"}];
  }},
  runtime:{connectNative(name) {
    assert.equal(name,"com.sellfish.bridge");
    const connection = port();
    native.push(connection);
    return connection;
  }}};
const source = await readFile(new URL("../../browser-extension/worker.js",import.meta.url),"utf8");
vm.runInNewContext(source,{chrome:api,navigator:{userAgent:"Chrome/147 Edg/147"},URL,crypto:{randomUUID},setTimeout,clearTimeout});
const target = {id:47,windowId:23,active:true,url:"https://chatgpt.com/c/0123456789"};
await action.onClicked.fire({...target,url:"https://example.com/"});
await action.onClicked.fire({...target,windowId:22});
assert.equal(native.length,0);
assert.equal(scripts.length,0);
await action.onClicked.fire(target);
const request = native[0].sent[0];
assert.equal(request.browser,"edge");
assert.equal(request.documentId,"doc-2");
assert.equal(request.tabId,47);
assert.equal(request.type,"pair");
assert.equal(pages.length,0); // No content is read before desktop accepts.
await native[0].onMessage.fire({type:"accept",session:"wrong"});
assert.equal(pages.length,0);
await native[0].onMessage.fire({type:"accept",session:request.session});
await delay(0);
const link = pages[0];
assert.equal(link.id,47);
assert.equal(link.options.documentId,"doc-2");
assert.equal(link.page.sent[0].type,"attach");
await tabs.onUpdated.fire(47,{status:"complete"});
await tabs.onUpdated.fire(48,{discarded:true});
assert.equal(link.page.closed,false);
await link.page.onMessage.fire({type:"state",session:"wrong"});
assert.equal(native[0].sent.length,1);
await link.page.onMessage.fire({type:"state",session:request.session,draft:"local"});
assert.equal(native[0].sent[1].draft,"local");
await native[0].onMessage.fire({type:"send",session:request.session,id:"7",text:"one"});
assert.equal(link.page.sent[1].id,"7");
await tabs.onUpdated.fire(47,{discarded:true});
assert.equal(link.page.closed,true);
assert.equal(native[0].closed,true);
assert.equal(native[0].sent.at(-1).type,"paused");
await action.onClicked.fire(target);
await native[1].onMessage.fire({type:"reject",session:native[1].sent[0].session,message:"wrong window"});
assert.equal(pages.length,1);
assert.equal(native[1].closed,true);
console.log("PASS: explicit foreground pairing, origin/session/document isolation, reject, discard, commands, state forwarding");
