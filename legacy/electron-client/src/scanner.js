/**
 * Playzek Guard — scanner.js
 * Scanare NON-invazivă: procese + titluri ferestre + hash de fișier +
 * integritate fișiere joc + module încărcate în procesul jocului.
 * Nu citim memoria altor procese și nu injectăm cod nicăieri.
 */

const { exec } = require("child_process");
const fs = require("fs");
const crypto = require("crypto");
const path = require("path");
const { checkGameFileIntegrity, checkLoadedModules, findGameInstallPath } = require("./game-integrity");

const signatures = JSON.parse(
  fs.readFileSync(path.join(__dirname, "known-signatures.json"), "utf8")
);
const knownHashes = JSON.parse(
  fs.readFileSync(path.join(__dirname, "known-hashes.json"), "utf8")
);

function listRunningProcesses() {
  return new Promise((resolve, reject) => {
    exec('tasklist /fo csv /v', { windowsHide: true, maxBuffer: 10 * 1024 * 1024 }, (err, stdout) => {
      if (err) return reject(err);
      const rows = stdout.split("\n").slice(1).filter(Boolean);
      const procs = rows.map((line) => {
        const cols = line.split('","').map((c) => c.replace(/"/g, "").trim());
        return { name: cols[0], pid: cols[1] };
      }).filter((p) => p.name);
      resolve(procs);
    });
  });
}

function listWindowTitles() {
  return new Promise((resolve, reject) => {
    const cmd =
      'powershell -NoProfile -Command "Get-Process | Where-Object { $_.MainWindowTitle -ne \'\' } | Select-Object -ExpandProperty MainWindowTitle"';
    exec(cmd, { windowsHide: true }, (err, stdout) => {
      if (err) return reject(err);
      resolve(stdout.split("\n").map((l) => l.trim()).filter(Boolean));
    });
  });
}

function getExecutablePath(pid) {
  return new Promise((resolve) => {
    const cmd = `powershell -NoProfile -Command "(Get-Process -Id ${pid} -ErrorAction SilentlyContinue).Path"`;
    exec(cmd, { windowsHide: true }, (err, stdout) => {
      resolve(err ? null : stdout.trim() || null);
    });
  });
}

function sha256OfFile(filePath) {
  return new Promise((resolve) => {
    try {
      const hash = crypto.createHash("sha256");
      const stream = fs.createReadStream(filePath);
      stream.on("data", (chunk) => hash.update(chunk));
      stream.on("end", () => resolve(hash.digest("hex")));
      stream.on("error", () => resolve(null));
    } catch {
      resolve(null);
    }
  });
}

async function checkFileHashes(processes) {
  const flags = [];
  const candidates = processes.slice(0, 60);

  for (const proc of candidates) {
    const exePath = await getExecutablePath(proc.pid);
    if (!exePath) continue;

    const hash = await sha256OfFile(exePath);
    if (hash && knownHashes.sha256.includes(hash)) {
      flags.push({ type: "file_hash", value: proc.name, path: exePath, hash });
    }
  }

  return flags;
}

async function runScan({ deepHashCheck = false, gamePath = null } = {}) {
  const [processes, titles] = await Promise.all([
    listRunningProcesses().catch(() => []),
    listWindowTitles().catch(() => []),
  ]);

  const flags = [];

  for (const proc of processes) {
    const lower = proc.name.toLowerCase();
    if (signatures.processNames.some((s) => lower === s.toLowerCase())) {
      flags.push({ type: "process", value: proc.name });
    }
  }

  for (const title of titles) {
    const lower = title.toLowerCase();
    for (const needle of signatures.windowTitleContains) {
      if (lower.includes(needle.toLowerCase())) {
        flags.push({ type: "window_title", value: title });
      }
    }
  }

  if (deepHashCheck) {
    const hashFlags = await checkFileHashes(processes);
    flags.push(...hashFlags);

    // Module încărcate în procesul jocului (hl.exe / cstrike.exe / cs16client.exe)
    for (const gameProc of ["hl", "cstrike", "cs16client"]) {
      const { running, flags: moduleFlags } = await checkLoadedModules(gameProc);
      if (running) flags.push(...moduleFlags);
    }

    // Integritatea fișierelor jocului — folosim calea dată explicit,
    // altfel încercăm să o detectăm automat în locațiile standard
    const resolvedGamePath = gamePath || findGameInstallPath();
    if (resolvedGamePath) {
      const integrityFlags = await checkGameFileIntegrity(resolvedGamePath);
      flags.push(...integrityFlags);
    }
  }

  return {
    scannedAt: new Date().toISOString(),
    processCount: processes.length,
    clean: flags.length === 0,
    flags,
  };
}

module.exports = { runScan };
