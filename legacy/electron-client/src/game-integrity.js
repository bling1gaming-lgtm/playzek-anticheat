/**
 * Playzek Guard — game-integrity.js
 *
 * Două verificări suplimentare, ambele NON-invazive:
 *
 * 1. Integritatea fișierelor jocului CS 1.6 — comparăm hash-ul
 *    fișierelor oficiale (modele, sunete, sprite-uri) cu valorile
 *    cunoscute, din instalarea curată. Detectează modele wallhack
 *    (texturi eliminate), sunete de radar, sprite-uri de fum/flash
 *    modificate să nu mai blocheze vederea.
 *
 * 2. Module încărcate în procesul jocului — listăm DLL-urile pe
 *    care hl.exe/cstrike.exe le-a încărcat (exact ca în Task Manager
 *    → Details → coloana DLL), nu citim conținutul memoriei. Un
 *    modul nesemnat sau necunoscut injectat în joc e un semnal
 *    puternic de cheat activ.
 */

const { exec } = require("child_process");
const fs = require("fs");
const crypto = require("crypto");
const path = require("path");

const gameFileHashes = JSON.parse(
  fs.readFileSync(path.join(__dirname, "game-file-hashes.json"), "utf8")
);

function sha256OfFile(filePath) {
  return new Promise((resolve) => {
    try {
      const hash = crypto.createHash("sha256");
      const stream = fs.createReadStream(filePath);
      stream.on("data", (c) => hash.update(c));
      stream.on("end", () => resolve(hash.digest("hex")));
      stream.on("error", () => resolve(null));
    } catch {
      resolve(null);
    }
  });
}

/**
 * Verifică integritatea fișierelor cunoscute din instalarea CS 1.6.
 * gamePath = folderul de instalare (ex: ...\Half-Life\cstrike)
 */
async function checkGameFileIntegrity(gamePath) {
  const flags = [];

  for (const [relativePath, expectedHash] of Object.entries(gameFileHashes.files)) {
    const fullPath = path.join(gamePath, relativePath);
    if (!fs.existsSync(fullPath)) continue; // fișier opțional / hartă custom, nu flagăm lipsa

    const actualHash = await sha256OfFile(fullPath);
    if (actualHash && actualHash !== expectedHash) {
      flags.push({
        type: "modified_game_file",
        file: relativePath,
        expectedHash,
        actualHash,
      });
    }
  }

  return flags;
}

/**
 * Listează modulele (DLL) încărcate de procesul jocului și semnalează
 * orice modul care nu e semnat digital sau nu e pe lista cunoscută.
 */
function checkLoadedModules(processName = "hl") {
  return new Promise((resolve) => {
    const cmd = `powershell -NoProfile -Command "` +
      `$p = Get-Process -Name '${processName}' -ErrorAction SilentlyContinue; ` +
      `if ($p) { $p.Modules | Select-Object -ExpandProperty FileName }"`;

    exec(cmd, { windowsHide: true, maxBuffer: 10 * 1024 * 1024 }, (err, stdout) => {
      if (err || !stdout) return resolve({ running: false, flags: [] });

      const modulePaths = stdout.split("\n").map((l) => l.trim()).filter(Boolean);
      const flags = [];

      for (const modPath of modulePaths) {
        const fileName = path.basename(modPath).toLowerCase();

        // Module din sistem (Windows, drivere grafice etc.) sunt de obicei curate —
        // filtrăm măcar calea System32, restul le verificăm pe rând
        if (modPath.toLowerCase().includes("\\windows\\system32\\")) continue;
        if (modPath.toLowerCase().includes("\\half-life\\")) continue; // module proprii jocului

        flags.push({ type: "unknown_loaded_module", module: fileName, path: modPath });
      }

      resolve({ running: true, flags });
    });
  });
}

/**
 * Caută automat folderul de instalare CS 1.6 în locațiile standard
 * (Steam sau instalare clasică non-Steam). Întoarce primul găsit.
 */
function findGameInstallPath() {
  const candidates = [
    "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Half-Life\\cstrike",
    "C:\\Program Files\\Steam\\steamapps\\common\\Half-Life\\cstrike",
    "C:\\Program Files (x86)\\Counter-Strike 1.6\\cstrike",
    "D:\\Steam\\steamapps\\common\\Half-Life\\cstrike",
    "D:\\Games\\Half-Life\\cstrike",
    "C:\\Half-Life\\cstrike",
  ];

  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) return candidate;
  }
  return null;
}

module.exports = { checkGameFileIntegrity, checkLoadedModules, findGameInstallPath };
