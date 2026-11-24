import http from "node:http";
import {readFileSync} from "node:fs";

const page = readFileSync(new URL("./fixture.html", import.meta.url));
const flag = process.argv.indexOf("--port");
const port = flag === -1 ? 17851 : Number(process.argv[flag + 1]);
http.createServer((req, res) => {
  res.writeHead(200, {"Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store"});
  res.end(page);
}).listen(port, "127.0.0.1", () => console.log(`fixture on http://127.0.0.1:${port}/`));
