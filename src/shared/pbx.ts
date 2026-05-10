export type PbxVendor = "yeastar-s100" | "freepbx";

export type PbxConnectorConfig = {
  vendor: PbxVendor;
  baseUrl: string;
  username: string;
  password: string;
  extension: string;
  amiHost?: string;
  amiPort?: number;
  ariBaseUrl?: string;
  ariUsername?: string;
  ariPassword?: string;
};

export type PbxExtensionState = "available" | "ringing" | "busy" | "offline" | "dnd" | "unknown";

export type PbxExtension = {
  number: string;
  name: string;
  state: PbxExtensionState;
  department?: string;
};

export type PbxCallRecord = {
  id: string;
  direction: "inbound" | "outbound" | "missed";
  remoteNumber: string;
  startedAt: string;
  durationSeconds: number;
  recordingId?: string;
};

export type PbxVoicemail = {
  id: string;
  from: string;
  receivedAt: string;
  durationSeconds: number;
  unread: boolean;
};

export type PbxQueue = {
  id: string;
  name: string;
  waitingCalls: number;
  loggedInAgents: number;
  pausedAgents: number;
};

export type PbxFeatureId =
  | "originate"
  | "answer"
  | "hangup"
  | "hold"
  | "blindTransfer"
  | "attendedTransfer"
  | "dtmf"
  | "conference"
  | "callPark"
  | "pickup"
  | "dnd"
  | "forwarding"
  | "voicemail"
  | "recordings"
  | "callHistory"
  | "contacts"
  | "presence"
  | "queues"
  | "agentPause"
  | "ringGroups"
  | "ivr"
  | "routes"
  | "trunks"
  | "provisioning"
  | "crmScreenPop";

export type PbxFeatureCapability = {
  id: PbxFeatureId;
  label: string;
  path: "sip" | "feature-code" | "ami" | "ari" | "vendor-api" | "manual-config";
  available: boolean;
  notes: string;
};

export type PbxSnapshot = {
  extensions: PbxExtension[];
  calls: PbxCallRecord[];
  voicemails: PbxVoicemail[];
  queues: PbxQueue[];
};
