/**
 * Playzek Guard — connection.js
 * WebSocket cu backend-ul Playzek în timpul unui meci ranked.
 */

const WebSocket = require("ws");
const { runScan } = require("./scanner");

const HEARTBEAT_INTERVAL_MS = 15000;

class PlayzekGuardConnection {
  constructor({ serverUrl, authToken, matchId, onStatus, captureScreenshot }) {
    this.serverUrl = serverUrl;
    this.authToken = authToken;
    this.matchId = matchId;
    this.onStatus = onStatus || (() => {});
    this.captureScreenshot = captureScreenshot || (async () => null);
    this.ws = null;
    this.heartbeatTimer = null;
  }

  async connect() {
    this.ws = new WebSocket(this.serverUrl, {
      headers: { Authorization: `Bearer ${this.authToken}` },
    });

    this.ws.on("open", async () => {
      this.onStatus("connected");
      this.send({ type: "hello", matchId: this.matchId });

      // Captură de referință la intrarea în meci
      const screenshot = await this.captureScreenshot();
      if (screenshot) this.send({ type: "screenshot", matchId: this.matchId, image: screenshot });

      // Scanare inițială, completă (inclusiv verificare de hash)
      const initial = await runScan({ deepHashCheck: true });
      this.send({ type: "initial_scan", matchId: this.matchId, result: initial });
      if (!initial.clean) await this.reportFlagged(initial.flags);

      this.startHeartbeat();
    });

    this.ws.on("message", (raw) => {
      let msg;
      try { msg = JSON.parse(raw.toString()); } catch { return; }
      this.handleServerMessage(msg);
    });

    this.ws.on("close", () => {
      this.onStatus("disconnected");
      this.stopHeartbeat();
    });

    this.ws.on("error", (err) => {
      this.onStatus("error", err.message);
    });
  }

  async handleServerMessage(msg) {
    if (msg.type === "request_scan") {
      const result = await runScan({ deepHashCheck: true });
      this.send({ type: "scan_result", matchId: this.matchId, result });
      if (!result.clean) await this.reportFlagged(result.flags);
    }
    if (msg.type === "request_screenshot") {
      const screenshot = await this.captureScreenshot();
      if (screenshot) this.send({ type: "screenshot", matchId: this.matchId, image: screenshot });
    }
  }

  async reportFlagged(flags) {
    this.onStatus("flagged", flags);
    // La un flag, mai luăm o captură — dovadă pentru revizuire de admin
    const screenshot = await this.captureScreenshot();
    if (screenshot) {
      this.send({ type: "screenshot", matchId: this.matchId, image: screenshot, reason: "flag" });
    }
  }

  startHeartbeat() {
    this.heartbeatTimer = setInterval(async () => {
      const result = await runScan(); // check-ul ușor, fără hash, la fiecare heartbeat
      this.send({
        type: "heartbeat",
        matchId: this.matchId,
        clean: result.clean,
        flagCount: result.flags.length,
      });
      if (!result.clean) await this.reportFlagged(result.flags);
    }, HEARTBEAT_INTERVAL_MS);
  }

  stopHeartbeat() {
    if (this.heartbeatTimer) clearInterval(this.heartbeatTimer);
  }

  send(obj) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(obj));
    }
  }

  disconnect() {
    this.stopHeartbeat();
    if (this.ws) this.ws.close();
  }
}

module.exports = { PlayzekGuardConnection };
