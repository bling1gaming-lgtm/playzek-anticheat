/**
 * Playzek Guard — generate-hashes.js
 *
 * Rulează ACEST script o singură dată, pe o instalare CS 1.6 CURATĂ,
 * fără nicio modificare — proaspăt reinstalată sau verificată prin Steam.
 * Generează game-file-hashes.json cu hash-urile fișierelor critice.
 *
 * Folosire:
 *   node tools/generate-hashes.js "C:\...\Half-Life\cstrike"
 */

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const gamePath = process.argv[2];
if (!gamePath) {
  console.error("Folosire: node generate-hashes.js <cale_către_folderul_cstrike>");
  process.exit(1);
}

// Extensii și foldere relevante pentru integritate — modele, sunete, sprite-uri.
// Nu includem .cfg sau demo-uri, care se schimbă normal.
const RELEVANT_EXT = [".mdl", ".wav", ".spr", ".wad"];
const RELEVANT_FOLDERS = ["models", "sound", "sprites"];

function sha256(filePath) {
  const data = fs.readFileSync(filePath);
  return crypto.createHash("sha256").update(data).digest("hex");
}

function walk(dir, baseDir, results) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(fullPath, baseDir, results);
    } else if (RELEVANT_EXT.includes(path.extname(entry.name).toLowerCase())) {
      const relative = path.relative(baseDir, fullPath);
      results[relative] = sha256(fullPath);
    }
  }
}

const results = {};
for (const folder of RELEVANT_FOLDERS) {
  const folderPath = path.join(gamePath, folder);
  if (fs.existsSync(folderPath)) {
    walk(folderPath, gamePath, results);
  }
}

const output = {
  _comment: `Generat din: ${gamePath} la ${new Date().toISOString()}`,
  files: results,
};

fs.writeFileSync(
  path.join(__dirname, "..", "src", "game-file-hashes.json"),
  JSON.stringify(output, null, 2)
);

console.log(`Gata — ${Object.keys(results).length} fișiere verificate și salvate în src/game-file-hashes.json`);
