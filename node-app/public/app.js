const $ = selector => document.querySelector(selector);
const state = {
  vendor: "yeastar-s100",
  dnd: false,
  queuePaused: false,
  activeCall: false
};

async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: { "content-type": "application/json" },
    ...options
  });
  return response.json();
}

function setNotice(text, good = true) {
  $("#notice").textContent = text;
  $("#status").textContent = good ? "Ready" : "Check";
  $("#status").style.background = good ? "#dff8ee" : "#fff1d6";
  $("#status").style.color = good ? "#106247" : "#8a4f08";
}

function currentConfig() {
  return {
    vendor: $("#vendor").value,
    pbxHost: $("#pbxHost").value,
    sipHost: $("#sipHost").value,
    sipPort: Number($("#sipPort").value || 5060),
    sipDomain: $("#sipDomain").value,
    sipTransport: "udp",
    extension: $("#extension").value,
    username: $("#username").value,
    password: $("#password").value,
    amiPort: Number($("#amiPort").value || 5038)
  };
}

function showView(viewId) {
  document.querySelectorAll(".view").forEach(view => view.classList.remove("active-view"));
  document.querySelectorAll(".nav").forEach(button => button.classList.remove("active"));
  $(`#${viewId}`).classList.add("active-view");
  document.querySelector(`[data-view="${viewId}"]`).classList.add("active");
}

function setActiveCall(active, destination = "") {
  state.activeCall = active;
  $("#inCallActions").classList.toggle("hidden", !active);
  $("#callButton").classList.toggle("hidden", active);
  $("#activeCallLabel").textContent = destination || $("#destination").value;
  $("#connectionBadge").textContent = active ? "In call" : "Ready";
}

function renderDnd() {
  $("#dndButton").textContent = state.dnd ? "DND on" : "DND off";
  $("#dndButton").classList.toggle("dnd-active", state.dnd);
  $("#dndButton").setAttribute("aria-pressed", String(state.dnd));
}

function renderDialpad() {
  $("#dialpad").innerHTML = "";
  "123456789*0#".split("").forEach(digit => {
    const button = document.createElement("button");
    button.textContent = digit;
    button.addEventListener("click", () => {
      $("#destination").value += digit;
      $("#destinationLabel").textContent = $("#destination").value || "Enter destination";
    });
    $("#dialpad").append(button);
  });
}

function renderFeatures(features) {
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

async function load() {
  const data = await api("/api/state");
  Object.assign(state, data.state);
  $("#vendor").value = data.state.vendor;
  $("#pbxHost").value = data.state.pbxHost || "";
  $("#sipHost").value = data.state.sipHost || "";
  $("#sipPort").value = data.state.sipPort || 5060;
  $("#sipDomain").value = data.state.sipDomain || "";
  $("#extension").value = data.state.extension || "1001";
  $("#username").value = data.state.username || "";
  $("#password").value = data.state.password || "";
  $("#amiPort").value = data.state.amiPort || 5038;
  $("#licenseState").textContent = data.state.license;
  $("#connectionBadge").textContent = data.state.sipRegistered ? "SIP registered" : "Not connected";
  renderFeatures(data.features);
  renderExtensions(data.extensions);
  renderQueues(data.queues);
  renderCallHistory(data.callHistory || []);
  setActiveCall(false);
  renderDnd();
}

document.querySelectorAll(".nav").forEach(button => {
  button.addEventListener("click", () => showView(button.dataset.view));
});

$("#destination").addEventListener("input", () => {
  $("#destinationLabel").textContent = $("#destination").value || "Enter destination";
});

document.addEventListener("click", event => {
  const historyButton = event.target.closest("[data-number]");
  if (!historyButton) return;
  $("#destination").value = historyButton.dataset.number;
  $("#destinationLabel").textContent = historyButton.dataset.number;
  showView("dialerView");
});

$("#vendor").addEventListener("change", async () => {
  Object.assign(state, currentConfig());
  await api("/api/config", { method: "POST", body: JSON.stringify(state) });
  setNotice(`PBX type set to ${state.vendor === "freepbx" ? "FreePBX" : "Yeastar S100"}`);
});

$("#saveConfig").addEventListener("click", async () => {
  Object.assign(state, currentConfig());
  await api("/api/config", { method: "POST", body: JSON.stringify(state) });
  setNotice("PBX settings saved.");
});

$("#connectPbx").addEventListener("click", async () => {
  Object.assign(state, currentConfig());
  const result = await api("/api/connect-pbx", {
    method: "POST",
    body: JSON.stringify(state)
  });
  $("#connectionBadge").textContent = result.ok ? "PBX connected" : "Not connected";
  setNotice(result.message, result.ok);
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

$("#unregisterSip").addEventListener("click", async () => {
  Object.assign(state, currentConfig());
  const result = await api("/api/unregister-sip", {
    method: "POST",
    body: JSON.stringify(state)
  });
  $("#connectionBadge").textContent = "Not connected";
  setNotice(result.message, result.ok);
});

$("#callButton").addEventListener("click", async () => {
  Object.assign(state, currentConfig());
  const destination = $("#destination").value;
  const result = await api("/api/dial", {
    method: "POST",
    body: JSON.stringify({ ...state, destination })
  });
  setNotice(result.message, result.ok);
  if (result.ok) {
    setActiveCall(true, destination);
  }
  load();
});

$("#hangupButton").addEventListener("click", async () => {
  const result = await api("/api/action", {
    method: "POST",
    body: JSON.stringify({ type: "hangup" })
  });
  setActiveCall(false);
  setNotice(result.message);
});

$("#muteButton").addEventListener("click", () => setNotice("Mute toggled locally. PBX/media wiring comes next."));
$("#holdButton").addEventListener("click", () => setNotice("Hold requested. SIP/WebRTC wiring comes next."));
$("#transferButton").addEventListener("click", () => setNotice("Transfer requested. Transfer target UI comes next."));
$("#parkButton").addEventListener("click", () => setNotice("Call park requested. PBX feature-code mapping comes next."));

$("#dndButton").addEventListener("click", async () => {
  state.dnd = !state.dnd;
  renderDnd();
  const result = await api("/api/action", {
    method: "POST",
    body: JSON.stringify({ type: "dnd", enabled: state.dnd })
  });
  setNotice(state.dnd ? "DND is active." : "DND is inactive.", result.ok !== false);
});

$("#pauseButton").addEventListener("click", async () => {
  state.queuePaused = !state.queuePaused;
  const result = await api("/api/action", {
    method: "POST",
    body: JSON.stringify({ type: "queuePause", paused: state.queuePaused })
  });
  setNotice(result.message);
});

renderDialpad();
load();
