import { app, BrowserWindow, ipcMain } from "electron";
import path from "node:path";
import Store from "electron-store";
import { verifyLicense } from "./verifyLicense";

const store = new Store<{
  licenseKey?: string;
  licenseName?: string;
  licenseExpiresAt?: string;
}>();

function createWindow() {
  const mainWindow = new BrowserWindow({
    width: 1260,
    height: 820,
    minWidth: 980,
    minHeight: 680,
    title: "Merlin SIP",
    backgroundColor: "#f5f7fb",
    webPreferences: {
      preload: path.join(__dirname, "../preload/preload.js"),
      contextIsolation: true,
      nodeIntegration: false
    }
  });

  if (!app.isPackaged) {
    mainWindow.loadURL("http://127.0.0.1:5173");
  } else {
    mainWindow.loadFile(path.join(__dirname, "../renderer/index.html"));
  }
}

app.whenReady().then(() => {
  ipcMain.handle("license:get", () => ({
    key: store.get("licenseKey"),
    name: store.get("licenseName"),
    expiresAt: store.get("licenseExpiresAt")
  }));

  ipcMain.handle("license:activate", async (_event, licenseText: string) => {
    const result = verifyLicense(licenseText);
    if (!result.valid) {
      return result;
    }

    store.set("licenseKey", licenseText);
    store.set("licenseName", result.name);
    store.set("licenseExpiresAt", result.expiresAt);
    return result;
  });

  createWindow();

  app.on("activate", () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow();
    }
  });
});

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") {
    app.quit();
  }
});
