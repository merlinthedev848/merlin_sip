import {
  Activity,
  Archive,
  BellRing,
  BookUser,
  Bot,
  Cable,
  CassetteTape,
  Contact,
  DoorOpen,
  FileAudio,
  GitBranch,
  Headphones,
  History,
  ListTree,
  MessagesSquare,
  MicOff,
  PhoneForwarded,
  RadioTower,
  Repeat2,
  Shield,
  Smartphone,
  UsersRound,
  Voicemail
} from "lucide-react";

export type PbxProfileId = "yeastar-s100" | "freepbx";

export type PbxFeature = {
  title: string;
  detail: string;
  status: "sip" | "api" | "planned";
  icon: typeof PhoneForwarded;
};

export const pbxProfiles: Record<PbxProfileId, { name: string; transport: string; notes: string }> = {
  "yeastar-s100": {
    name: "Yeastar S100",
    transport: "SIP extension, Linkus/CTI/API where available",
    notes: "S100 is end-of-sale, so the app should support S-Series while staying ready for P-Series migration."
  },
  freepbx: {
    name: "FreePBX",
    transport: "PJSIP/SIP plus Asterisk AMI/ARI and UCP-style companion APIs",
    notes: "FreePBX feature depth comes from Asterisk events, dialplan feature codes, and module-specific APIs."
  }
};

export const pbxFeatures: PbxFeature[] = [
  { title: "Voice calls", detail: "Inbound, outbound, answer, reject, hang up.", status: "sip", icon: Headphones },
  { title: "Hold and resume", detail: "SIP re-INVITE hold with clear visual call state.", status: "sip", icon: MicOff },
  { title: "DTMF keypad", detail: "In-call tones for IVR, voicemail, and feature codes.", status: "sip", icon: RadioTower },
  { title: "Blind transfer", detail: "Transfer directly to an extension or outside number.", status: "sip", icon: PhoneForwarded },
  { title: "Attended transfer", detail: "Consult first, then complete or cancel the transfer.", status: "sip", icon: Repeat2 },
  { title: "Conference", detail: "PBX-backed conference rooms and ad-hoc merge controls.", status: "api", icon: UsersRound },
  { title: "Voicemail", detail: "Unread counts, playback, delete, and callback.", status: "api", icon: Voicemail },
  { title: "Call history", detail: "Missed, inbound, outbound, duration, disposition.", status: "api", icon: History },
  { title: "Recordings", detail: "List, playback, download, retention indicators.", status: "api", icon: FileAudio },
  { title: "Queues", detail: "Agent login, pause, queue presence, waiting callers.", status: "api", icon: ListTree },
  { title: "Presence and BLF", detail: "Extension state, ringing, busy, offline, DND.", status: "api", icon: Activity },
  { title: "Contacts", detail: "PBX directory, local contacts, CRM search.", status: "api", icon: BookUser },
  { title: "Messaging", detail: "Team chat when PBX/provider exposes compatible messaging.", status: "planned", icon: MessagesSquare },
  { title: "Call parking", detail: "Park, retrieve, and watch parking slots.", status: "api", icon: Archive },
  { title: "Pickup groups", detail: "One-tap group pickup and directed pickup.", status: "api", icon: BellRing },
  { title: "Call forwarding", detail: "Enable, disable, and show active forwarding rules.", status: "api", icon: GitBranch },
  { title: "DND", detail: "Toggle do-not-disturb with synced PBX state.", status: "api", icon: Shield },
  { title: "Mobile extension", detail: "Support paired mobile identity and one-number workflows.", status: "api", icon: Smartphone },
  { title: "Door phone", detail: "Preview, unlock, and call flows where hardware supports it.", status: "planned", icon: DoorOpen },
  { title: "Provisioning", detail: "Admin profiles, QR/mobile setup, and managed defaults.", status: "api", icon: Cable },
  { title: "AI notes", detail: "Optional call summaries and CRM-ready notes.", status: "planned", icon: Bot },
  { title: "Fax/media", detail: "Expose PBX media boxes where deployments use them.", status: "planned", icon: CassetteTape },
  { title: "CRM screen pop", detail: "Incoming call lookup and click-to-call from records.", status: "api", icon: Contact }
];
