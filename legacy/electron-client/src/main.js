const { app, BrowserWindow, Tray, Menu, ipcMain, desktopCapturer } = require("electron");
const path = require("path");
const { runScan } = require("./scanner");
const { PlayzekGuardConnection } = require("./connection");

let mainWindow;
let tray;
let connection = null;

// Fără meniul implicit Electron (File/Edit/View/Window/Help) — arată neprofesionist
Menu.setApplicationMenu(null);

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 440,
    height: 700,
    resizable: false,
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
    },
  });
  mainWindow.setMenu(null);
  mainWindow.loadFile(path.join(__dirname, "ui", "index.html"));
}

function createTray() {
  tray = new Tray(path.join(__dirname, "..", "assets", "icon.png"));
  tray.setToolTip("Playzek Guard — activ");
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: "Deschide Playzek Guard", click: () => mainWindow.show() },
      { label: "Ieșire", click: () => app.quit() },
    ])
  );
}

app.whenReady().then(() => {
  createWindow();
  try { createTray(); } catch (e) { console.log("tray skip:", e.message); }
});

app.on("window-all-closed", (e) => {
  e.preventDefault();
});

async function captureScreenshot() {
  const sources = await desktopCapturer.getSources({
    types: ["screen"],
    thumbnailSize: { width: 1280, height: 720 },
  });
  if (!sources.length) return null;
  return sources[0].thumbnail.toDataURL();
}

ipcMain.handle("guard:scan-now", async (_e, opts) => runScan(opts || {}));

ipcMain.handle("guard:screenshot-now", async () => captureScreenshot());

ipcMain.handle("guard:start-match", async (_e, { serverUrl, authToken, matchId }) => {
  if (connection) connection.disconnect();
  connection = new PlayzekGuardConnection({
    serverUrl, authToken, matchId,
    captureScreenshot,
    onStatus: (status, detail) => {
      mainWindow.webContents.send("guard:status", { status, detail });
    },
  });
  connection.connect();
  return { ok: true };
});

ipcMain.handle("guard:stop-match", async () => {
  if (connection) { connection.disconnect(); connection = null; }
  return { ok: true };
});
