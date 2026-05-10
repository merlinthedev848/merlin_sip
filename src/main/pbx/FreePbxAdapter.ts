import { PbxFeatureCapability } from "../../shared/pbx";
import { BasePbxAdapter } from "./PbxAdapter";

const capabilities: PbxFeatureCapability[] = [
  { id: "originate", label: "Click-to-call", path: "ami", available: true, notes: "Use Asterisk AMI Originate or ARI channel create." },
  { id: "presence", label: "Presence/BLF", path: "ami", available: true, notes: "Subscribe to AMI device state and bridge events." },
  { id: "callHistory", label: "CDR", path: "manual-config", available: true, notes: "Read CDR database or configured reporting API." },
  { id: "recordings", label: "Recordings", path: "manual-config", available: true, notes: "Read monitor directory or recording module source." },
  { id: "voicemail", label: "Voicemail", path: "feature-code", available: true, notes: "Feature code now; voicemail API/storage later." },
  { id: "queues", label: "Queues", path: "ami", available: true, notes: "Use QueueStatus, QueueSummary, and queue events." },
  { id: "agentPause", label: "Queue pause", path: "ami", available: true, notes: "Use QueuePause for dynamic agents." },
  { id: "conference", label: "Conference", path: "ari", available: true, notes: "ConfBridge plus AMI/ARI events." },
  { id: "callPark", label: "Call park", path: "feature-code", available: true, notes: "Use configured parking lots and hints." },
  { id: "pickup", label: "Call pickup", path: "feature-code", available: true, notes: "Group pickup and directed pickup feature codes." },
  { id: "dnd", label: "DND", path: "feature-code", available: true, notes: "FreePBX feature code plus hint sync." },
  { id: "forwarding", label: "Call forwarding", path: "feature-code", available: true, notes: "FreePBX CF/CFB/CFU feature codes." },
  { id: "contacts", label: "Contacts", path: "manual-config", available: true, notes: "Userman/contact manager integration." },
  { id: "provisioning", label: "Provisioning", path: "manual-config", available: true, notes: "Endpoint Manager if licensed/configured." },
  { id: "crmScreenPop", label: "CRM screen pop", path: "ami", available: true, notes: "Caller ID events plus CRM lookup." }
];

export class FreePbxAdapter extends BasePbxAdapter {
  capabilities() {
    return capabilities;
  }

  async originateCall(destination: string) {
    await this.postJson("/ari/channels", {
      endpoint: `PJSIP/${this.config.extension}`,
      extension: destination,
      context: "from-internal",
      priority: 1
    });
  }

  async setDnd(enabled: boolean) {
    await this.postJson("/feature-code", {
      extension: this.config.extension,
      code: enabled ? "*78" : "*79"
    });
  }

  async setForwarding(destination: string | null) {
    await this.postJson("/feature-code", {
      extension: this.config.extension,
      code: destination ? `*72${destination}` : "*73"
    });
  }

  async pauseQueueAgent(queueId: string, paused: boolean) {
    await this.postJson("/ami/queue-pause", {
      interface: `PJSIP/${this.config.extension}`,
      queue: queueId,
      paused
    });
  }
}
