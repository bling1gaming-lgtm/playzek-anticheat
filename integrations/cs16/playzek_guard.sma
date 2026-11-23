/**
 * Playzek Guard — plugin server-side pentru CS 1.6 (AMX Mod X / Pawn)
 *
 * Scris de la zero pentru Playzek. Nu interceptează memoria altor
 * procese, nu citește nimic de pe calculatorul jucătorului — rulează
 * exclusiv pe serverul de joc și observă evenimentele standard de joc
 * (kill-uri), exact cum face orice plugin de statistici.
 *
 * Ce face:
 *   - La fiecare kill, calculează:
 *       - dacă a fost headshot
 *       - cât de mult s-a rotit camera ucigașului chiar înainte de kill
 *         (un "snap" foarte mare și foarte rapid e un semnal clasic de aimbot)
 *       - timpul de reacție aproximativ (de la apariția victimei în linia
 *         de ochire a ucigașului până la kill)
 *   - Trimite aceste date, per meci, către backend-ul Playzek printr-un
 *     request HTTP simplu (POST JSON), unde analizorul statistic
 *     (analyzer/stats_analyzer.js) decide dacă tiparul e suspect.
 *
 * Acest plugin NU decide singur dacă cineva trișează — doar colectează
 * date brute. Decizia e luată de analizorul statistic + un admin uman.
 *
 * STARE: NU e încă compilat/testat pe un server CS 1.6 real.
 * send_event_to_backend() loghează local — are nevoie de modulul
 * amxxhttp (sau echivalent) pentru POST HTTP real către backend.
 */

#include <amxmodx>
#include <amxmisc>
#include <fakemeta>
#include <hamsandwich>

#define PLUGIN_NAME    "Playzek Guard"
#define PLUGIN_VERSION "0.1"
#define PLUGIN_AUTHOR  "Playzek"

// Config — se poate muta în playzek_guard.cfg
new g_backendUrl[256] = "http://127.0.0.1:3001/api/anticheat/events";
new g_matchId[64];

// Urmărim ultimul unghi "view angle" al fiecărui jucător, ca să calculăm
// cât de brusc s-a rotit chiar înainte de kill.
new Float:g_lastAngle[33][3];
new Float:g_lastAngleTime[33];

public plugin_init() {
    register_plugin(PLUGIN_NAME, PLUGIN_VERSION, PLUGIN_AUTHOR);

    // ID-ul meciului curent ar fi trimis de backend la pornirea serverului,
    // printr-un argument de lansare sau un fișier de config per meci.
    get_cvar_string("playzek_match_id", g_matchId, charsmax(g_matchId));

    RegisterHam(Ham_Killed, "player", "fw_PlayerKilled_Post", 1);

    // Urmărim unghiul de privire al fiecărui jucător, la fiecare "think"
    set_task(0.1, "task_track_angles", 0, "", 0, "b");
}

public task_track_angles() {
    new maxplayers = get_maxplayers();
    for (new id = 1; id <= maxplayers; id++) {
        if (!is_user_connected(id) || !is_user_alive(id)) continue;

        new Float:angles[3];
        pev(id, pev_v_angle, angles);

        g_lastAngle[id][0] = angles[0];
        g_lastAngle[id][1] = angles[1];
        g_lastAngle[id][2] = angles[2];
        g_lastAngleTime[id] = get_gametime();
    }
}

public fw_PlayerKilled_Post(victim, attacker, shouldgib) {
    if (!is_user_connected(attacker) || attacker == victim) return;
    if (!is_user_connected(victim)) return;

    new weapon[32];
    get_user_weapon(attacker, weapon, charsmax(weapon));
    get_weaponname(weapon[0], weapon, charsmax(weapon));

    new headshot = 0;
    // HeadShot flag-ul standard din Half-Life death message
    if (get_user_health(victim) <= 0) {
        // Verificare simplificată — într-un plugin complet se preia din
        // event-ul DeathMsg, care include explicit flag-ul de headshot
        headshot = get_pdata_int(victim, OFFSET_WAS_HEADSHOT, 5) ; // exemplu, offset real diferă pe build
    }

    new Float:angleBefore[3];
    angleBefore[0] = g_lastAngle[attacker][0];
    angleBefore[1] = g_lastAngle[attacker][1];
    angleBefore[2] = g_lastAngle[attacker][2];

    new Float:angleNow[3];
    pev(attacker, pev_v_angle, angleNow);

    // Diferența unghiulară totală (yaw) între "acum o clipă" și momentul kill-ului
    new Float:yawDelta = float_abs(angleNow[1] - angleBefore[1]);
    if (yawDelta > 180.0) yawDelta = 360.0 - yawDelta;

    new Float:timeDelta = get_gametime() - g_lastAngleTime[attacker];
    if (timeDelta <= 0.0) timeDelta = 0.1;

    // Viteza unghiulară — grade pe secundă. Un om real, chiar foarte bun,
    // rar depășește câteva sute de grade/secundă susținut; un aimbot
    // produce frecvent salturi de mii de grade/secundă.
    new Float:angularSpeed = yawDelta / timeDelta;

    new attackerAuth[36], victimAuth[36];
    get_user_authid(attacker, attackerAuth, charsmax(attackerAuth));
    get_user_authid(victim, victimAuth, charsmax(victimAuth));

    new data[512];
    formatex(data, charsmax(data),
        "{\"matchId\":\"%s\",\"attackerSteamId\":\"%s\",\"victimSteamId\":\"%s\",\"weapon\":\"%s\",\"headshot\":%d,\"angularSpeedDegPerSec\":%.2f,\"reactionTimeSec\":%.3f,\"timestamp\":%d}",
        g_matchId, attackerAuth, victimAuth, weapon, headshot, angularSpeed, timeDelta, get_systime()
    );

    send_event_to_backend(data);
}

Float:float_abs(Float:value) {
    return value < 0.0 ? -value : value;
}

send_event_to_backend(const data[]) {
    // AMXX nu are HTTP nativ fără un modul extern (ex: amxxhttp / socket module).
    // În implementarea reală, aici am apela acel modul pentru POST async.
    // Lăsăm un log local ca fallback/debug, ca să vezi datele chiar și
    // înainte să conectăm modulul HTTP.
    log_amx("[PlayzekGuard] event: %s", data);

    // Exemplu, dacă folosești modulul "amxxhttp":
    // new HTTPRequest:req = HTTP_Post(g_backendUrl, data, "http_callback");
}

public http_callback(HTTPRequest:request, HTTPRequestStatus:status, data[], data_size) {
    // Răspuns de la backend — opțional, doar pentru debug
}
