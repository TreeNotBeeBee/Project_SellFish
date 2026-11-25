import {EditorState} from "prosemirror-state";
import {EditorView} from "prosemirror-view";
import {schema} from "prosemirror-schema-basic";

// A newly created EditorView renders an empty <p><br class="ProseMirror-trailingBreak"></p>.
// Read its document model separately from the browser's accessibility representation.
const editor = new EditorView(document.querySelector("#mount"), {
  state: EditorState.create({schema}),
  attributes: {role: "textbox", "aria-label": "Fixture draft", "aria-multiline": "true"},
  dispatchTransaction(transaction) {
    editor.updateState(editor.state.apply(transaction));
    report();
  }
});
let sent = [], decoys = 0;
let reports = Promise.resolve();
const draft = () => editor.state.doc.textBetween(0, editor.state.doc.content.size, "\n",
  node => node.type.name === "hard_break" ? "\n" : "");
function report() {
  const body = JSON.stringify({draft: draft(), html: editor.dom.innerHTML, sent, decoys});
  reports = reports.then(() => fetch("/state", {method: "POST", body}));
}
document.querySelector("#decoy").onclick = () => { decoys++; report(); };
document.querySelector("#send").onclick = () => {
  sent.push(draft());
  editor.updateState(EditorState.create({schema}));
  report();
  document.querySelector("#latest").replaceChildren(Object.assign(document.createElement("p"), {textContent:"Streaming token"}));
  setTimeout(() => {
    const article = document.createElement("article");
    article.id = "latest";
    article.setAttribute("aria-label", "Latest answer");
    for (const text of ["Updated third paragraph", "Streaming complete"]) {
      article.append(Object.assign(document.createElement("p"), {textContent:text}));
    }
    document.querySelector("#latest").replaceWith(article);
  }, 500);
};
report();
