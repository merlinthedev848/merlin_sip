/// <reference types="vite/client" />

type LicenseActivationResult =
  | { valid: true; name: string; expiresAt: string; seats: number; features: string[] }
  | { valid: false; reason: string };

interface Window {
  merlinSip: {
    getLicense: () => Promise<{ key?: string; name?: string; expiresAt?: string }>;
    activateLicense: (licenseText: string) => Promise<LicenseActivationResult>;
  };
}
