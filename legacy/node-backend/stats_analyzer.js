/**
 * Playzek Guard — stats_analyzer.js
 *
 * Primește evenimente de kill (din pluginul AMXX, per meci) și decide
 * dacă tiparul unui jucător e statistic suspect, comparat cu o
 * populație de referință de jucători "curați" (sau cu pragurile
 * generale de mai jos, dacă nu avem încă destule date proprii).
 *
 * Nu analizăm memoria PC-ului nimănui — doar numere din joc:
 * unghiuri, timpi de reacție, procent de headshot.
 */

// Praguri de pornire, bazate pe cunoștințe publice despre jocul competitiv.
// Se recomandă calibrarea lor ulterior pe baza datelor proprii Playzek
// (ex: percentila 99 din populația reală de jucători curați).
const THRESHOLDS = {
  // grade/secundă — un "snap" susținut peste asta, repetat, e suspect
  angularSpeedWarn: 1200,
  angularSpeedCritical: 2200,

  // reacție sub pragul ăsta, repetat, la kill-uri cu headshot, e suspect
  reactionTimeCriticalSec: 0.12,

  // procent headshot anormal de ridicat pe un eșantion suficient de kill-uri
  headshotRatioWarnMinKills: 15,
  headshotRatioWarnPct: 70,
  headshotRatioCriticalPct: 85,
};

class PlayerMatchStats {
  constructor(steamId) {
    this.steamId = steamId;
    this.kills = [];
  }

  addKill(event) {
    this.kills.push(event);
  }

  get killCount() {
    return this.kills.length;
  }

  get headshotCount() {
    return this.kills.filter((k) => k.headshot).length;
  }

  get headshotRatioPct() {
    if (this.killCount === 0) return 0;
    return (this.headshotCount / this.killCount) * 100;
  }

  get suspiciousSnapKills() {
    return this.kills.filter((k) => k.angularSpeedDegPerSec >= THRESHOLDS.angularSpeedWarn);
  }

  get criticalSnapKills() {
    return this.kills.filter((k) => k.angularSpeedDegPerSec >= THRESHOLDS.angularSpeedCritical);
  }

  get suspiciousReactionKills() {
    return this.kills.filter(
      (k) => k.headshot && k.reactionTimeSec <= THRESHOLDS.reactionTimeCriticalSec
    );
  }

  evaluate() {
    const reasons = [];
    let severity = "clean"; // clean | warn | critical

    if (this.criticalSnapKills.length >= 2) {
      severity = "critical";
      reasons.push(
        `${this.criticalSnapKills.length} kill-uri cu viteză unghiulară peste ${THRESHOLDS.angularSpeedCritical}°/s`
      );
    } else if (this.suspiciousSnapKills.length >= 4) {
      severity = escalate(severity, "warn");
      reasons.push(
        `${this.suspiciousSnapKills.length} kill-uri cu viteză unghiulară peste ${THRESHOLDS.angularSpeedWarn}°/s`
      );
    }

    if (this.suspiciousReactionKills.length >= 3) {
      severity = escalate(severity, "critical");
      reasons.push(
        `${this.suspiciousReactionKills.length} headshot-uri cu reacție sub ${THRESHOLDS.reactionTimeCriticalSec * 1000}ms`
      );
    }

    if (
      this.killCount >= THRESHOLDS.headshotRatioWarnMinKills &&
      this.headshotRatioPct >= THRESHOLDS.headshotRatioCriticalPct
    ) {
      severity = escalate(severity, "critical");
      reasons.push(`headshot ratio ${this.headshotRatioPct.toFixed(1)}% pe ${this.killCount} kill-uri`);
    } else if (
      this.killCount >= THRESHOLDS.headshotRatioWarnMinKills &&
      this.headshotRatioPct >= THRESHOLDS.headshotRatioWarnPct
    ) {
      severity = escalate(severity, "warn");
      reasons.push(`headshot ratio ${this.headshotRatioPct.toFixed(1)}% pe ${this.killCount} kill-uri`);
    }

    return {
      steamId: this.steamId,
      killCount: this.killCount,
      headshotRatioPct: Number(this.headshotRatioPct.toFixed(1)),
      severity,
      reasons,
    };
  }
}

function escalate(current, next) {
  const order = { clean: 0, warn: 1, critical: 2 };
  return order[next] > order[current] ? next : current;
}

class MatchAnalyzer {
  constructor(matchId) {
    this.matchId = matchId;
    this.players = new Map(); // steamId -> PlayerMatchStats
  }

  ingest(event) {
    if (!this.players.has(event.attackerSteamId)) {
      this.players.set(event.attackerSteamId, new PlayerMatchStats(event.attackerSteamId));
    }
    this.players.get(event.attackerSteamId).addKill(event);
  }

  report() {
    const results = [...this.players.values()].map((p) => p.evaluate());
    return {
      matchId: this.matchId,
      generatedAt: new Date().toISOString(),
      players: results,
      flaggedCount: results.filter((r) => r.severity !== "clean").length,
    };
  }
}

module.exports = { MatchAnalyzer, PlayerMatchStats, THRESHOLDS };
