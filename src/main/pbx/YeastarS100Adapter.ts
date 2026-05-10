import { PbxFeatureCapability } from "../../shared/pbx";
import { BasePbxAdapter } from "./PbxAdapter";

const capabilities: PbxFeatureCapability[] = [
  { id: "originate", label: "Click-to-call", path: "vendor-api", available: true, notes: "Use Yeastar CTI/API where enabled." },
  { id: "presence", label: "Presence/BLF", path: "vendor-api", available: true, notes: "Requires S-Series API/Linkus-compatible state source." },
  { id: "callHistory", label: "Call logs/CDR", path: "vendor-api", available: true, notes: "Read from CDR integration endpoint or export source." },
  { id: "recordings", label: "Recordings", path: "vendor-api", available: true, notes: "Depends on recording permissions and storage mode." },
  { id: "voicemail", label: "Voicemail", path: "feature-code", available: true, notes: "Feature-code fallback works even before API playback is added." },
  { id: "queues", label: "Queues", path: "vendor-api", available: true, notes: "Call center app/API or supported integration required." },
  { id: "agentPause", label: "Queue pause", path: "vendor-api", available: true, notes: "Map to queue-agent controls when available." },
  { id: "conference", label: "Conference", path: "feature-code", available: true, notes: "PBX conference rooms and in-call SIP controls." },
  { id: "callPark", label: "Call park", path: "feature-code", available: true, notes: "Uses configured S-Series parking feature codes." },
  { id: "pickup", label: "Call pickup", path: "feature-code", available: true, notes: "Group and directed pickup via configured codes." },
  { id: "dnd", label: "DND", path: "feature-code", available: true, notes: "Feature-code fallback; API state sync preferred." },
  { id: "forwarding", label: "Call forwarding", path: "feature-code", available: true, notes: "Feature-code fallback; API state sync preferred." },
  { id: "contacts", label: "Corporate directory", path: "vendor-api", available: true, notes: "Directory or Linkus source." },
  { id: "provisioning", label: "Provisioning", path: "vendor-api", available: true, notes: "Admin profile support for managed deployments." },
  { id: "crmScreenPop", label: "CRM screen pop", path: "vendor-api", available: true, notes: "Use caller ID events plus CRM lookup." }
];

export class YeastarS100Adapter extends BasePbxAdapter {
  capabilities() {
    return capabilities;
  }

  async originateCall(destination: string) {
    await this.postJson("/api/cti/originate", {
      extension: this.config.extension,
      destination
    });
  }

  async setDnd(enabled: boolean) {
    await this.postJson("/api/cti/dnd", {
      extension: this.config.extension,
      enabled
    });
  }

  async setForwarding(destination: string | null) {
    await this.postJson("/api/cti/forwarding", {
      extension: this.config.extension,
      destination
    });
  }

  async pauseQueueAgent(queueId: string, paused: boolean) {
    await this.postJson("/api/cti/queues/agent-pause", {
      extension: this.config.extension,
      queueId,
      paused
    });
  }
}
