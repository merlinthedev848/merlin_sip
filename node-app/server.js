const http = require("http");
const fs = require("fs");
const path = require("path");
const net = require("net");
const dgram = require("dgram");
const os = require("os");
const crypto = require("crypto");

const root = path.join(__dirname, "public");
const state = {
  vendor: "yeastar-s100",
  extension: "1001",
  pbxHost: "",
  amiPort: 5038,
  username: "",
  password: "",
  sipHost: "",
  sipPort: 5060,
  sipDomain: "",
  sipTransport: "udp",
  sipRegistered: false,
  sipContact: "",
  dnd: false,
  forwarding: "",
  queuePaused: false,
  license: "Trial mode"
};

const features = [
  ["Voice calls", "SIP registration or PBX originate/click-to-call", "SIP"],
  ["Hold / resume", "SIP session hold and resume", "SIP"],
  ["Blind transfer", "SIP REFER or PBX feature code", "SIP"],
  ["Attended transfer", "Consult then complete or cancel", "SIP"],
  ["DTMF", "IVR, voicemail, and feature-code tones", "SIP"],
  ["Presence / BLF", "Yeastar API/CTI or Asterisk AMI device state", "PBX"],
  ["Queues", "Agent login, pause, waiting callers, summaries", "PBX"],
  ["Voicemail", "Unread count, playback, delete, callback", "PBX"],
  ["Recordings", "Search, playback, download, retention", "PBX"],
  ["Call history", "CDR, missed calls, dispositions", "PBX"],
  ["DND", "Feature code or PBX API toggle", "PBX"],
  ["Forwarding", "Set, clear, and sync forwarding rules", "PBX"],
  ["Call park", "Park slots and retrieve controls", "PBX"],
  ["Pickup groups", "Directed and group pickup", "PBX"],
  ["Contacts", "PBX directory, local contacts, CRM lookup", "PBX"],
  ["CRM screen pop", "Caller ID events to CRM search", "PBX"],
  ["Provisioning", "Managed profiles and deployment defaults", "Admin"],
  ["Mobile ready", "Shared API contract for future Android/iOS", "Roadmap"]
];

const extensions = [
  { number: "1001", name: "CK Media Services", department: "Sales", state: "Available" },
  { number: "1002", name: "Support Desk", department: "Support", state: "Ringing" },
  { number: "1003", name: "Accounts", department: "Finance", state: "Busy" },
  { number: "1004", name: "Warehouse", department: "Ops", state: "DND" }
];

const queues = [
  { name: "Sales", waiting: 2, agents: 6, paused: 1 },
  { name: "Support", waiting: 5, agents: 9, paused: 2 },
  { name: "Accounts", waiting: 0, agents: 3, paused: 0 }
];

const callHistory = [
  { id: "seed-1", direction: "outbound", number: "1002", name: "Support Desk", startedAt: new Date(Date.now() - 18 * 60000).toISOString(), duration: "02:14", result: "Answered" },
  { id: "seed-2", direction: "missed", number: "1004", name: "Warehouse", startedAt: new Date(Date.now() - 61 * 60000).toISOString(), duration: "00:00", result: "Missed" },
  { id: "seed-3", direction: "inbound", number: "1003", name: "Accounts", startedAt: new Date(Date.now() - 4 * 3600000).toISOString(), duration: "05:46", result: "Answered" }
];

let sipSocket;
let sipRegistrationTimer;

function sendJson(res, value) {
  res.writeHead(200, { "content-type": "application/json" });
  res.end(JSON.stringify(value));
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let body = "";
    req.on("data", chunk => {
      body += chunk;
      if (body.length > 1_000_000) req.destroy();
    });
    req.on("end", () => {
      try {
        resolve(body ? JSON.parse(body) : {});
      } catch (error) {
        reject(error);
      }
    });
  });
}

function serveStatic(req, res) {
  const requestPath = req.url === "/" ? "/index.html" : req.url;
  const filePath = path.normalize(path.join(root, requestPath));

  if (!filePath.startsWith(root)) {
    res.writeHead(403);
    res.end("Forbidden");
    return;
  }

  fs.readFile(filePath, (error, data) => {
    if (error) {
      res.writeHead(404);
      res.end("Not found");
      return;
    }

    const ext = path.extname(filePath);
    const type = {
      ".html": "text/html",
      ".css": "text/css",
      ".js": "text/javascript",
      ".json": "application/json"
    }[ext] || "application/octet-stream";

    res.writeHead(200, { "content-type": type });
    res.end(data);
  });
}

function testTcp(host, port, timeoutMs = 1800) {
  return new Promise(resolve => {
    const socket = net.createConnection({ host, port });
    const done = result => {
      socket.destroy();
      resolve(result);
    };

    socket.setTimeout(timeoutMs);
    socket.on("connect", () => done({ ok: true, message: `Connected to ${host}:${port}` }));
    socket.on("timeout", () => done({ ok: false, message: `Timed out connecting to ${host}:${port}` }));
    socket.on("error", error => done({ ok: false, message: error.message }));
  });
}

function amiAction(config, actions, timeoutMs = 3500) {
  const host = config.host || state.pbxHost;
  const port = Number(config.port || state.amiPort || 5038);
  const username = config.username || state.username;
  const password = config.password || state.password;

  if (!host || !username || !password) {
    return Promise.resolve({
      ok: false,
      message: "FreePBX AMI host, username, and password are required."
    });
  }

  return new Promise(resolve => {
    const socket = net.createConnection({ host, port });
    let buffer = "";
    let finished = false;

    const finish = result => {
      if (finished) return;
      finished = true;
      socket.destroy();
      resolve(result);
    };

    socket.setTimeout(timeoutMs);
    socket.on("connect", () => {
      socket.write([
        "Action: Login",
        `Username: ${username}`,
        `Secret: ${password}`,
        "Events: off",
        "",
        ""
      ].join("\r\n"));

      for (const action of actions) {
        socket.write(action.concat(["", ""]).join("\r\n"));
      }

      socket.write(["Action: Logoff", "", ""].join("\r\n"));
    });

    socket.on("data", chunk => {
      buffer += chunk.toString("utf8");
      if (buffer.includes("Response: Error")) {
        finish({ ok: false, message: compactAmiMessage(buffer), raw: buffer });
      }
      if (buffer.includes("Message: Thanks for all the fish")) {
        finish({ ok: true, message: "FreePBX AMI accepted the command.", raw: buffer });
      }
    });

    socket.on("timeout", () => finish({ ok: false, message: `Timed out connecting to AMI at ${host}:${port}` }));
    socket.on("error", error => finish({ ok: false, message: error.message }));
    socket.on("close", () => {
      if (!finished) {
        finish({
          ok: buffer.includes("Response: Success"),
          message: buffer.includes("Response: Success") ? "FreePBX AMI accepted the command." : "AMI connection closed before success.",
          raw: buffer
        });
      }
    });
  });
}

function compactAmiMessage(raw) {
  const message = raw.split(/\r?\n/).find(line => line.toLowerCase().startsWith("message:"));
  return message ? message.replace(/^message:\s*/i, "") : "FreePBX AMI returned an error.";
}

function localAddressFor(remoteHost) {
  const interfaces = os.networkInterfaces();
  for (const addresses of Object.values(interfaces)) {
    for (const address of addresses || []) {
      if (address.family === "IPv4" && !address.internal) return address.address;
    }
  }
  return "127.0.0.1";
}

function parseSipHeaders(message) {
  const headers = {};
  for (const line of message.split(/\r?\n/).slice(1)) {
    const index = line.indexOf(":");
    if (index > 0) headers[line.slice(0, index).trim().toLowerCase()] = line.slice(index + 1).trim();
  }
  return headers;
}

function parseDigestChallenge(header = "") {
  const challenge = {};
  const value = header.replace(/^Digest\s+/i, "");
  for (const part of value.match(/(?:[^,"]+|"[^"]*")+/g) || []) {
    const [key, raw] = part.split("=");
    if (key && raw) challenge[key.trim()] = raw.trim().replace(/^"|"$/g, "");
  }
  return challenge;
}

function md5(value) {
  return crypto.createHash("md5").update(value).digest("hex");
}

function digestAuthorization({ username, password, method, uri, challenge }) {
  const realm = challenge.realm || "";
  const nonce = challenge.nonce || "";
  const qop = challenge.qop && challenge.qop.split(",").map(part => part.trim()).includes("auth") ? "auth" : "";
  const nc = "00000001";
  const cnonce = crypto.randomBytes(8).toString("hex");
  const ha1 = md5(`${username}:${realm}:${password}`);
  const ha2 = md5(`${method}:${uri}`);
  const response = qop
    ? md5(`${ha1}:${nonce}:${nc}:${cnonce}:${qop}:${ha2}`)
    : md5(`${ha1}:${nonce}:${ha2}`);

  const fields = [
    `username="${username}"`,
    `realm="${realm}"`,
    `nonce="${nonce}"`,
    `uri="${uri}"`,
    `response="${response}"`,
    "algorithm=MD5"
  ];

  if (challenge.opaque) fields.push(`opaque="${challenge.opaque}"`);
  if (qop) fields.push(`qop=${qop}`, `nc=${nc}`, `cnonce="${cnonce}"`);
  return `Digest ${fields.join(", ")}`;
}

function buildRegisterMessage(config, options = {}) {
  const method = "REGISTER";
  const domain = config.sipDomain || config.sipHost;
  const uri = `sip:${domain}`;
  const localIp = localAddressFor(config.sipHost);
  const localPort = sipSocket.address().port;
  const branch = `z9hG4bK-${crypto.randomBytes(8).toString("hex")}`;
  const tag = crypto.randomBytes(6).toString("hex");
  const callId = options.callId || `${crypto.randomBytes(12).toString("hex")}@merlin-sip`;
  const cseq = options.cseq || 1;
  const expires = options.expires ?? 300;
  const contact = `sip:${config.extension}@${localIp}:${localPort};transport=udp`;
  state.sipContact = contact;

  const lines = [
    `${method} ${uri} SIP/2.0`,
    `Via: SIP/2.0/UDP ${localIp}:${localPort};branch=${branch};rport`,
    "Max-Forwards: 70",
    `From: <sip:${config.extension}@${domain}>;tag=${tag}`,
    `To: <sip:${config.extension}@${domain}>`,
    `Call-ID: ${callId}`,
    `CSeq: ${cseq} ${method}`,
    `Contact: <${contact}>`,
    `Expires: ${expires}`,
    "Allow: INVITE, ACK, CANCEL, BYE, OPTIONS, REGISTER, REFER, NOTIFY",
    "User-Agent: Merlin SIP",
    "Content-Length: 0"
  ];

  if (options.authorization) {
    lines.splice(8, 0, `Authorization: ${options.authorization}`);
  }

  return `${lines.join("\r\n")}\r\n\r\n`;
}

function waitForSipResponse(socket, timeoutMs = 3500) {
  return new Promise(resolve => {
    const timer = setTimeout(() => {
      socket.off("message", onMessage);
      resolve({ ok: false, message: "Timed out waiting for SIP server response." });
    }, timeoutMs);

    function onMessage(buffer) {
      clearTimeout(timer);
      socket.off("message", onMessage);
      const raw = buffer.toString("utf8");
      const statusLine = raw.split(/\r?\n/)[0] || "";
      const match = statusLine.match(/^SIP\/2.0\s+(\d{3})\s*(.*)$/i);
      resolve({
        ok: Boolean(match),
        code: match ? Number(match[1]) : 0,
        reason: match ? match[2] : "Invalid SIP response",
        headers: parseSipHeaders(raw),
        raw
      });
    }

    socket.on("message", onMessage);
  });
}

async function sendSip(socket, host, port, message) {
  await new Promise((resolve, reject) => socket.send(Buffer.from(message), port, host, error => error ? reject(error) : resolve()));
  return waitForSipResponse(socket);
}

async function registerSip(config, expires = 300) {
  if (config.sipTransport !== "udp") {
    return { ok: false, message: "This no-install Node build currently supports SIP over UDP first." };
  }

  const host = config.sipHost || config.pbxHost;
  const port = Number(config.sipPort || 5060);
  const username = config.username || config.extension;
  const password = config.password;

  if (!host || !config.extension || !username || !password) {
    return { ok: false, message: "SIP host, extension, username, and password are required." };
  }

  if (sipSocket) sipSocket.close();
  sipSocket = dgram.createSocket("udp4");
  await new Promise(resolve => sipSocket.bind(0, resolve));

  const callId = `${crypto.randomBytes(12).toString("hex")}@merlin-sip`;
  const first = buildRegisterMessage({ ...config, sipHost: host }, { callId, cseq: 1, expires });
  const firstResponse = await sendSip(sipSocket, host, port, first);

  if (firstResponse.code === 200) {
    state.sipRegistered = expires > 0;
    scheduleSipRefresh(config);
    return { ok: true, message: expires > 0 ? "SIP extension registered." : "SIP extension unregistered." };
  }

  if (firstResponse.code !== 401 && firstResponse.code !== 407) {
    state.sipRegistered = false;
    return { ok: false, message: `SIP server returned ${firstResponse.code || "no"} ${firstResponse.reason || "response"}.` };
  }

  const challengeHeader = firstResponse.headers["www-authenticate"] || firstResponse.headers["proxy-authenticate"];
  const challenge = parseDigestChallenge(challengeHeader);
  const domain = config.sipDomain || host;
  const authorization = digestAuthorization({
    username,
    password,
    method: "REGISTER",
    uri: `sip:${domain}`,
    challenge
  });
  const second = buildRegisterMessage({ ...config, sipHost: host }, { callId, cseq: 2, expires, authorization });
  const secondResponse = await sendSip(sipSocket, host, port, second);

  state.sipRegistered = secondResponse.code === 200 && expires > 0;
  if (state.sipRegistered) scheduleSipRefresh(config);
  return {
    ok: secondResponse.code === 200,
    message: secondResponse.code === 200
      ? (expires > 0 ? "SIP extension registered." : "SIP extension unregistered.")
      : `SIP registration failed: ${secondResponse.code} ${secondResponse.reason}`
  };
}

function scheduleSipRefresh(config) {
  clearTimeout(sipRegistrationTimer);
  sipRegistrationTimer = setTimeout(() => {
    registerSip(config, 300).catch(() => {
      state.sipRegistered = false;
    });
  }, 240000);
}

async function handleApi(req, res) {
  if (req.url === "/api/state" && req.method === "GET") {
    sendJson(res, { state, features, extensions, queues, callHistory });
    return;
  }

  if (req.url === "/api/config" && req.method === "POST") {
    Object.assign(state, await readBody(req));
    sendJson(res, { ok: true, state });
    return;
  }

  if (req.url === "/api/test-ami" && req.method === "POST") {
    const body = await readBody(req);
    const result = await testTcp(body.host || state.pbxHost, Number(body.port || state.amiPort || 5038));
    sendJson(res, result);
    return;
  }

  if (req.url === "/api/connect-pbx" && req.method === "POST") {
    Object.assign(state, await readBody(req));

    if (state.vendor === "freepbx") {
      const result = await amiAction({}, [["Action: Ping"]]);
      sendJson(res, result);
      return;
    }

    sendJson(res, {
      ok: false,
      message: "Yeastar S100 connection needs the enabled Yeastar API/CTI details. SIP registration is not available in this no-library Node build yet."
    });
    return;
  }

  if (req.url === "/api/register-sip" && req.method === "POST") {
    Object.assign(state, await readBody(req));
    const result = await registerSip(state, 300);
    sendJson(res, { ...result, state });
    return;
  }

  if (req.url === "/api/unregister-sip" && req.method === "POST") {
    Object.assign(state, await readBody(req));
    const result = await registerSip(state, 0);
    clearTimeout(sipRegistrationTimer);
    state.sipRegistered = false;
    sendJson(res, { ...result, state });
    return;
  }

  if (req.url === "/api/dial" && req.method === "POST") {
    const body = await readBody(req);
    Object.assign(state, body);
    const destination = String(body.destination || "").trim();

    if (!destination) {
      sendJson(res, { ok: false, message: "Enter a destination first." });
      return;
    }

    if (state.vendor !== "freepbx") {
      sendJson(res, {
        ok: false,
        message: "Dial is wired for FreePBX AMI Originate first. Yeastar needs your S100 API/CTI endpoint details before it can place calls."
      });
      return;
    }

    const result = await amiAction({}, [[
      "Action: Originate",
      `Channel: PJSIP/${state.extension}`,
      "Context: from-internal",
      `Exten: ${destination}`,
      "Priority: 1",
      `CallerID: Merlin SIP <${state.extension}>`,
      "Async: true"
    ]]);

    callHistory.unshift({
      id: crypto.randomUUID(),
      direction: "outbound",
      number: destination,
      name: destination,
      startedAt: new Date().toISOString(),
      duration: result.ok ? "Active" : "00:00",
      result: result.ok ? "Dialled" : "Failed"
    });

    sendJson(res, result);
    return;
  }

  if (req.url === "/api/action" && req.method === "POST") {
    const body = await readBody(req);
    const id = crypto.randomUUID();
    if (body.type === "dnd") state.dnd = Boolean(body.enabled);
    if (body.type === "forward") state.forwarding = body.destination || "";
    if (body.type === "queuePause") state.queuePaused = Boolean(body.paused);
    if (body.type === "license") state.license = body.token ? "Licensed" : "Trial mode";

    sendJson(res, {
      ok: true,
      id,
      message: `${body.type || "action"} accepted for ${state.vendor}. Real PBX command mapping goes here.`,
      state
    });
    return;
  }

  res.writeHead(404, { "content-type": "application/json" });
  res.end(JSON.stringify({ error: "Not found" }));
}

const server = http.createServer((req, res) => {
  if (req.url.startsWith("/api/")) {
    handleApi(req, res).catch(error => {
      res.writeHead(500, { "content-type": "application/json" });
      res.end(JSON.stringify({ error: error.message }));
    });
    return;
  }

  serveStatic(req, res);
});

const port = Number(process.env.PORT || 4173);
server.listen(port, "127.0.0.1", () => {
  console.log(`Merlin SIP running at http://127.0.0.1:${port}`);
});
