const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("playzekGuard", {
  scanNow: () => ipcRenderer.invoke("guard:scan-now"),
  startMatch: (payload) => ipcRenderer.invoke("guard:start-match", payload),
  stopMatch: () => ipcRenderer.invoke("guard:stop-match"),
  onStatus: (callback) =>
    ipcRenderer.on("guard:status", (_event, data) => callback(data)),
});
