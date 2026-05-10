import { contextBridge, ipcRenderer } from "electron";

contextBridge.exposeInMainWorld("signalDesk", {
  getLicense: () => ipcRenderer.invoke("license:get"),
  activateLicense: (licenseText: string) => ipcRenderer.invoke("license:activate", licenseText)
});
