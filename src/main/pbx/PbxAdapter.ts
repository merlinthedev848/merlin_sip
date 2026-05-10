import {
  PbxCallRecord,
  PbxConnectorConfig,
  PbxExtension,
  PbxFeatureCapability,
  PbxQueue,
  PbxSnapshot,
  PbxVoicemail
} from "../../shared/pbx";

export interface PbxAdapter {
  readonly config: PbxConnectorConfig;
  capabilities(): PbxFeatureCapability[];
  snapshot(): Promise<PbxSnapshot>;
  listExtensions(): Promise<PbxExtension[]>;
  listCallHistory(): Promise<PbxCallRecord[]>;
  listVoicemails(): Promise<PbxVoicemail[]>;
  listQueues(): Promise<PbxQueue[]>;
  setDnd(enabled: boolean): Promise<void>;
  setForwarding(destination: string | null): Promise<void>;
  originateCall(destination: string): Promise<void>;
  pauseQueueAgent(queueId: string, paused: boolean): Promise<void>;
}

export abstract class BasePbxAdapter implements PbxAdapter {
  constructor(readonly config: PbxConnectorConfig) {}

  abstract capabilities(): PbxFeatureCapability[];

  async snapshot(): Promise<PbxSnapshot> {
    const [extensions, calls, voicemails, queues] = await Promise.all([
      this.listExtensions(),
      this.listCallHistory(),
      this.listVoicemails(),
      this.listQueues()
    ]);

    return { extensions, calls, voicemails, queues };
  }

  protected async getJson<T>(path: string): Promise<T> {
    if (!this.config.baseUrl) {
      throw new Error("PBX base URL is not configured.");
    }

    const response = await fetch(new URL(path, this.config.baseUrl), {
      headers: this.authHeaders()
    });

    if (!response.ok) {
      throw new Error(`PBX request failed with ${response.status}.`);
    }

    return response.json() as Promise<T>;
  }

  protected async postJson<T>(path: string, body: unknown): Promise<T> {
    if (!this.config.baseUrl) {
      throw new Error("PBX base URL is not configured.");
    }

    const response = await fetch(new URL(path, this.config.baseUrl), {
      method: "POST",
      headers: {
        ...this.authHeaders(),
        "content-type": "application/json"
      },
      body: JSON.stringify(body)
    });

    if (!response.ok) {
      throw new Error(`PBX request failed with ${response.status}.`);
    }

    return response.json() as Promise<T>;
  }

  protected featureCode(code: string) {
    return `${code}${this.config.extension}`;
  }

  private authHeaders() {
    const token = Buffer.from(`${this.config.username}:${this.config.password}`).toString("base64");
    return { authorization: `Basic ${token}` };
  }

  listExtensions(): Promise<PbxExtension[]> {
    return Promise.resolve([]);
  }

  listCallHistory(): Promise<PbxCallRecord[]> {
    return Promise.resolve([]);
  }

  listVoicemails(): Promise<PbxVoicemail[]> {
    return Promise.resolve([]);
  }

  listQueues(): Promise<PbxQueue[]> {
    return Promise.resolve([]);
  }

  setDnd(_enabled: boolean): Promise<void> {
    return Promise.reject(new Error("DND integration is not configured for this PBX yet."));
  }

  setForwarding(_destination: string | null): Promise<void> {
    return Promise.reject(new Error("Forwarding integration is not configured for this PBX yet."));
  }

  originateCall(_destination: string): Promise<void> {
    return Promise.reject(new Error("Originate integration is not configured for this PBX yet."));
  }

  pauseQueueAgent(_queueId: string, _paused: boolean): Promise<void> {
    return Promise.reject(new Error("Queue integration is not configured for this PBX yet."));
  }
}
