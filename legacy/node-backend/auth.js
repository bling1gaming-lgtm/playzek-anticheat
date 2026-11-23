/**
 * Playzek Guard — auth.js
 * Autentificare reală: parole hash-uite cu bcrypt, token-uri de
 * sesiune JWT semnate, expirare și revocare server-side.
 */

const bcrypt = require("bcryptjs");
const jwt = require("jsonwebtoken");
const crypto = require("crypto");
const db = require("./db");

const JWT_SECRET = process.env.PLAYZEK_JWT_SECRET || crypto.randomBytes(32).toString("hex");
const SESSION_TTL_HOURS = 6;

if (!process.env.PLAYZEK_JWT_SECRET) {
  console.warn(
    "[playzek] PLAYZEK_JWT_SECRET nu e setat — folosesc un secret generat random la pornire " +
    "(token-urile vechi nu vor mai fi valide după un restart). Setează variabila de mediu în producție."
  );
}

function register(username, password, steamId = null) {
  const existing = db.prepare("SELECT id FROM users WHERE username = ?").get(username);
  if (existing) throw new Error("username_taken");

  const passwordHash = bcrypt.hashSync(password, 12);
  const result = db
    .prepare("INSERT INTO users (username, password_hash, steam_id) VALUES (?, ?, ?)")
    .run(username, passwordHash, steamId);

  return { userId: result.lastInsertRowid, username };
}

function login(username, password) {
  const user = db.prepare("SELECT * FROM users WHERE username = ?").get(username);
  if (!user) throw new Error("invalid_credentials");
  if (user.status !== "active") throw new Error("account_suspended");

  const ok = bcrypt.compareSync(password, user.password_hash);
  if (!ok) throw new Error("invalid_credentials");

  const loginToken = jwt.sign(
    { sub: user.id, username: user.username, type: "login" },
    JWT_SECRET,
    { expiresIn: "15m" }
  );

  return { userId: user.id, username: user.username, loginToken };
}

function registerDevice(userId, deviceFingerprint, hostname) {
  db.prepare(
    `INSERT INTO registered_devices (user_id, device_fingerprint, hostname)
     VALUES (?, ?, ?)
     ON CONFLICT(user_id, device_fingerprint)
     DO UPDATE SET last_seen_at = datetime('now'), hostname = excluded.hostname`
  ).run(userId, deviceFingerprint, hostname);

  return db
    .prepare("SELECT id FROM registered_devices WHERE user_id = ? AND device_fingerprint = ?")
    .get(userId, deviceFingerprint);
}

function startAntiCheatSession(userId, deviceId, matchId) {
  const sessionToken = crypto.randomBytes(24).toString("hex");
  const expiresAt = new Date(Date.now() + SESSION_TTL_HOURS * 3600 * 1000).toISOString();

  db.prepare(
    `INSERT INTO anti_cheat_sessions (session_token, user_id, device_id, match_id, expires_at)
     VALUES (?, ?, ?, ?, ?)`
  ).run(sessionToken, userId, deviceId, matchId || null, expiresAt);

  return { sessionToken, expiresAt };
}

/**
 * Validare SERVER-SIDE reală a unei sesiuni anti-cheat — nu ne bazăm
 * doar pe un indicator din client. Verificăm existența, statusul și
 * expirarea în baza de date la fiecare cerere.
 */
function validateAntiCheatSession(sessionToken) {
  const session = db
    .prepare("SELECT * FROM anti_cheat_sessions WHERE session_token = ?")
    .get(sessionToken);

  if (!session) return { valid: false, reason: "not_found" };
  if (session.status === "revoked") return { valid: false, reason: "revoked" };
  if (new Date(session.expires_at).getTime() < Date.now()) {
    return { valid: false, reason: "expired" };
  }

  return { valid: true, session };
}

function revokeSession(sessionToken) {
  db.prepare(
    "UPDATE anti_cheat_sessions SET status = 'revoked', revoked_at = datetime('now') WHERE session_token = ?"
  ).run(sessionToken);
}

module.exports = {
  register,
  login,
  registerDevice,
  startAntiCheatSession,
  validateAntiCheatSession,
  revokeSession,
};
