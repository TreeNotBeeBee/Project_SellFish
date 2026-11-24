import assert from "node:assert/strict";
import {setTimeout as delay} from "node:timers/promises";
import {openFixture} from "./adapter-driver.mjs";

const origin = process.env.SELL_FISH_FIXTURE_ORIGIN || "http://127.0.0.1:17851";
for (const product of ["chrome","edge"]) {
  const frames = [];
  const waitFor = async predicate => {
    const until = Date.now() + 18000;
    do {
      const frame = frames.findLast(predicate);
      if (frame) return frame;
      await delay(90);
    } while (Date.now() < until);
    throw Error("Timed out waiting for fixture frame: " + JSON.stringify(frames.slice(-3)));
  };
  const fixture = await openFixture({product,origin,session:"fixture",receive:frame => frames.push(frame)});
  try {
    await waitFor(f => f.type === "state" && f.conversation === "new");
    await fixture.background();
    const text = "输入法已完成\n第二行";
    await fixture.page.evaluate(() => document.getElementById("prompt-textarea")
      .dispatchEvent(new CompositionEvent("compositionstart", {bubbles:true})));
    await fixture.command({type:"send",id:"during-ime",expected:"",text:"尚未组词"});
    assert.equal((await waitFor(f => f.id === "during-ime")).ok,false);
    assert.equal(await fixture.page.locator("#send-count").textContent(),"0");
    await fixture.page.evaluate(() => document.getElementById("prompt-textarea")
      .dispatchEvent(new CompositionEvent("compositionend", {bubbles:true})));
    await fixture.command({type:"draft", id:"draft", expected:"", text});
    assert.equal((await waitFor(f => f.id === "draft")).ok,true);
    await waitFor(f => f.type === "state" && f.draft === text && f.canSend);
    await fixture.command({type:"draft",id:"conflict",expected:"过期草稿",text:"不能覆盖"});
    assert.equal((await waitFor(f => f.id === "conflict")).ok,false);
    await fixture.command({type:"send",id:"send-once",expected:text,text});
    await fixture.command({type:"send",id:"send-once",expected:text,text});
    assert.equal((await waitFor(f => f.id === "send-once")).ok,true);
    const state = await waitFor(f => f.type === "state" && !f.generating &&
      f.blocks.some(b => b.text.includes("流式完成")));
    assert.equal(state.conversation,"c/11111111-1111-4111-8111-111111111111");
    assert.deepEqual(state.blocks.map(b => b.kind),["paragraph","list","code"]);
    assert.match(state.blocks[1].text,/1\. 第一项/);
    assert.equal(state.blocks[2].text,"alpha\nbeta");
    assert.equal(await fixture.page.locator("#send-count").textContent(),"1");
    await fixture.page.evaluate(() => document.querySelector("#other").click());
    await waitFor(f => f.type === "paused");
    await fixture.command({type:"send",id:"wrong-chat",expected:"",text:"不应发送"});
    await delay(350);
    assert.equal(await fixture.page.locator("#send-count").textContent(),"1");
    console.log(product + " PASS: background/minimized, two-line draft, conflict, exactly one send, streaming, list/code, conversation isolation");
  } finally { await fixture.close(); }
}
