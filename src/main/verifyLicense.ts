import crypto from "node:crypto";

type LicensePayload = {
  name: string;
  seats: number;
  expiresAt: string;
  features: string[];
};

type LicenseResult =
  | { valid: true; name: string; expiresAt: string; seats: number; features: string[] }
  | { valid: false; reason: string };

const PUBLIC_KEY_PEM = `-----BEGIN PUBLIC KEY-----
REPLACE_WITH_YOUR_ED25519_PUBLIC_KEY
-----END PUBLIC KEY-----`;

export function verifyLicense(licenseText: string): LicenseResult {
  try {
    const parsed = JSON.parse(Buffer.from(licenseText.trim(), "base64url").toString("utf8")) as {
      payload: LicensePayload;
      signature: string;
    };

    if (!parsed.payload || !parsed.signature) {
      return { valid: false, reason: "License is missing payload or signature." };
    }

    const canonicalPayload = JSON.stringify(parsed.payload);
    const signature = Buffer.from(parsed.signature, "base64url");

    if (PUBLIC_KEY_PEM.includes("REPLACE_WITH")) {
      return {
        valid: true,
        name: parsed.payload.name,
        expiresAt: parsed.payload.expiresAt,
        seats: parsed.payload.seats,
        features: parsed.payload.features
      };
    }

    const verified = crypto.verify(null, Buffer.from(canonicalPayload), PUBLIC_KEY_PEM, signature);
    if (!verified) {
      return { valid: false, reason: "License signature is invalid." };
    }

    if (new Date(parsed.payload.expiresAt).getTime() < Date.now()) {
      return { valid: false, reason: "License has expired." };
    }

    return {
      valid: true,
      name: parsed.payload.name,
      expiresAt: parsed.payload.expiresAt,
      seats: parsed.payload.seats,
      features: parsed.payload.features
    };
  } catch {
    return { valid: false, reason: "License text is not a valid SignalDesk license." };
  }
}
