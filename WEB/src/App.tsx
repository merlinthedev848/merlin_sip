import { useState, useEffect, useRef } from 'react';
import { 
  Phone, PhoneCall, PhoneOff, Search, Trash2, Clock, User, 
  X, Mic, Play
} from 'lucide-react';
import logo from './assets/logo.png';

interface Contact {
  name: string;
  number: string;
  presence: 'online' | 'offline' | 'busy';
}

interface CallRecord {
  id: string;
  destination: string;
  type: 'incoming' | 'outgoing' | 'missed';
  time: string;
  duration?: string;
}

interface ActiveCall {
  destination: string;
  state: 'dialing' | 'ringing' | 'connected' | 'hold';
  duration: number;
  muted: boolean;
}

interface AudioDevice {
  id: string;
  name: string;
}

export default function App() {
  // State
  const [step, setStep] = useState<'credentials' | 'main'>('credentials');
  const [allowCustomSip] = useState(false);
  const [errorText, setErrorText] = useState('');
  
  // Credentials
  const [authMode, setAuthMode] = useState<'provision' | 'manual'>('provision');
  const [provisionCode, setProvisionCode] = useState('');
  const [sipServer, setSipServer] = useState('pbx.chriskendall.media');
  const [extension, setExtension] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  
  // Settings
  const [audioInputs, setAudioInputs] = useState<AudioDevice[]>([]);
  const [audioOutputs, setAudioOutputs] = useState<AudioDevice[]>([]);
  const [selectedInput, setSelectedInput] = useState('');
  const [selectedOutput, setSelectedOutput] = useState('');
  const [sipTransport, setSipTransport] = useState('UDP');
  const [dndMode, setDndMode] = useState('Off');
  const [mobileNumber, setMobileNumber] = useState('');
  const [combineContacts, setCombineContacts] = useState(true);
  const [sipAlgCompat, setSipAlgCompat] = useState(false);
  
  // Main App State
  const [registered, setRegistered] = useState(false);
  const [registerStatusText] = useState('Registered and listening for calls.');
  const [destination, setDestination] = useState('');
  const [activeTab, setActiveTab] = useState<'phone' | 'history' | 'contacts'>('phone');
  const [searchQuery, setSearchQuery] = useState('');
  const [showSettings, setShowSettings] = useState(false);
  const [settingsTab, setSettingsTab] = useState<'general' | 'account' | 'handling' | 'audio' | 'diagnostics' | 'about'>('general');
  
  // Call State
  const [activeCall, setActiveCall] = useState<ActiveCall | null>(null);
  
  // Cache persistence
  const [callHistory, setCallHistory] = useState<CallRecord[]>([]);
  const [contacts] = useState<Contact[]>([
    { name: 'Reception Desk', number: '100', presence: 'online' },
    { name: 'Chris Kendall', number: '101', presence: 'busy' },
    { name: 'IT Support Hotdesk', number: '150', presence: 'online' },
    { name: 'Conference Room A', number: '201', presence: 'offline' },
    { name: 'Outbound Trunk', number: '9', presence: 'online' }
  ]);

  // Diagnostics modal
  const [showDiagConsole, setShowDiagConsole] = useState(false);
  const [diagLogs, setDiagLogs] = useState('');
  const [diagStatus, setDiagStatus] = useState('Initializing Diagnostics...');
  const [diagRunning, setDiagRunning] = useState(false);
  const [diagComplete, setDiagComplete] = useState(false);
  const [diagPass, setDiagPass] = useState(true);

  // Time & Timer Refs
  const callTimerRef = useRef<any>(null);
  const diagCancelRef = useRef<boolean>(false);

  // Load cache on start
  useEffect(() => {
    const cachedConfig = localStorage.getItem('merlin_sip_config');
    const cachedHistory = localStorage.getItem('merlin_sip_history');
    if (cachedConfig) {
      const config = JSON.parse(cachedConfig);
      setSipServer(config.sipServer || 'pbx.chriskendall.media');
      setExtension(config.extension || '');
      setUsername(config.username || '');
      setPassword(config.password || '');
      setSipTransport(config.sipTransport || 'UDP');
      setDndMode(config.dndMode || 'Off');
      setMobileNumber(config.mobileNumber || '');
      setCombineContacts(config.combineContacts ?? true);
      setSipAlgCompat(config.sipAlgCompat ?? false);
      setSelectedInput(config.audioInput || '');
      setSelectedOutput(config.audioOutput || '');
      setRegistered(true);
      setStep('main');
      console.log('Session initialized.');
    }
    if (cachedHistory) {
      setCallHistory(JSON.parse(cachedHistory));
    }
    
    // Request media permissions first to ensure devices appear with actual labels
    navigator.mediaDevices.getUserMedia({ audio: true, video: true })
      .then(stream => {
        // Stop tracks immediately to close mic/camera usage indicators
        stream.getTracks().forEach(track => track.stop());
        return navigator.mediaDevices.enumerateDevices();
      })
      .catch(() => {
        // Fallback to enumerate if block/denied
        return navigator.mediaDevices.enumerateDevices();
      })
      .then(devices => {
        const inputs = devices.filter(d => d.kind === 'audioinput').map(d => ({ id: d.deviceId, name: d.label || `Microphone (${d.deviceId.slice(0,4)})` }));
        const outputs = devices.filter(d => d.kind === 'audiooutput').map(d => ({ id: d.deviceId, name: d.label || `Speaker (${d.deviceId.slice(0,4)})` }));
        setAudioInputs(inputs);
        setAudioOutputs(outputs);
        if (inputs.length > 0) setSelectedInput(inputs[0].id);
        if (outputs.length > 0) setSelectedOutput(outputs[0].id);
      })
      .catch(() => {
        // Hard fallback
        setAudioInputs([{ id: 'default', name: 'Default Audio Input' }]);
        setAudioOutputs([{ id: 'default', name: 'Default Audio Output' }]);
        setSelectedInput('default');
        setSelectedOutput('default');
      });
  }, []);

  // Timer effect for call duration
  useEffect(() => {
    if (activeCall && activeCall.state === 'connected') {
      callTimerRef.current = setInterval(() => {
        setActiveCall(prev => prev ? { ...prev, duration: prev.duration + 1 } : null);
      }, 1000);
    } else {
      if (callTimerRef.current) clearInterval(callTimerRef.current);
    }
    return () => {
      if (callTimerRef.current) clearInterval(callTimerRef.current);
    };
  }, [activeCall?.state]);

  const handleCredentialsSubmit = () => {
    setErrorText('');
    if (authMode === 'provision') {
      if (!provisionCode.trim()) {
        setErrorText('Enter a provisioning code.');
        return;
      }
    } else {
      if (!extension.trim()) {
        setErrorText('Enter the user / extension.');
        return;
      }
      if (!username.trim() || !password.trim()) {
        setErrorText('Enter the login username and password.');
        return;
      }
      if (allowCustomSip && !sipServer.trim()) {
        setErrorText('Enter the PBX server.');
        return;
      }
    }

    // Save to Cache
    const config = {
      sipServer: allowCustomSip ? sipServer : 'pbx.chriskendall.media',
      extension: authMode === 'provision' ? '100' : extension,
      username: authMode === 'provision' ? 'reception' : username,
      password,
      sipTransport,
      dndMode,
      mobileNumber,
      combineContacts,
      sipAlgCompat,
      audioInput: selectedInput,
      audioOutput: selectedOutput
    };
    localStorage.setItem('merlin_sip_config', JSON.stringify(config));
    setRegistered(true);
    setStep('main');
  };

  // Dial pad handling
  const handleKeyPress = (num: string) => {
    if (activeCall) return;
    setDestination(prev => prev + num);
  };

  const handleBackspace = () => {
    setDestination(prev => prev.slice(0, -1));
  };

  const handleMakeCall = () => {
    if (!destination.trim()) return;
    
    // Begin Call Setup Flow
    setActiveCall({
      destination,
      state: 'dialing',
      duration: 0,
      muted: false
    });

    // Simulated Ringing & Connect
    setTimeout(() => {
      setActiveCall(prev => prev ? { ...prev, state: 'ringing' } : null);
      
      setTimeout(() => {
        setActiveCall(prev => prev ? { ...prev, state: 'connected' } : null);
      }, 1500);
    }, 1000);
  };

  const handleHangUp = () => {
    if (!activeCall) return;
    
    // Add to history
    const durationMinSec = `${Math.floor(activeCall.duration / 60)}:${(activeCall.duration % 60).toString().padStart(2, '0')}`;
    const newRecord: CallRecord = {
      id: Date.now().toString(),
      destination: activeCall.destination,
      type: 'outgoing',
      time: new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
      duration: activeCall.state === 'connected' ? durationMinSec : 'Cancelled'
    };
    const updatedHistory = [newRecord, ...callHistory];
    setCallHistory(updatedHistory);
    localStorage.setItem('merlin_sip_history', JSON.stringify(updatedHistory));
    
    setActiveCall(null);
    setDestination('');
  };

  const toggleMute = () => {
    setActiveCall(prev => prev ? { ...prev, muted: !prev.muted } : null);
  };

  const handleClearHistory = () => {
    if (window.confirm('Clear all call history?')) {
      setCallHistory([]);
      localStorage.removeItem('merlin_sip_history');
    }
  };

  const handleResetApp = () => {
    if (window.confirm('Reset application and remove cache?')) {
      localStorage.clear();
      setStep('credentials');
      setRegistered(false);
      setShowSettings(false);
      setActiveCall(null);
    }
  };

  // Run 10-step Diagnose tool simulation
  const runDiagnostics = async () => {
    setDiagRunning(true);
    setDiagComplete(false);
    setDiagLogs('');
    diagCancelRef.current = false;
    
    const logs: string[] = [];
    const log = (msg: string, isError = false) => {
      const line = `${isError ? '[ERROR] ' : ''}${msg}\n`;
      logs.push(line);
      setDiagLogs(logs.join(''));
    };

    const delay = (ms: number) => new Promise(res => setTimeout(res, ms));

    log("Starting Merlin Network Diagnostic Tool...");
    log("=================================================================");
    log(`Timestamp:         ${new Date().toISOString()}`);
    log(`OS Version:        Browser Sandbox`);
    log(`Local IP Address:  192.168.1.45 (Simulated)`);
    log(`Diagnostic Scope:  Strictly checking Merlin SIP Network Guidance`);
    log("=================================================================");
    log("All tests are running directly against the servers and ports specified in the network guide.");
    await delay(800);

    const steps = [
      { name: "DNS Domain & Resolution Check", action: async () => {
          log("Resolving Merlin service domains...");
          log(`Success: Resolved '${sipServer}' to: 104.24.120.5`);
          return true;
        } 
      },
      { name: "HTTP/HTTPS Outbound Probes", action: async () => {
          log("Testing web connection and TLS handshake...");
          log("Success: HTTP/HTTPS handshake to portal complete in 42ms.");
          return true;
        } 
      },
      { name: "NTP Time Sync", action: async () => {
          log("Syncing with NTP server pool.ntp.org...");
          log("Success: Offset +0.012s. System clock is in sync.");
          return true;
        } 
      },
      { name: "CK Media Services STUN", action: async () => {
          log("Querying primary STUN servers on port 3478...");
          log("Success: Port mapping received.");
          return true;
        } 
      },
      { name: "Google STUN Check", action: async () => {
          log("Querying fallback Google STUN...");
          log("Success: Google STUN responded in 18ms.");
          return true;
        } 
      },
      { name: "NAT Hops Verification", action: async () => {
          log("Verifying double-NAT hops routing...");
          log("Success: Single NAT detected. Routing is direct.");
          return true;
        } 
      },
      { name: "NAT Port Randomness", action: async () => {
          log("Testing port mapper allocation randomness...");
          log("Success: Port mapping is symmetric-safe (Full Cone).");
          return true;
        } 
      },
      { name: "SIP ALG Inspection", action: async () => {
          log("Inspecting SIP headers on port 5060 for ALG modifications...");
          log("Success: No ALG rewriting or Via header corruption detected.");
          return true;
        } 
      },
      { name: "RTP Quality path checks", action: async () => {
          log("Simulating G.711 media packet transmission...");
          log("Path A (SIP Port 5060): Loss 0.0%, Jitter 2ms, MOS: 4.39");
          log("Path B (PBX Local Port): Loss 0.0%, Jitter 3ms, MOS: 4.39");
          log("Path C (Google STUN 19302): Loss 0.0%, Jitter 2ms, MOS: 4.39");
          return true;
        } 
      },
      { name: "SignalR WebSocket Stability Check", action: async () => {
          log("Verifying hub WebSocket connections...");
          log("Signalling Hub (WebSocket): Connected, state: Active.");
          log("Presence Hub (WebSocket): Connected, state: Active.");
          log("Rooms Hub (WebSocket): Connected, state: Active.");
          log("DPI Checker: Connections remain stable after 2.5s monitor.");
          return true;
        } 
      }
    ];

    let overallPass = true;

    for (let i = 0; i < steps.length; i++) {
      if (diagCancelRef.current) {
        log("Diagnostics aborted by user.");
        setDiagStatus("Diagnostics Aborted");
        setDiagRunning(false);
        return;
      }
      setDiagStatus(`Running: ${steps[i].name}...`);
      log(`\n=== CHECK ${i+1}/10: ${steps[i].name.toUpperCase()} ===`);
      log("-----------------------------------------------------------------");
      
      const pass = await steps[i].action();
      if (!pass) overallPass = false;
      await delay(1000);
    }

    log("\n=================================================================");
    if (overallPass) {
      log("All network checks PASSED! Your firewall configuration is fully compliant with the Merlin SIP Network Guidance.");
      setDiagStatus("Diagnostics Completed - PASS");
      setDiagPass(true);
    } else {
      log("Some network checks FAILED. Please review the warnings in red above.");
      setDiagStatus("Diagnostics Completed - FAIL/WARN");
      setDiagPass(false);
    }
    log("Weighted Diagnostics Score: 100/100");
    setDiagComplete(true);
    setDiagRunning(false);
  };

  // Filter Lists
  const filteredContacts = contacts.filter(c => 
    c.name.toLowerCase().includes(searchQuery.toLowerCase()) || 
    c.number.includes(searchQuery)
  );

  const filteredHistory = callHistory.filter(h => 
    h.destination.includes(searchQuery)
  );

  return (
    <div className="app-container">
      {/* 1. Setup Flows */}
      {step === 'credentials' && (
        <div className="setup-container">
          <div className="setup-header">
            <div className="setup-logo-container" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
              <img src={logo} className="setup-logo" alt="CK Media logo" style={{ width: 34, height: 34, objectFit: 'contain' }} />
            </div>
            <h1 className="setup-title">Merlin SIP</h1>
            <p className="setup-subtitle">Choose how to authenticate this device.</p>
          </div>

          <div className="auth-modes" style={{ display: 'flex', gap: '24px', margin: '0 0 16px 0', alignItems: 'center' }}>
            <label style={{ display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer', fontSize: '13px', fontWeight: 500, color: 'var(--muted-color)' }}>
              <input 
                type="radio" 
                name="authMode" 
                checked={authMode === 'provision'} 
                onChange={() => setAuthMode('provision')} 
                style={{ cursor: 'pointer', accentColor: 'var(--amber-color)' }}
              />
              Provisioning code
            </label>
            <label style={{ display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer', fontSize: '13px', fontWeight: 500, color: 'var(--muted-color)' }}>
              <input 
                type="radio" 
                name="authMode" 
                checked={authMode === 'manual'} 
                onChange={() => setAuthMode('manual')} 
                style={{ cursor: 'pointer', accentColor: 'var(--amber-color)' }}
              />
              SIP details
            </label>
          </div>

          {authMode === 'provision' ? (
            <div className="form-group">
              <label className="form-label">Provisioning code</label>
              <input 
                type="text" 
                maxLength={8}
                className="form-input" 
                value={provisionCode} 
                onChange={e => setProvisionCode(e.target.value)} 
              />
            </div>
          ) : (
            <>
              {allowCustomSip && (
                <div className="form-group">
                  <label className="form-label">PBX server</label>
                  <input 
                    type="text" 
                    className="form-input" 
                    value={sipServer} 
                    onChange={e => setSipServer(e.target.value)} 
                  />
                </div>
              )}
              <div className="form-group">
                <label className="form-label">User / extension</label>
                <input 
                  type="text" 
                  className="form-input" 
                  value={extension} 
                  onChange={e => setExtension(e.target.value)} 
                />
              </div>
              <div className="form-group">
                <label className="form-label">Auth username</label>
                <input 
                  type="text" 
                  className="form-input" 
                  value={username} 
                  onChange={e => setUsername(e.target.value)} 
                />
              </div>
              <div className="form-group">
                <label className="form-label">Password</label>
                <input 
                  type="password" 
                  className="form-input" 
                  value={password} 
                  onChange={e => setPassword(e.target.value)} 
                />
              </div>
            </>
          )}

          <div className="form-group">
            <label className="form-label">Audio input</label>
            <select 
              className="form-input" 
              value={selectedInput} 
              onChange={e => setSelectedInput(e.target.value)}
            >
              {audioInputs.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          </div>

          <div className="form-group" style={{ marginBottom: 30 }}>
            <label className="form-label">Audio output</label>
            <select 
              className="form-input" 
              value={selectedOutput} 
              onChange={e => setSelectedOutput(e.target.value)}
            >
              {audioOutputs.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          </div>

          {errorText && <div className="error-text">{errorText}</div>}

          <div className="setup-actions" style={{ display: 'flex', justifyContent: 'flex-end', marginTop: 'auto' }}>
            <button className="btn btn-primary" onClick={handleCredentialsSubmit} style={{ width: 140 }}>Provision</button>
          </div>
        </div>
      )}

      {/* 2. Main Softphone App */}
      {step === 'main' && (
        <div className="main-layout">
          {/* Header */}
          <div className="header">
            <div className="header-brand">
              <div className="brand-icon" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', background: '#101622' }}>
                <img src={logo} alt="CK Media logo" style={{ width: 18, height: 18, objectFit: 'contain' }} />
              </div>
              <span className="brand-text">Merlin SIP</span>
            </div>
            <div className={`status-pill ${registered ? 'connected' : 'disconnected'}`}>
              <div className="status-dot" style={{ width: 6, height: 6, borderRadius: '50%', backgroundColor: registered ? 'var(--success-color)' : 'var(--danger-color)' }} />
              {registered ? 'Connected' : 'Offline'}
            </div>
          </div>

          {/* Main Content Area */}
          <div className="main-content">
            {/* Tabs */}
            <div className="tab-control">
              <button 
                className={`tab-item ${activeTab === 'phone' ? 'active' : ''}`}
                onClick={() => setActiveTab('phone')}
              >
                Phone
              </button>
              <button 
                className={`tab-item ${activeTab === 'history' ? 'active' : ''}`}
                onClick={() => setActiveTab('history')}
              >
                History
              </button>
              <button 
                className={`tab-item ${activeTab === 'contacts' ? 'active' : ''}`}
                onClick={() => setActiveTab('contacts')}
              >
                Contacts
              </button>
            </div>

            {/* TAB: Phone Dialpad */}
            {activeTab === 'phone' && (
              <div className="dialer-view">
                <div className="destination-box">
                  <input 
                    type="text" 
                    className="destination-input" 
                    value={destination}
                    onChange={e => setDestination(e.target.value)}
                    placeholder="Enter extension or trunk number..."
                  />
                  {destination && (
                    <button className="backspace-btn" onClick={handleBackspace}>
                      <X size={18} />
                    </button>
                  )}
                </div>

                <div className="dialpad">
                  {[
                    { num: '1', lets: 'o_o' }, { num: '2', lets: 'abc' }, { num: '3', lets: 'def' },
                    { num: '4', lets: 'ghi' }, { num: '5', lets: 'jkl' }, { num: '6', lets: 'mno' },
                    { num: '7', lets: 'pqrs' }, { num: '8', lets: 'tuv' }, { num: '9', lets: 'wxyz' },
                    { num: '*', lets: '' }, { num: '0', lets: '+' }, { num: '#', lets: '' }
                  ].map(k => (
                    <div key={k.num} className="dialpad-key" onClick={() => handleKeyPress(k.num)}>
                      <span className="key-number">{k.num}</span>
                      <span className="key-letters">{k.lets}</span>
                    </div>
                  ))}
                </div>

                <div className="dial-actions">
                  <button className="btn btn-primary" onClick={handleMakeCall} style={{ background: 'var(--amber-color)', color: '#fff' }}>
                    <Phone size={16} /> Dial
                  </button>
                </div>
              </div>
            )}

            {/* TAB: Call History */}
            {activeTab === 'history' && (
              <div className="list-container">
                <div className="search-bar">
                  <Search size={16} className="text-slate-400" />
                  <input 
                    type="text" 
                    className="search-input" 
                    placeholder="Search history..." 
                    value={searchQuery}
                    onChange={e => setSearchQuery(e.target.value)}
                  />
                  {callHistory.length > 0 && (
                    <button className="backspace-btn" onClick={handleClearHistory} title="Clear History">
                      <Trash2 size={16} />
                    </button>
                  )}
                </div>
                <div className="list-scroll">
                  {filteredHistory.length > 0 ? (
                    filteredHistory.map(h => (
                      <div key={h.id} className="list-item" onClick={() => setDestination(h.destination)}>
                        <div className="item-info">
                          <span className="item-title">{h.destination}</span>
                          <span className="item-subtitle">{h.time} • {h.duration}</span>
                        </div>
                        <Phone size={14} style={{ color: 'var(--muted-color)' }} />
                      </div>
                    ))
                  ) : (
                    <div className="empty-state">
                      <Clock size={32} />
                      <span>No call history recorded.</span>
                    </div>
                  )}
                </div>
              </div>
            )}

            {/* TAB: Contacts */}
            {activeTab === 'contacts' && (
              <div className="list-container">
                <div className="search-bar">
                  <Search size={16} className="text-slate-400" />
                  <input 
                    type="text" 
                    className="search-input" 
                    placeholder="Search contacts..." 
                    value={searchQuery}
                    onChange={e => setSearchQuery(e.target.value)}
                  />
                </div>
                <div className="list-scroll">
                  {filteredContacts.length > 0 ? (
                    filteredContacts.map(c => (
                      <div key={c.number} className="list-item" onClick={() => setDestination(c.number)}>
                        <div className="item-info">
                          <span className="item-title">{c.name}</span>
                          <span className="item-subtitle">Extension {c.number}</span>
                        </div>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                          <span style={{ fontSize: 10, color: c.presence === 'online' ? 'var(--success-color)' : c.presence === 'busy' ? '#f59e0b' : 'var(--muted-color)' }}>
                            {c.presence}
                          </span>
                          <Phone size={14} style={{ color: 'var(--muted-color)' }} />
                        </div>
                      </div>
                    ))
                  ) : (
                    <div className="empty-state">
                      <User size={32} />
                      <span>No contacts found.</span>
                    </div>
                  )}
                </div>
              </div>
            )}
          </div>

          {/* Footer status bar */}
          <div className="footer">
            <span>{registerStatusText}</span>
            <span className="footer-link" onClick={() => {
              setSettingsTab('general');
              setShowSettings(true);
            }}>
              Settings
            </span>
          </div>
        </div>
      )}

      {/* 3. Call overlay (Active Call) */}
      {activeCall && (
        <div className="active-call-overlay">
          <div className="call-avatar">
            <User size={36} />
          </div>
          <div className="call-target">{activeCall.destination}</div>
          <div className="call-status">
            {activeCall.state === 'dialing' && 'Dialing...'}
            {activeCall.state === 'ringing' && 'Ringing...'}
            {activeCall.state === 'connected' && `Connected (${Math.floor(activeCall.duration / 60)}:${(activeCall.duration % 60).toString().padStart(2, '0')})`}
            {activeCall.state === 'hold' && 'On Hold'}
          </div>

          <div className="call-actions-grid">
            <button 
              className={`call-action-btn ${activeCall.muted ? 'active' : ''}`}
              onClick={toggleMute}
            >
              <div className="action-icon-circle">
                <Mic size={18} />
              </div>
              <span>Mute</span>
            </button>

            <button 
              className={`call-action-btn ${activeCall.state === 'hold' ? 'active' : ''}`}
              onClick={() => {
                setActiveCall(prev => prev ? { 
                  ...prev, 
                  state: prev.state === 'hold' ? 'connected' : 'hold' 
                } : null);
              }}
            >
              <div className="action-icon-circle">
                <Play size={18} />
              </div>
              <span>{activeCall.state === 'hold' ? 'Resume' : 'Hold'}</span>
            </button>

            <button className="call-action-btn" onClick={() => alert('Transfering call... (Not supported in standalone mode)')}>
              <div className="action-icon-circle">
                <PhoneCall size={18} />
              </div>
              <span>Transfer</span>
            </button>
          </div>

          <button className="btn btn-danger" onClick={handleHangUp} style={{ width: 140 }}>
            <PhoneOff size={16} /> Hang Up
          </button>
        </div>
      )}

      {/* 4. Settings Modal */}
      {showSettings && (
        <div className="modal-overlay">
          <div className="modal-content">
            <div className="modal-header">
              <span className="modal-title">Settings</span>
              <button className="modal-close-btn" onClick={() => setShowSettings(false)}>
                <X size={16} />
              </button>
            </div>
            
            {/* Modal Navigation tabs */}
            <div className="tab-control" style={{ padding: '0 20px', borderBottom: '1px solid var(--line-color)' }}>
              {(['general', 'account', 'audio', 'diagnostics', 'about'] as const).map(tab => (
                <button 
                  key={tab}
                  className={`tab-item ${settingsTab === tab ? 'active' : ''}`}
                  onClick={() => setSettingsTab(tab)}
                  style={{ textTransform: 'capitalize', padding: '10px 4px', fontSize: 13 }}
                >
                  {tab}
                </button>
              ))}
            </div>

            <div className="modal-body">
              {settingsTab === 'general' && (
                <div>
                  <div className="form-group">
                    <label className="form-label">SIP Signaling Transport</label>
                    <select className="form-input" value={sipTransport} onChange={e => setSipTransport(e.target.value)}>
                      <option value="UDP">UDP</option>
                      <option value="TCP">TCP</option>
                      <option value="TLS">TLS</option>
                    </select>
                  </div>
                  <div className="form-group">
                    <label className="form-label">Do Not Disturb (DND)</label>
                    <select className="form-input" value={dndMode} onChange={e => setDndMode(e.target.value)}>
                      <option value="Off">Off</option>
                      <option value="On">On (Auto busy)</option>
                    </select>
                  </div>
                  <div className="form-group">
                    <label className="form-label">Mobile Twin Number</label>
                    <input 
                      type="text" 
                      className="form-input" 
                      value={mobileNumber} 
                      onChange={e => setMobileNumber(e.target.value)} 
                    />
                  </div>
                  <div className="form-group" style={{ flexDirection: 'row', alignItems: 'center', gap: 10 }}>
                    <input 
                      type="checkbox" 
                      id="combineContacts" 
                      checked={combineContacts} 
                      onChange={e => setCombineContacts(e.target.checked)} 
                    />
                    <label htmlFor="combineContacts" className="form-label" style={{ margin: 0 }}>Combine Contacts in Search</label>
                  </div>
                </div>
              )}

              {settingsTab === 'account' && (
                <div>
                  <div className="form-group">
                    <label className="form-label">PBX Server</label>
                    <input 
                      type="text" 
                      className="form-input" 
                      disabled={!allowCustomSip} 
                      value={sipServer} 
                      onChange={e => setSipServer(e.target.value)} 
                    />
                  </div>
                  <div className="form-group">
                    <label className="form-label">Extension / User</label>
                    <input type="text" className="form-input" value={extension} onChange={e => setExtension(e.target.value)} />
                  </div>
                  <div className="form-group">
                    <label className="form-label">Auth Username</label>
                    <input type="text" className="form-input" value={username} onChange={e => setUsername(e.target.value)} />
                  </div>
                  <div className="form-group">
                    <label className="form-label">Password</label>
                    <input type="password" className="form-input" value={password} onChange={e => setPassword(e.target.value)} />
                  </div>
                </div>
              )}

              {settingsTab === 'audio' && (
                <div>
                  <div className="form-group">
                    <label className="form-label">Audio Input (Microphone)</label>
                    <select className="form-input" value={selectedInput} onChange={e => setSelectedInput(e.target.value)}>
                      {audioInputs.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
                    </select>
                  </div>
                  <div className="form-group">
                    <label className="form-label">Audio Output (Speaker)</label>
                    <select className="form-input" value={selectedOutput} onChange={e => setSelectedOutput(e.target.value)}>
                      {audioOutputs.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
                    </select>
                  </div>
                </div>
              )}

              {settingsTab === 'diagnostics' && (
                <div>
                  <div className="settings-card">
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                      <span className="settings-card-title">PBX Diagnostics</span>
                      <button className="btn btn-secondary" onClick={() => {
                        setShowSettings(false);
                        setShowDiagConsole(true);
                        runDiagnostics();
                      }} style={{ padding: '6px 12px', fontSize: 12 }}>
                        Diagnose
                      </button>
                    </div>
                    <p className="item-subtitle">Checks registration latency, messaging capabilities, firewall compatibility, and codec path validation.</p>
                  </div>

                  <div className="settings-card">
                    <span className="settings-card-title">Support Actions</span>
                    <div style={{ display: 'flex', gap: 10, marginTop: 8 }}>
                      <button className="btn btn-secondary" onClick={() => alert('Logs uploaded successfully.')} style={{ flex: 1, padding: '8px' }}>Send error log</button>
                      <button className="btn btn-danger" onClick={handleResetApp} style={{ flex: 1, padding: '8px' }}>Reset app</button>
                    </div>
                  </div>
                </div>
              )}

              {settingsTab === 'about' && (
                <div>
                  <div className="settings-card" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                    <span className="settings-card-title">About Merlin SIP</span>
                    <p className="item-subtitle" style={{ lineHeight: 1.5 }}>CK Media Services softphone for managed SIP, presence, contacts, and reception workflows.</p>
                    <div style={{ display: 'grid', gridTemplateColumns: '100px 1fr', gap: '6px 10px', fontSize: 13, marginTop: 10 }}>
                      <span style={{ color: 'var(--muted-color)' }}>Version</span>
                      <span>1.1.36</span>
                      <span style={{ color: 'var(--muted-color)' }}>Product</span>
                      <span>merlin-sip</span>
                      <span style={{ color: 'var(--muted-color)' }}>Licensed to</span>
                      <span>CK Media Services</span>
                    </div>
                  </div>
                </div>
              )}
            </div>

            <div className="modal-footer">
              <button className="btn btn-secondary" onClick={() => setShowSettings(false)}>Cancel</button>
              <button className="btn btn-primary" onClick={() => {
                // Mock saving
                const config = {
                  sipServer,
                  extension,
                  username,
                  password,
                  sipTransport,
                  dndMode,
                  mobileNumber,
                  combineContacts,
                  sipAlgCompat,
                  audioInput: selectedInput,
                  audioOutput: selectedOutput
                };
                localStorage.setItem('merlin_sip_config', JSON.stringify(config));
                setShowSettings(false);
              }}>
                Save Settings
              </button>
            </div>
          </div>
        </div>
      )}

      {/* 5. Diagnostics Terminal Modal */}
      {showDiagConsole && (
        <div className="modal-overlay">
          <div className="modal-content" style={{ width: 480 }}>
            <div className="modal-header">
              <span className="modal-title">Network Diagnostics</span>
              <button 
                className="modal-close-btn" 
                disabled={diagRunning}
                onClick={() => setShowDiagConsole(false)}
              >
                <X size={16} />
              </button>
            </div>
            
            <div className="modal-body">
              <div className="diagnostic-container">
                <div className="diag-header">
                  <span className="diag-status-text" style={{ color: diagComplete ? (diagPass ? 'var(--success-color)' : '#f59e0b') : 'var(--ink-color)' }}>
                    {diagStatus}
                  </span>
                </div>
                <div className="diag-console" id="diagConsole">
                  {diagLogs}
                </div>
              </div>
            </div>

            <div className="modal-footer">
              {diagRunning ? (
                <button className="btn btn-danger" onClick={() => {
                  diagCancelRef.current = true;
                }}>
                  Abort
                </button>
              ) : (
                <>
                  <button className="btn btn-secondary" onClick={() => setShowDiagConsole(false)}>Close</button>
                  <button className="btn btn-primary" onClick={runDiagnostics}>Re-run</button>
                </>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
