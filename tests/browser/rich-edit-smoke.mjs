// Optional regression against a real ProseMirror editor, including blank paragraphs.
import assert from "node:assert/strict";
import {createRequire} from "node:module";
import {dirname} from "node:path";
import {setTimeout as delay} from "node:timers/promises";
import {openFixture} from "./adapter-driver.mjs";
const require = createRequire(process.env.SELL_FISH_PLAYWRIGHT
  ? dirname(process.env.SELL_FISH_PLAYWRIGHT) + "/__test__.cjs" : import.meta.url);
const {build} = require("esbuild");
const bundle = await build({stdin:{contents:
  'import {EditorView} from "prosemirror-view"; import {EditorState} from "prosemirror-state"; import {schema} from "prosemirror-schema-basic";' +
  'document.getElementById("prompt-textarea").remove();window.testEditor=new EditorView(document.getElementById("composer"),' +
  '{attributes:{id:"prompt-textarea"},state:EditorState.create({schema})});',
  resolveDir:dirname(require.resolve("prosemirror-view"))},
  write:false,bundle:true,platform:"browser",format:"iife"});
for (const product of ["chrome","edge"]) {
  const frames = [];
  const fixture = await openFixture({product,origin:process.env.SELL_FISH_FIXTURE_ORIGIN || "http://127.0.0.1:17851",
    session:"rich",receive: f => frames.push(f)});
  try {
    await fixture.page.addScriptTag({content:bundle.outputFiles[0].text});
    await fixture.background();
    let expected = "";
    for (const [index,text] of ["你好\n第二行","开头\n\n结尾","直接替换",""].entries()) {
      const id = "rich-"+index;
      await fixture.command({type:"draft",id,expected,text});
      let reply;
      for (let i=0;i<120 && !reply;i++) {reply=frames.find(f=>f.id===id); await delay(80);}
      assert.equal(reply?.ok,true,reply?.message);
      assert.equal(await fixture.page.evaluate(()=>window.testEditor.state.doc.textBetween(0,window.testEditor.state.doc.content.size,"\n")),text);
      expected=text;
    }
    console.log(product+" PASS: actual ProseMirror state accepted insertion, replacement, blank line, clear in background");
  } finally { await fixture.close(); }
}
