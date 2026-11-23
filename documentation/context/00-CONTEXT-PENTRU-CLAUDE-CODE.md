# Playzek Anti-Cheat — context complet pentru continuare

Acest folder conține tot ce s-a discutat și construit până acum pentru
proiectul "Playzek Anti-Cheat", ca o sesiune nouă de Claude Code să poată
continua fără să piardă context.

## Ce este proiectul

Un sistem anti-cheat pentru platforma de gaming "Playzek" (clonă originală,
proprie, a conceptului FastCup.net, pentru jucători români de CS 1.6/CS2).
Nu reproduce codul, designul sau datele vreunei platforme existente —
este construit de la zero.

## Repo țintă

```
git@github.com:bling1gaming-lgtm/playzek-anticheat.git
```

Push-ul NU a reușit încă din sesiunea cloud Claude — cheia SSH de pe acest
calculator (`bling-vps`, în `C:\Users\patri\.ssh\id_ed25519.pub`) nu era
încă adăugată pe contul GitHub. Dacă between timp ai adăugat-o la
https://github.com/settings/keys, poți face push direct din acest
calculator cu `git push`.

Cheia publică (dacă mai trebuie adăugată):
```
ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAICbDcyJVV3byWo+txOK8ICsmLhxBWOz5h7TYtN8zVHqx bling-vps
```

## Decizii și limite ferme, de respectat în continuare

Acestea sunt limite pe care le-am ținut consecvent în toată conversația
anterioară și care rămân valabile:

1. **Nu reproducem FastCup.net** — nici cod, nici design, nici arhivă
   locală a site-ului lor, nici query-uri GraphQL interceptate din
   traficul lor live. Playzek e o platformă originală, inspirată conceptual,
   nu o copie.
2. **Nu decompilăm/reverse-engineering binare proprietare** (ex: nu am
   folosit installer-ul FACEIT ca bază pentru anticheat).
3. **Nu citim memoria altor procese arbitrare** (tehnică dual-use, identică
   cu cea folosită de cheat-urile de injectare). Ce facem în schimb,
   non-invaziv:
   - listă procese + nume + titluri ferestre (semnături cunoscute)
   - hash SHA-256 al executabilelor altor procese (comparat cu o listă de
     hash-uri cunoscute de cheat-uri)
   - **module/DLL-uri încărcate în procesul JOCULUI** (hl.exe/cstrike.exe)
     — doar enumerare, nu citire de memorie
   - **integritate fișiere joc** — hash-uim modele/sunete/sprite-uri oficiale
     CS 1.6 și detectăm orice a fost modificat (necesită o bază de hash-uri
     generată dintr-o instalare curată — vezi mai jos, era încă goală)
4. **Fără driver de kernel** în prima versiune (conform și specificației
   complete primite de la user — vezi fișierul 02).
5. **Fără date/statistici simulate în producție** — dashboard-ul trebuie
   să arate date reale din backend, nu mock-uri.

## Starea curentă a codului (în acest moment)

### Pe acest calculator: `C:\Users\patri\vps\playzek-guard\`
Client desktop Electron, deja instalat și testat vizual (fereastră fără
meniu Electron implicit, 440x700px, cu UI tematic "shield" roșu/verde/portocaliu).

Fișiere cheie:
- `src/main.js` — proces principal Electron (fereastră, tray, IPC)
- `src/preload.js` — expune API către UI (`scanNow`, `startMatch`, `stopMatch`)
- `src/scanner.js` — scanare non-invazivă (procese, ferestre, hash-uri,
  acum extins cu integritate fișiere joc + module încărcate)
- `src/connection.js` — WebSocket client către backend (heartbeat 15s,
  scanare completă la conectare și la cerere de la server)
- `src/game-integrity.js` — **NOU, adăugat ca răspuns la cererea
  "scanează tot, toate modificările jocului"** — hash integritate fișiere
  joc + enumerare module încărcate în hl.exe/cstrike.exe + auto-detectare
  cale instalare CS 1.6 (`findGameInstallPath`)
- `src/game-file-hashes.json` — **GOL încă** (`{"files": {}}`). Trebuie
  generat rulând `tools/generate-hashes.js` pe o instalare CS 1.6 CURATĂ
  (fără modificări) — user-ul a confirmat că instalarea lui actuală E
  deja modificată, deci NU poate fi folosită ca sursă pentru hash-uri
  (ar "sfinți" fișierele cheat-uite ca fiind cele oficiale). Are nevoie
  fie de o reinstalare curată, fie de hash-uri de pe altă instalare curată.
- `src/ui/index.html` — interfața vizuală a clientului
- `tools/generate-hashes.js` — script care generează
  `game-file-hashes.json` dintr-o instalare curată

### Pe serverul cloud (container Claude) — trebuie copiat manual sau
re-creat pe acest calculator / pe VPS:

`playzek-guard-server/` (backend + plugin de joc):
- `amxx/playzek_guard.sma` — plugin AMX Mod X pentru CS 1.6, trimite
  evenimente de kill (unghi mișcare, timp de reacție) către backend.
  **Nu are încă modul HTTP real** (amxxhttp) — doar `log_amx` local.
  Nu e compilat/testat pe un server CS 1.6 real încă.
- `analyzer/server.js` — backend Express, **tocmai extins cu**:
  - `/api/auth/register`, `/api/auth/login` (bcrypt + JWT)
  - `/api/devices/register` (înregistrare dispozitiv per user)
  - `/api/anticheat/session/start`, `/session/validate`, `/session/revoke`
    (sesiuni anti-cheat validate SERVER-SIDE, nu doar indicator client)
  - `/api/anticheat/integrity-report` (primește rapoarte de scanare)
  - `/api/anticheat/events` (kills de la AMXX, cu severitate persistată
    în `detection_events` dacă e critică)
  - `/api/admin/devices`, `/api/admin/sessions`, `/api/admin/detection-events`
    (date reale pentru un viitor dashboard admin)
- `analyzer/db.js` — **NOU** — bază de date SQLite persistentă
  (`better-sqlite3`), schema: users, registered_devices,
  anti_cheat_sessions, integrity_reports, detection_events, bans
- `analyzer/auth.js` — **NOU** — logică de autentificare/sesiuni
  (bcryptjs + jsonwebtoken), testată end-to-end cu succes (register →
  login → device → sesiune → validare → vizibil în admin)
- `analyzer/stats_analyzer.js` — analiză statistică comportamentală
  (deja existentă, testată): unghiuri de mișcare suspecte, timp de
  reacție, raport headshot, cu praguri în `THRESHOLDS`

`demo/` — platforma web Playzek (artifact separat, deja publicat),
nu are legătură directă cu anti-cheat-ul, dar face parte din ecosistem.

## Specificația completă primită de la user

Vezi fișierul `02-specificatie-completa-playzek-anticheat.md` din acest
folder — e documentul lung pe care user-ul l-a trimis, cu toate cele 13
componente cerute (Windows Installer, native engine C++, backend
Go+PostgreSQL+Redis, dashboard React, CS2 Shield, etc.)

**Decizie luată cu user-ul**: nu reconstruim de la zero în acel stack
exact (C++/.NET + Go + PostgreSQL + React) — e un proiect de luni de zile.
Continuăm să EVOLUĂM stack-ul existent (Electron + Node/Express), dar
împingem arhitectura spre ce cere spec-ul: autentificare reală, bază de
date persistentă, sesiuni validate server-side, dashboard cu date reale.

## Task list activ (din sesiunea anterioară)

1. ✅ Backend: migrare la SQLite persistent + schema reală
2. ✅ Backend: autentificare + sesiuni + device registration
3. ⏳ Electron client: login + înregistrare dispozitiv + stare sesiune reală
   (NU e făcut încă — UI-ul încă folosește DEMO_TOKEN/DEMO_MATCH placeholder)
4. ⏳ AMXX: validare sesiune reală cu backend-ul (nu doar log_amx)
5. ⏳ Dashboard minimal cu date reale din backend

## Pași următori recomandați

1. Copiază folderul `playzek-guard-server/` din containerul cloud pe acest
   calculator (sau recreează-l — codul complet e inclus mai jos / în
   fișierele atașate din acest pachet, dacă au fost incluse).
2. Rulează `npm install` în `analyzer/` dacă lipsesc `node_modules`.
3. Continuă cu task-ul 3: adaugă ecran de login + înregistrare dispozitiv
   în `playzek-guard/src/ui/index.html`, înlocuind DEMO_TOKEN/DEMO_MATCH.
4. Task 4: instalează modulul `amxxhttp` pentru AMXX, ca pluginul CS 1.6
   să valideze sesiunea real cu backend-ul (`/api/anticheat/session/validate`).
5. Task 5: dashboard simplu (poate fi un singur fișier HTML/JS la început)
   care citește `/api/admin/*`.
6. Pentru integritate fișiere joc: reinstalează CS 1.6 curat undeva (alt
   folder, altă mașină sau VM), rulează
   `node tools/generate-hashes.js "<cale>\cstrike"`, copiază
   `game-file-hashes.json` rezultat peste cel gol din `playzek-guard/src/`.
