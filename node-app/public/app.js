const $ = selector => document.querySelector(selector);
const MERLIN_PBX_HOST = "pbx.chriskendall.media";

const state = {
  vendor: "yeastar-s100",
  dnd: false,
  queuePaused: false,
  activeCall: false
};

const offlineData = {
  state: {
    vendor: "yeastar-s100",
    extension: "",
    pbxHost: MERLIN_PBX_HOST,
    amiPort: 5038,
    username: "",
    authId: "",
    password: "",
    sipHost: MERLIN_PBX_HOST,
    sipPort: 5060,
    sipDomain: MERLIN_PBX_HOST,
    sipTransport: "udp",
    sipRegistered: false,
    dnd: false,
    forwarding: "",
    queuePaused: false,
    license: "Android demo"
  },
  features: [
    ["Voice calls", "SIP registration or PBX originate/click-to-call", "SIP"],
    ["Hold / resume", "SIP session hold and resume", "SIP"],
    ["Blind transfer", "SIP REFER or PBX feature code", "SIP"],
    ["DTMF", "IVR, voicemail, and feature-code tones", "SIP"],
    ["Presence / BLF", "Yeastar API/CTI or Asterisk AMI device state", "PBX"],
    ["Queues", "Agent login, pause, waiting callers, summaries", "PBX"],
    ["Call history", "CDR, missed calls, dispositions", "PBX"],
    ["Mobile ready", "Native SIP media layer is the next Android milestone", "Roadmap"]
  ],
  extensions: [
    { number: "1001", name: "CK Media Services", department: "Sales", state: "Available" },
    { number: "1002", name: "Support Desk", department: "Support", state: "Ringing" },
    { number: "1003", name: "Accounts", department: "Finance", state: "Busy" },
    { number: "1004", name: "Warehouse", department: "Ops", state: "DND" }
  ],
  queues: [
    { name: "Sales", waiting: 2, agents: 6, paused: 1 },
    { name: "Support", waiting: 5, agents: 9, paused: 2 },
    { name: "Accounts", waiting: 0, agents: 3, paused: 0 }
  ],
  callHistory: []
};

function saveOfflineState() {
  localStorage.setItem("merlinSipAndroidState", JSON.stringify({
    state: offlineData.state,
    callHistory: offlineData.callHistory
  }));
}

function loadOfflineState() {
  const saved = localStorage.getItem("merlinSipAndroidState");
  if (!saved) return;
  try {
    const parsed = JSON.parse(saved);
    if (parsed.state) {
      Object.assign(offlineData.state, parsed.state);
      offlineData.callHistory = parsed.callHistory || [];
    } else {
      Object.assign(offlineData.state, parsed);
    }
  } catch {
    localStorage.removeItem("merlinSipAndroidState");
  }
}

async function api(path, options = {}) {
  try {
    const response = await fetch(path, {
      headers: { "content-type": "application/json" },
      ...options
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    return response.json();
  } catch {
    const body = options.body ? JSON.parse(options.body) : {};
    if (path === "/api/state") {
      loadOfflineState();
      return offlineData;
    }
    if (path === "/api/config") {
      Object.assign(offlineData.state, body);
      saveOfflineState();
      return { ok: true, state: offlineData.state };
    }
    if (path === "/api/dial") {
      const destination = String(body.destination || "").trim();
      if (!destination) return { ok: false, message: "Enter a destination first." };
      Object.assign(offlineData.state, body);
      offlineData.callHistory.unshift({
        id: `android-${Date.now()}`,
        direction: "outbound",
        number: destination,
        name: destination,
        startedAt: new Date().toISOString(),
        duration: "Active",
        result: "Dialled"
      });
      saveOfflineState();
      return { ok: true, message: `Dialling ${destination} from Android shell.` };
    }
    if (path === "/api/register-sip") {
      Object.assign(offlineData.state, body, { sipRegistered: true });
      saveOfflineState();
      return { ok: true, message: "SIP details saved. Native Android registration layer comes next.", state: offlineData.state };
    }
    if (path === "/api/unregister-sip") {
      Object.assign(offlineData.state, body, { sipRegistered: false });
      saveOfflineState();
      return { ok: true, message: "SIP registration cleared.", state: offlineData.state };
    }
    if (path === "/api/connect-pbx") {
      Object.assign(offlineData.state, body);
      saveOfflineState();
      return { ok: true, message: "PBX profile saved for Android." };
    }
    if (path === "/api/action") {
      if (body.type === "dnd") offlineData.state.dnd = Boolean(body.enabled);
      if (body.type === "queuePause") offlineData.state.queuePaused = Boolean(body.paused);
      saveOfflineState();
      return { ok: true, message: `${body.type || "action"} accepted locally.`, state: offlineData.state };
    }
    return { ok: false, message: "This Android build is running without the Node prototype server." };
  }
}

function setNotice(text, good = true) {
  $("#notice").textContent = text;
  $("#connectionBadge").textContent = good ? "Ready" : "Inactive";
  $("#connectionBadge").classList.toggle("status-warning", !good);
}

function currentConfig() {
  const username = $("#username").value.trim();
  const authId = $("#authId").value.trim();

  return {
    vendor: "yeastar-s100",
    pbxHost: MERLIN_PBX_HOST,
    sipHost: MERLIN_PBX_HOST,
    sipPort: 5060,
    sipDomain: MERLIN_PBX_HOST,
    sipTransport: "udp",
    extension: username,
    username,
    authId,
    password: $("#password").value,
    amiPort: 5038
  };
}

function showView(viewId) {
  document.querySelectorAll(".view").forEach(view => view.classList.remove("active-view"));
  $(`#${viewId}`).classList.add("active-view");
  const dialerOpen = viewId === "dialerView";
  $("#settingsToggle").classList.toggle("hidden", !dialerOpen);
  $("#historyToggle").classList.toggle("hidden", !dialerOpen);
}

function showDialerOnStartup() {
  showView("dialerView");
  $("#destination").focus({ preventScroll: true });
}

function setActiveCall(active, destination = "") {
  state.activeCall = active;
  $("#inCallActions").classList.toggle("hidden", !active);
  $("#activeCallLabel").textContent = destination || $("#destination").value;
  $("#connectionBadge").textContent = active ? "In call" : "Inactive";
  $("#connectionBadge").classList.toggle("status-warning", !active);
}

function renderDnd() {
  $("#dndButton").textContent = state.dnd ? "DND on" : "DND off";
  $("#dndButton").classList.toggle("dnd-active", state.dnd);
  $("#dndButton").setAttribute("aria-pressed", String(state.dnd));
}

function renderDialpad() {
  const labels = {
    "1": "",
    "2": "ABC",
    "3": "DEF",
    "4": "GHI",
    "5": "JKL",
    "6": "MNO",
    "7": "PQRS",
    "8": "TUV",
    "9": "WXYZ",
    "*": "",
    "0": "+",
    "#": ""
  };

  $("#dialpad").innerHTML = "";
  "123456789*0#".split("").forEach(digit => {
    const button = document.createElement("button");
    button.type = "button";
    button.innerHTML = `<span class="dial-digit">${digit}</span><span class="dial-letters">${labels[digit]}</span>`;
    button.addEventListener("click", () => {
      $("#destination").value += digit;
      playDtmfTone(digit);
    });
    $("#dialpad").append(button);
  });
}

function playDtmfTone(digit) {
  const tones = {
    "1": [697, 1209],
    "2": [697, 1336],
    "3": [697, 1477],
    "4": [770, 1209],
    "5": [770, 1336],
    "6": [770, 1477],
    "7": [852, 1209],
    "8": [852, 1336],
    "9": [852, 1477],
    "*": [941, 1209],
    "0": [941, 1336],
    "#": [941, 1477]
  };
  const pair = tones[digit];
  if (!pair) return;

  const AudioContext = window.AudioContext || window.webkitAudioContext;
  if (!AudioContext) return;

  const context = playDtmfTone.context || new AudioContext();
  playDtmfTone.context = context;

  const gain = context.createGain();
  gain.gain.setValueAtTime(0.0001, context.currentTime);
  gain.gain.exponentialRampToValueAtTime(0.16, context.currentTime + 0.01);
  gain.gain.exponentialRampToValueAtTime(0.0001, context.currentTime + 0.16);
  gain.connect(context.destination);

  pair.forEach(frequency => {
    const oscillator = context.createOscillator();
    oscillator.type = "sine";
    oscillator.frequency.value = frequency;
    oscillator.connect(gain);
    oscillator.start(context.currentTime);
    oscillator.stop(context.currentTime + 0.17);
  });
}

function renderFeatures(features) {
  if (!$("#features")) return;

  $("#features").innerHTML = "";
  features.forEach(([title, detail, tag]) => {
    const card = document.createElement("article");
    card.className = "feature";
    card.innerHTML = `<h3>${title}</h3><p>${detail}</p><span class="tag">${tag}</span>`;
    $("#features").append(card);
  });
}

function renderExtensions(extensions) {
  $("#extensions").innerHTML = extensions.map(extension => `
    <div class="row">
      <strong>${extension.number}</strong>
      <span>${extension.name}<br><small>${extension.department}</small></span>
      <span class="pill">${extension.state}</span>
    </div>
  `).join("");
}

function renderQueues(queues) {
  $("#queues").innerHTML = queues.map(queue => `
    <div class="row">
      <strong>${queue.name}</strong>
      <span>${queue.waiting} waiting<br><small>${queue.agents} agents, ${queue.paused} paused</small></span>
      <span class="pill">${queue.waiting ? "Live" : "Clear"}</span>
    </div>
  `).join("");
}

function formatTime(value) {
  return new Date(value).toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit"
  });
}

function renderCallHistory(callHistory) {
  return;

  const rows = callHistory.map(call => `
    <button class="history-row" data-number="${call.number}">
      <span class="call-direction ${call.direction}">${call.direction}</span>
      <strong>${call.name || call.number}</strong>
      <span>${call.number}</span>
      <span>${formatTime(call.startedAt)}</span>
      <span>${call.duration}</span>
      <span class="pill">${call.result}</span>
    </button>
  `).join("");

  $("#recentCalls").innerHTML = callHistory.slice(0, 5).map(call => `
    <button class="recent-call" data-number="${call.number}">
      <span class="call-direction ${call.direction}">${call.direction}</span>
      <strong>${call.name || call.number}</strong>
      <small>${call.number} · ${formatTime(call.startedAt)}</small>
    </button>
  `).join("");

  $("#fullCallHistory").innerHTML = rows || `<div class="empty-state">No call history yet.</div>`;
}

function renderCallHistory(callHistory) {
  const target = $("#fullCallHistory");
  if (!target) return;

  target.innerHTML = callHistory.map(call => `
    <button class="history-row" data-number="${call.number}">
      <span class="call-direction ${call.direction}">${call.direction}</span>
      <strong>${call.name || call.number}</strong>
      <small>${call.number} - ${formatTime(call.startedAt)}</small>
      <span class="pill">${call.result}</span>
    </button>
  `).join("") || `<div class="empty-state">No call history yet.</div>`;
}

async function load() {
  const data = await api("/api/state");
  Object.assign(state, data.state);
  state.pbxHost = MERLIN_PBX_HOST;
  state.sipHost = MERLIN_PBX_HOST;
  state.sipDomain = MERLIN_PBX_HOST;
  if ($("#accountLabel")) $("#accountLabel").textContent = data.state.username ? `Line ${data.state.username}` : "Line not configured";
  $("#username").value = data.state.username || "";
  $("#authId").value = data.state.authId || "";
  $("#password").value = data.state.password || "";
  $("#connectionBadge").textContent = data.state.sipRegistered ? "SIP registered" : "Not connected";
  renderFeatures(data.features);
  if ($("#extensions")) renderExtensions(data.extensions);
  if ($("#queues")) renderQueues(data.queues);
  renderCallHistory(data.callHistory || []);
  setActiveCall(false);
  renderDnd();
}

$("#settingsToggle").addEventListener("click", () => {
  showView("settingsView");
});

$("#historyToggle").addEventListener("click", () => {
  showView("historyView");
});

$("#closeSettingsButton").addEventListener("click", () => {
  showView("dialerView");
});

$("#closeHistoryButton").addEventListener("click", () => {
  showView("dialerView");
});

$("#clearButton").addEventListener("click", () => {
  $("#destination").value = "";
  $("#destination").focus({ preventScroll: true });
});

$("#fullCallHistory").addEventListener("click", event => {
  const historyButton = event.target.closest("[data-number]");
  if (!historyButton) return;
  $("#destination").value = historyButton.dataset.number;
  showView("dialerView");
});

$("#saveConfig").addEventListener("click", async () => {
  Object.assign(state, currentConfig());
  await api("/api/config", { method: "POST", body: JSON.stringify(state) });
  if ($("#accountLabel")) $("#accountLabel").textContent = state.username ? `Line ${state.username}` : "Line not configured";
  setNotice("Account saved.");
});

$("#registerSip").addEventListener("click", async () => {
  Object.assign(state, currentConfig());
  const result = await api("/api/register-sip", {
    method: "POST",
    body: JSON.stringify(state)
  });
  $("#connectionBadge").textContent = result.ok ? "SIP registered" : "Not connected";
  setNotice(result.message, result.ok);
});

if ($("#unregisterSip")) {
  $("#unregisterSip").addEventListener("click", async () => {
    Object.assign(state, currentConfig());
    const result = await api("/api/unregister-sip", {
      method: "POST",
      body: JSON.stringify(state)
    });
    $("#connectionBadge").textContent = "Not connected";
    setNotice(result.message, result.ok);
  });
}

$("#callButton").addEventListener("click", async () => {
  Object.assign(state, currentConfig());
  const destination = $("#destination").value;
  const result = await api("/api/dial", {
    method: "POST",
    body: JSON.stringify({ ...state, destination })
  });
  setNotice(result.ok ? result.message : "Call setup needs your PBX settings.", result.ok);
  if (result.ok) {
    setActiveCall(true, destination);
  }
  load();
});

$("#endButton").addEventListener("click", async () => {
  const result = await api("/api/action", {
    method: "POST",
    body: JSON.stringify({ type: "hangup" })
  });
  setActiveCall(false);
  setNotice(result.message);
});

$("#muteButton").addEventListener("click", () => setNotice("Mute toggled locally. PBX/media wiring comes next."));
$("#holdButton").addEventListener("click", () => setNotice("Hold requested. SIP/WebRTC wiring comes next."));
$("#transferButton").addEventListener("click", () => {
  $("#transferPopup").classList.remove("hidden");
});
$("#assistedTransferButton").addEventListener("click", () => {
  $("#transferPopup").classList.add("hidden");
  setNotice("Assisted transfer selected. Enter the target number, then confirm transfer.");
});
$("#blindTransferButton").addEventListener("click", () => {
  $("#transferPopup").classList.add("hidden");
  setNotice("Blind transfer selected. Enter the target number, then confirm transfer.");
});
$("#closeTransferPopup").addEventListener("click", () => {
  $("#transferPopup").classList.add("hidden");
});
if ($("#parkButton")) $("#parkButton").addEventListener("click", () => setNotice("Call park requested. PBX feature-code mapping comes next."));

$("#dndButton").addEventListener("click", async () => {
  state.dnd = !state.dnd;
  renderDnd();
  const result = await api("/api/action", {
    method: "POST",
    body: JSON.stringify({ type: "dnd", enabled: state.dnd })
  });
  setNotice(state.dnd ? "DND is active." : "DND is inactive.", result.ok !== false);
});

if ($("#pauseButton")) {
  $("#pauseButton").addEventListener("click", async () => {
    state.queuePaused = !state.queuePaused;
    const result = await api("/api/action", {
      method: "POST",
      body: JSON.stringify({ type: "queuePause", paused: state.queuePaused })
    });
    setNotice(result.message);
  });
}

renderDialpad();
showDialerOnStartup();
load().then(showDialerOnStartup);
