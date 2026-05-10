import {
  Inviter,
  Registerer,
  Session,
  SessionState,
  UserAgent,
  UserAgentOptions
} from "sip.js";

export type SipAccount = {
  displayName: string;
  sipUri: string;
  password: string;
  websocketServer: string;
};

export type SipStatus = "offline" | "connecting" | "registered" | "calling" | "in-call" | "error";

type Listener = (status: SipStatus, message?: string) => void;

export class SipClient {
  private userAgent?: UserAgent;
  private registerer?: Registerer;
  private session?: Session;
  private listeners = new Set<Listener>();

  onStatus(listener: Listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  async connect(account: SipAccount) {
    this.emit("connecting");
    const uri = UserAgent.makeURI(account.sipUri);

    if (!uri) {
      this.emit("error", "SIP URI is invalid.");
      return;
    }

    const options: UserAgentOptions = {
      uri,
      displayName: account.displayName,
      authorizationUsername: uri.user,
      authorizationPassword: account.password,
      transportOptions: {
        server: account.websocketServer
      }
    };

    this.userAgent = new UserAgent(options);
    this.registerer = new Registerer(this.userAgent);

    await this.userAgent.start();
    await this.registerer.register();
    this.emit("registered");
  }

  async call(target: string) {
    if (!this.userAgent) {
      this.emit("error", "Connect your SIP account first.");
      return;
    }

    const targetUri = UserAgent.makeURI(target);
    if (!targetUri) {
      this.emit("error", "Destination SIP URI is invalid.");
      return;
    }

    const inviter = new Inviter(this.userAgent, targetUri);
    this.session = inviter;
    this.bindSession(inviter);
    this.emit("calling");
    await inviter.invite();
  }

  async hangup() {
    if (!this.session) {
      return;
    }

    if (this.session.state === SessionState.Established) {
      await this.session.bye();
    } else {
      await this.session.dispose();
    }

    this.session = undefined;
    this.emit("registered");
  }

  async disconnect() {
    await this.registerer?.unregister();
    await this.userAgent?.stop();
    this.registerer = undefined;
    this.userAgent = undefined;
    this.session = undefined;
    this.emit("offline");
  }

  private bindSession(session: Session) {
    session.stateChange.addListener((state) => {
      if (state === SessionState.Established) {
        this.emit("in-call");
      }
      if (state === SessionState.Terminated) {
        this.session = undefined;
        this.emit(this.userAgent ? "registered" : "offline");
      }
    });
  }

  private emit(status: SipStatus, message?: string) {
    for (const listener of this.listeners) {
      listener(status, message);
    }
  }
}
