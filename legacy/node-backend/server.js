/**
 * Playzek Guard — server.js
 * Backend real: autentificare, înregistrare dispozitive, sesiuni
 * anti-cheat validate server-side, plus analiza meciurilor (kills
 * primite de la pluginul AMXX) — totul pe bază de date SQLite
 * persistentă (db.js), nu mai pe stare in-memory.
 *
 * Rulare: node server.js
 * Pornește pe portul 3001 (PLAYZEK_ANTICHEAT_PORT în .env dacă vrei altul).
 */

const express = require("express");
const db = require("./db");
const auth = require("./auth");
const { MatchAnalyzer } = require("./stats_analyzer");

const app = express();
app.use(express.json());

const matches = new Map(); // matchId -> MatchAnalyzer (în memorie doar cât durează meciul curent)

function getAnalyzer(matchId) {
  if (!matches.has(matchId)) matches.set(matchId, new MatchAnalyzer(matchId));
  return matches.get(matchId);
}

// ---------- Autentificare ----------

app.post("/api/auth/register", (req, res) => {
  const { username, password, steamId } = req.body || {};
  if (!username || !password || password.length < 8) {
    return res.status(400).json({ success: false, message: "username_si_parola_minim_8_caractere" });
  }
  try {
    const user = auth.register(username, password, steamId);
    res.json({ success: true, data: user });
  } catch (e) {
    const status = e.message === "username_taken" ? 409 : 500;
    res.status(status).json({ success: false, message: e.message });
  }
});

app.post("/api/auth/login", (req, res) => {
  const { username, password } = req.body || {};
  if (!username || !password) {
    return res.status(400).json({ success: false, message: "lipsesc_credentialele" });
  }
  try {
    const result = auth.login(username, password);
    res.json({ success: true, data: result });
  } catch (e) {
    res.status(401).json({ success: false, message: e.message });
  }
});

// ---------- Înregistrare dispozitiv ----------

app.post("/api/devices/register", (req, res) => {
  const { userId, deviceFingerprint, hostname } = req.body || {};
  if (!userId || !deviceFingerprint) {
    return res.status(400).json({ success: false, message: "lipsesc_userId_sau_deviceFingerprint" });
  }
  const device = auth.registerDevice(userId, deviceFingerprint, hostname);
  res.json({ success: true, data: device });
});

// ---------- Sesiuni anti-cheat (validare SERVER-SIDE reală) ----------

app.post("/api/anticheat/session/start", (req, res) => {
  const { userId, deviceId, matchId } = req.body || {};
  if (!userId || !deviceId) {
    return res.status(400).json({ success: false, message: "lipsesc_userId_sau_deviceId" });
  }
  const session = auth.startAntiCheatSession(userId, deviceId, matchId);
  res.json({ success: true, data: session });
});

// Apelat de AMXX (server-ul de joc) ca să valideze independent sesiunea
// unui jucător — nu se bazează pe ce zice clientul Windows.
app.get("/api/anticheat/session/validate", (req, res) => {
  const { token } = req.query;
  if (!token) return res.status(400).json({ success: false, message: "lipseste_token" });

  const result = auth.validateAntiCheatSession(token);
  res.json({ success: true, data: result });
});

app.post("/api/anticheat/session/revoke", (req, res) => {
  const { token } = req.body || {};
  if (!token) return res.status(400).json({ success: false, message: "lipseste_token" });
  auth.revokeSession(token);
  res.json({ success: true });
});

// ---------- Rapoarte de integritate (din scanările clientului Electron) ----------

app.post("/api/anticheat/integrity-report", (req, res) => {
  const { sessionToken, scannedAt, processCount, clean, flags } = req.body || {};
  const { valid, session } = auth.validateAntiCheatSession(sessionToken || "");
  if (!valid) return res.status(401).json({ success: false, message: "sesiune_invalida" });

  db.prepare(
    `INSERT INTO integrity_reports (session_id, scanned_at, process_count, clean, flags_json)
     VALUES (?, ?, ?, ?, ?)`
  ).run(session.id, scannedAt, processCount || 0, clean ? 1 : 0, JSON.stringify(flags || []));

  res.json({ success: true });
});

// ---------- Analiza meciurilor (kills de la AMXX) ----------

app.post("/api/anticheat/events", (req, res) => {
  const event = req.body;

  if (!event || !event.matchId || !event.attackerSteamId) {
    return res.status(400).json({ success: false, message: "invalid_event" });
  }

  const analyzer = getAnalyzer(event.matchId);
  analyzer.ingest(event);

  // Dacă analiza marchează suspiciune critică, scriem un eveniment de detecție
  // persistent în baza de date — nu doar în memoria procesului curent.
  const report = analyzer.report();
  const attackerStats = report.players.find((p) => p.steamId === event.attackerSteamId);
  if (attackerStats && attackerStats.severity === "critical") {
    db.prepare(
      `INSERT INTO detection_events (match_id, player_steam_id, severity, reasons_json)
       VALUES (?, ?, ?, ?)`
    ).run(event.matchId, event.attackerSteamId, "critical", JSON.stringify(attackerStats.reasons || []));
  }

  res.json({ success: true });
});

app.get("/api/anticheat/matches/:matchId/report", (req, res) => {
  const analyzer = matches.get(req.params.matchId);
  if (!analyzer) {
    return res.status(404).json({ success: false, message: "match_not_found" });
  }
  res.json({ success: true, data: analyzer.report() });
});

app.get("/api/anticheat/matches", (req, res) => {
  res.json({ success: true, data: [...matches.keys()] });
});

// ---------- Panou admin: date reale pentru dashboard ----------

app.get("/api/admin/devices", (req, res) => {
  const rows = db.prepare(
    `SELECT rd.id, rd.hostname, rd.first_seen_at, rd.last_seen_at, u.username
     FROM registered_devices rd JOIN users u ON u.id = rd.user_id
     ORDER BY rd.last_seen_at DESC`
  ).all();
  res.json({ success: true, data: rows });
});

app.get("/api/admin/sessions", (req, res) => {
  const rows = db.prepare(
    `SELECT s.id, s.session_token, s.status, s.created_at, s.expires_at, s.match_id, u.username
     FROM anti_cheat_sessions s JOIN users u ON u.id = s.user_id
     ORDER BY s.created_at DESC LIMIT 100`
  ).all();
  res.json({ success: true, data: rows });
});

app.get("/api/admin/detection-events", (req, res) => {
  const rows = db.prepare(
    `SELECT * FROM detection_events ORDER BY created_at DESC LIMIT 100`
  ).all();
  res.json({
    success: true,
    data: rows.map((r) => ({ ...r, reasons_json: JSON.parse(r.reasons_json) })),
  });
});

const PORT = process.env.PLAYZEK_ANTICHEAT_PORT || 3001;
app.listen(PORT, () => {
  console.log(`Playzek Guard — backend pornit pe portul ${PORT}`);
});
