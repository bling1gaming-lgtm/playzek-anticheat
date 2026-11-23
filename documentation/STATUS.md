# Starea reală a livrabilelor

Regula din spec: nimic nu e marcat „gata” dacă nu există și nu a fost testat.

Ultima actualizare: 23.11.2026, Faza 1.

## Faze

| Fază | Stare | Ce s-a verificat |
|---|---|---|
| 1. Arhitectură, backend, autentificare, bază de date | **Gata (backend)** | Compilare fără avertismente; 28 teste unitare + 31 de integrare trec local (SQLite in-memory). Rularea pe PostgreSQL 17 e configurată în CI și se verifică la primul push. |
| 2. Aplicație Windows, instalator, dispozitiv, verificări inițiale | Neînceput | — |
| 3. Integritate sistem, procese, joc, protecție live | Neînceput | Logica veche (Node) există în `legacy/electron-client` ca referință |
| 4. Integrare CS 1.6 + validare sesiune | API gata, plugin nu | Endpoint-ul `/game-server/players/authorize` e testat; pluginul AMXX vechi e în `integrations/cs16` și nu e încă rescris |
| 5. Detecție, dovezi, analiză comportamentală | Neînceput | Analizorul vechi e în `legacy/node-backend/stats_analyzer.js` |
| 6. CS2 | Neînceput | — |
| 7. Securitate, compatibilitate, performanță, producție | Neînceput | — |

## Tabele PostgreSQL

Create în migrația `InitialSchema` (15): `users`, `player_profiles`, `registered_devices`, `refresh_tokens` (suplimentar),
`game_servers`, `game_sessions`, `anti_cheat_sessions`, `client_versions`, `integrity_reports`, `bans`,
`administrator_roles`, `administrator_accounts`, `audit_logs`, `system_settings`.

De adăugat în fazele lor: `detection_events`, `behavioral_reports`, `ban_appeals`, `evidence_records`,
`update_packages`, `detection_rules`.

## Acoperire teste (Faza 1)

Login, autentificare, blocare cont, expirare token de acces, rotație și reutilizare refresh token, logout,
înregistrare dispozitiv și limită, izolare între jucători, expirare sesiune (durată maximă și heartbeat),
întrerupere scurtă de rețea, reînnoire sesiune, versiuni de client blocate, autentificare server de joc,
autorizare jucător cu toate motivele de refuz, ban activ/expirat, rapoarte de integritate, audit, health.
