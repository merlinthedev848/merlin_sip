import { useEffect, useMemo, useState } from "react";
import {
  BadgeCheck,
  CircleDot,
  Dialpad,
  Headphones,
  KeyRound,
  Mic,
  Phone,
  PhoneOff,
  Settings,
  ShieldCheck,
  Sparkles,
  UserRound,
  Waves
} from "lucide-react";
import { pbxFeatures, pbxProfiles, PbxProfileId } from "./pbxFeatures";
import { SipAccount, SipClient, SipStatus } from "./sipClient";

const defaultAccount: SipAccount = {
  displayName: "Chris",
  sipUri: "sip:1001@example.com",
  password: "",
  websocketServer: "wss://sip-ws.example.com"
};

const statusLabel: Record<SipStatus, string> = {
  offline: "Offline",
  connecting: "Connecting",
  registered: "Registered",
  calling: "Calling",
  "in-call": "In call",
  error: "Needs attention"
};

const navItems = [
  { label: "Dialer", icon: Dialpad },
  { label: "Contacts", icon: UserRound },
  { label: "Devices", icon: Mic },
  { label: "Licensing", icon: ShieldCheck },
  { label: "Settings", icon: Settings }
];

export function App() {
  const sip = useMemo(() => new SipClient(), []);
  const [account, setAccount] = useState(defaultAccount);
  const [destination, setDestination] = useState("sip:1002@example.com");
  const [status, setStatus] = useState<SipStatus>("offline");
  const [notice, setNotice] = useState("Choose your PBX profile, connect, and start testing.");
  const [license, setLicense] = useState<{ name?: string; expiresAt?: string }>({});
  const [licenseText, setLicenseText] = useState("");
  const [profile, setProfile] = useState<PbxProfileId>("yeastar-s100");
  const [activeNav, setActiveNav] = useState("Dialer");

  useEffect(() => {
    window.signalDesk.getLicense().then(setLicense);
    return sip.onStatus((next, message) => {
      setStatus(next);
      setNotice(message ?? statusLabel[next]);
    });
  }, [sip]);

  const canCall = status === "registered" || status === "in-call" || status === "calling";
  const selectedProfile = pbxProfiles[profile];

  async function activateLicense() {
    const result = await window.signalDesk.activateLicense(licenseText);
    if (result.valid) {
      setLicense({ name: result.name, expiresAt: result.expiresAt });
      setNotice(`Licensed to ${result.name}`);
      setLicenseText("");
    } else {
      setNotice(result.reason);
    }
  }

  return (
    <main className="shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">
            <Headphones size={22} />
          </div>
          <div>
            <strong>SignalDesk</strong>
            <span>PBX companion</span>
          </div>
        </div>

        <nav className="nav" aria-label="Primary">
          {navItems.map((item) => (
            <button
              key={item.label}
              className={activeNav === item.label ? "nav-active pressable" : "pressable"}
              title={item.label}
              onClick={() => setActiveNav(item.label)}
            >
              <item.icon size={20} />
            </button>
          ))}
        </nav>

        <div className="license-mini">
          <KeyRound size={18} />
          <div>
            <span>License</span>
            <strong>{license.name ? "Active" : "Trial mode"}</strong>
          </div>
        </div>
      </aside>

      <section className="workspace">
        <header className="topbar">
          <div>
            <h1>{selectedProfile.name} Console</h1>
            <p>{notice}</p>
          </div>
          <div className={`status-pill status-${status}`}>
            <CircleDot size={16} />
            {statusLabel[status]}
          </div>
        </header>

        <section className="profile-strip">
          {(Object.keys(pbxProfiles) as PbxProfileId[]).map((id) => (
            <button
              className={profile === id ? "profile-card profile-active pressable" : "profile-card pressable"}
              key={id}
              onClick={() => setProfile(id)}
            >
              <Waves size={20} />
              <span>{pbxProfiles[id].name}</span>
              <small>{pbxProfiles[id].transport}</small>
            </button>
          ))}
        </section>

        <section className="dialer-layout">
          <div className="panel call-panel">
            <div className="call-header">
              <div>
                <span className="eyebrow">Outbound call</span>
                <h2>{destination || "Enter a destination"}</h2>
              </div>
              <BadgeCheck size={24} />
            </div>

            <input
              className="destination"
              value={destination}
              onChange={(event) => setDestination(event.target.value)}
              placeholder="sip:1002@example.com"
            />

            <div className="dialpad">
              {"123456789*0#".split("").map((digit) => (
                <button className="pressable" key={digit} onClick={() => setDestination((value) => value + digit)}>
                  {digit}
                </button>
              ))}
            </div>

            <div className="call-actions">
              <button className="primary-action pressable" disabled={!canCall || status === "in-call"} onClick={() => sip.call(destination)}>
                <Phone size={20} />
                Call
              </button>
              <button className="danger-action pressable" disabled={status !== "calling" && status !== "in-call"} onClick={() => sip.hangup()}>
                <PhoneOff size={20} />
                Hang up
              </button>
            </div>
          </div>

          <div className="side-stack">
            <div className="panel settings-panel">
              <div className="panel-title">
                <h2>SIP Account</h2>
                <span>{selectedProfile.name}</span>
              </div>

              <label>
                Display name
                <input value={account.displayName} onChange={(event) => setAccount({ ...account, displayName: event.target.value })} />
              </label>
              <label>
                SIP URI
                <input value={account.sipUri} onChange={(event) => setAccount({ ...account, sipUri: event.target.value })} />
              </label>
              <label>
                Password
                <input
                  type="password"
                  value={account.password}
                  onChange={(event) => setAccount({ ...account, password: event.target.value })}
                />
              </label>
              <label>
                WebSocket server
                <input
                  value={account.websocketServer}
                  onChange={(event) => setAccount({ ...account, websocketServer: event.target.value })}
                />
              </label>

              <div className="button-row">
                <button className="pressable" onClick={() => sip.connect(account)} disabled={status === "connecting" || status === "registered"}>
                  Register
                </button>
                <button className="pressable" onClick={() => sip.disconnect()} disabled={status === "offline"}>
                  Disconnect
                </button>
              </div>
            </div>

            <div className="panel license-panel">
              <div className="panel-title">
                <h2>Licensing</h2>
                <span>{license.expiresAt ? `Expires ${license.expiresAt}` : "Offline activation"}</span>
              </div>
              <textarea
                value={licenseText}
                onChange={(event) => setLicenseText(event.target.value)}
                placeholder="Paste a signed license token"
              />
              <button className="pressable" onClick={activateLicense} disabled={!licenseText.trim()}>
                Activate license
              </button>
            </div>
          </div>
        </section>

        <section className="feature-section">
          <div className="feature-heading">
            <div>
              <span className="eyebrow">PBX feature surface</span>
              <h2>Built for Yeastar S-Series and FreePBX parity</h2>
            </div>
            <p>{selectedProfile.notes}</p>
          </div>

          <div className="feature-grid">
            {pbxFeatures.map((feature) => (
              <article className="feature-tile pressable" key={feature.title}>
                <div className={`feature-icon feature-${feature.status}`}>
                  <feature.icon size={19} />
                </div>
                <div>
                  <h3>{feature.title}</h3>
                  <p>{feature.detail}</p>
                </div>
                <span className={`feature-tag tag-${feature.status}`}>
                  {feature.status === "sip" ? "SIP" : feature.status === "api" ? "PBX API" : "Roadmap"}
                </span>
              </article>
            ))}
          </div>
        </section>

        <section className="mobile-band">
          <Sparkles size={22} />
          <div>
            <h2>Mobile-ready direction</h2>
            <p>
              The domain model, PBX adapters, licensing flow, and design language should be shared with a React Native app,
              while native mobile call handling, push notifications, and background registration are built per platform.
            </p>
          </div>
        </section>
      </section>
    </main>
  );
}
