/**
 * Playzek Guard — db.js
 * Bază de date SQLite persistentă (fișier local .sqlite), nu mai e
 * in-memory. Schema minimă dar reală: utilizatori, dispozitive
 * înregistrate, sesiuni anti-cheat, rapoarte de integritate și
 * evenimente de detecție.
 */

const Database = require("better-sqlite3");
const path = require("path");

const DB_PATH = process.env.PLAYZEK_DB_PATH || path.join(__dirname, "playzek.sqlite");
const db = new Database(DB_PATH);

db.pragma("journal_mode = WAL");
db.pragma("foreign_keys = ON");

db.exec(`
  CREATE TABLE IF NOT EXISTS users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT UNIQUE NOT NULL,
    password_hash TEXT NOT NULL,
    steam_id TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    status TEXT NOT NULL DEFAULT 'active'
  );

  CREATE TABLE IF NOT EXISTS registered_devices (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES users(id),
    device_fingerprint TEXT NOT NULL,
    hostname TEXT,
    first_seen_at TEXT NOT NULL DEFAULT (datetime('now')),
    last_seen_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(user_id, device_fingerprint)
  );

  CREATE TABLE IF NOT EXISTS anti_cheat_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_token TEXT UNIQUE NOT NULL,
    user_id INTEGER NOT NULL REFERENCES users(id),
    device_id INTEGER NOT NULL REFERENCES registered_devices(id),
    match_id TEXT,
    status TEXT NOT NULL DEFAULT 'active',
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at TEXT NOT NULL,
    revoked_at TEXT
  );

  CREATE TABLE IF NOT EXISTS integrity_reports (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id INTEGER REFERENCES anti_cheat_sessions(id),
    scanned_at TEXT NOT NULL,
    process_count INTEGER,
    clean INTEGER NOT NULL,
    flags_json TEXT NOT NULL
  );

  CREATE TABLE IF NOT EXISTS detection_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    match_id TEXT NOT NULL,
    player_steam_id TEXT,
    severity TEXT NOT NULL,
    reasons_json TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    review_status TEXT NOT NULL DEFAULT 'pending'
  );

  CREATE TABLE IF NOT EXISTS bans (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER REFERENCES users(id),
    reason TEXT NOT NULL,
    evidence_event_id INTEGER REFERENCES detection_events(id),
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    is_permanent INTEGER NOT NULL DEFAULT 0,
    expires_at TEXT
  );
`);

module.exports = db;
