// A FAKE Gateway for the Boxes screen's QA proof (Factory Boxes mission, phase 1). It answers the three Boxes calls in
// the contract's exact shapes, and just enough of the rest that the Cockpit shell draws its frame. It is a proof
// fixture, never product code. Run:  node fake-gateway.mjs <port>
//
// What it answers is chosen per run by POST /__qa/mode {"flag":true|false,"list":"two"|"empty"|"error","ticket":"ok"|"error"},
// and the screenshot driver (shoot.mjs) sets it before each page load.
import http from "node:http";

const port = Number(process.argv[2] ?? 7979);
let mode = { flag: true, list: "two", ticket: "ok" };

const TWO = {
  count: 2,
  boxes: [
    {
      boxId: "demo-desktop-soren",
      displayName: "Soren's demo desktop",
      directorId: "4f6c2a3e-0000-0000-0000-000000000000",
      directorName: "devlinux slot 1",
      machineName: "devlinux",
      state: "Online",
      relativeDesktopPath: "/boxes/demo-desktop-soren/desktop/",
    },
    {
      boxId: "research-box-2",
      displayName: "Research desktop",
      directorId: "9a9a9a9a-0000-0000-0000-000000000000",
      directorName: "",
      machineName: "devlinux",
      state: "Online",
      relativeDesktopPath: "/boxes/research-box-2/desktop/",
    },
  ],
};

function json(res, status, body) {
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8" });
  res.end(body === undefined ? "" : JSON.stringify(body));
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url ?? "/", "http://fake.invalid");
  const path = url.pathname;
  let body = "";
  req.on("data", (chunk) => (body += chunk));
  req.on("end", () => {
    if (req.method === "POST" && path === "/__qa/mode") {
      mode = { ...mode, ...JSON.parse(body || "{}") };
      return json(res, 200, mode);
    }
    if (path === "/account/status") {
      return json(res, 200, { signedIn: true, email: "qa@example.test", provider: "fake", staff: false, gatewaySignIn: false, boxes: mode.flag });
    }
    if (req.method === "GET" && path === "/boxes") {
      if (!mode.flag) return json(res, 404);
      if (mode.list === "empty") return json(res, 200, { count: 0, boxes: [] });
      if (mode.list === "error") return json(res, 503, { error: "The Gateway is starting up and cannot list boxes yet." });
      return json(res, 200, TWO);
    }
    const ticket = /^\/boxes\/([^/]+)\/desktop-ticket$/.exec(path);
    if (req.method === "POST" && ticket) {
      if (mode.ticket === "error") return json(res, 404, { error: "No connected Director in your account hosts a desktop for that box." });
      return json(res, 200, { url: `http://127.0.0.1:${port}/__qa/desktop/${ticket[1]}?ticket=fake`, expiresAtUtc: new Date(Date.now() + 60_000).toISOString() });
    }
    if (path.startsWith("/__qa/desktop/")) {
      res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
      return res.end(`<!doctype html><title>fake desktop</title><body style="background:#123;color:#fff;font:20px sans-serif;padding:40px">The fake desktop for ${path.split("/")[3]}</body>`);
    }
    // The shell's other reads: an empty roster and nothing else, so the frame draws without inventing data.
    if (path === "/sessions") return json(res, 200, { sessions: [], directors: [] });
    if (path === "/client-errors") return json(res, 202, {});
    return json(res, 404);
  });
});

server.listen(port, "127.0.0.1", () => console.log(`[fake-gateway] listening on ${port}`));
