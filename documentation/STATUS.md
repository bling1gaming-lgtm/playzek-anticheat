# Starea reală a livrabilelor

Regula din spec: nimic nu e marcat „gata” dacă nu există și nu a fost testat.

Ultima actualizare: 23.11.2026, Faza 2.

## Faze

| Fază | Stare | Ce s-a verificat |
|---|---|---|
| 1. Arhitectură, backend, autentificare, bază de date | **Gata** | 28 teste unitare + 31 de integrare; trec local (SQLite) și în CI pe **PostgreSQL 17** cu migrațiile reale |
| 2. Aplicație Windows, instalator, dispozitiv, verificări inițiale | **Gata, cu excepțiile de mai jos** | 20 teste client contra backend-ului real în memorie; rulare reală pe Windows 11 (backend + serviciu + interfață) |
| 3. Integritate sistem, procese, joc, protecție live | Neînceput | Logica veche (Node) există în `legacy/electron-client` ca referință |
| 4. Integrare CS 1.6 + validare sesiune | API gata, plugin nu | Endpoint-ul de autorizare e testat; pluginul AMXX nu e încă rescris |
| 5. Detecție, dovezi, analiză comportamentală | Neînceput | Analizorul vechi e în `legacy/node-backend/stats_analyzer.js` |
| 6. CS2 | Neînceput | — |
| 7. Securitate, compatibilitate, performanță, producție | Neînceput | — |

## Faza 2 — detalii

Gata și testat:
- **PLAYZEK Protection Service** (serviciu Windows separat, LocalService): login, înregistrare dispozitiv, verificări,
  sesiune protejată, heartbeat la 15 s, reconectare automată la întreruperi de rețea, reînnoire sesiune și token,
  token stocat criptat (DPAPI), reluarea login-ului după repornire.
- **Aplicația desktop WPF** (fără privilegii, comunică doar prin named pipe cu ACL local):
  login / cont nou, stare protecție, sesiune curentă și validarea ei de către server, cont și dispozitiv,
  verificări de sistem, export raport JSON, evenimente de securitate, deconectare. Fiecare buton face ce scrie.
- **Verificări reale** (API-uri Windows documentate): versiune Windows (10 22H2 / 11), arhitectură, Secure Boot (registru),
  TPM 2.0 (TBS API), VBS (WMI `Win32_DeviceGuard`). Rezultatele se trimit la backend ca rapoarte structurate.
- **MSI** construit cu WiX 6: verificare Windows ≥ 19045 x64, instalare serviciu, scurtătură, actualizare (MajorUpgrade).

Încă netestat / lipsă (cinstit):
- **Instalarea MSI-ului pe un PC** (instalare, reparare, dezinstalare, actualizare) — cere drepturi de administrator;
  MSI-ul e construit și validat de WiX (ICE), dar nu a fost încă rulat.
- **Semnare cod** — lipsește certificatul.
- **Updater automat** (manifeste semnate) — planificat pentru Faza 7.
- Secțiuni din spec neimplementate încă (fiindcă depind de fazele 3–5): scanare completă/rapidă cu progres, inspecția
  proceselor, integritatea fișierelor de joc, starea CS 1.6/CS2, istoricul scanărilor, setări (pornire cu Windows,
  notificări, limbă, confidențialitate, diagnostic). Nu există butoane pentru ele în interfață până nu funcționează.

## Tabele PostgreSQL

Create în migrația `InitialSchema` (15): `users`, `player_profiles`, `registered_devices`, `refresh_tokens` (suplimentar),
`game_servers`, `game_sessions`, `anti_cheat_sessions`, `client_versions`, `integrity_reports`, `bans`,
`administrator_roles`, `administrator_accounts`, `audit_logs`, `system_settings`.

De adăugat în fazele lor: `detection_events`, `behavioral_reports`, `ban_appeals`, `evidence_records`,
`update_packages`, `detection_rules`.

## Acoperire teste

- Backend: login, autentificare, blocare cont, expirare token de acces, rotație și reutilizare refresh token, logout,
  înregistrare dispozitiv și limită, izolare între jucători, expirare sesiune (durată maximă și heartbeat),
  întrerupere scurtă de rețea, reînnoire sesiune, versiuni de client blocate, autentificare server de joc,
  autorizare jucător cu toate motivele de refuz, ban activ/expirat, rapoarte de integritate, audit, health.
- Client: fluxul complet LOGIN → DEVICE → CHECKS → PROTECTION → validare de către serverul de joc; întrerupere scurtă
  (reconectare) și lungă (expirare pe server, motiv clar în client); oprirea protecției; reîmprospătarea transparentă a
  token-ului pe 15+ minute; repornirea serviciului; logout cu revocare; parolă greșită; HTTPS obligatoriu;
  amprentă dispozitiv; DPAPI; protocolul IPC; verificările reale pe mașina de test.
  Evenimentele simulate din teste sunt marcate explicit „[TEST]”.
