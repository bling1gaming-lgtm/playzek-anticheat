# PLAYZEK AC Backend — API v1

Toate răspunsurile de eroare sunt `application/problem+json` cu un câmp stabil `code` și un mesaj clar `detail`.

## Jucător — autentificare (rate limit: 10/min/IP)

| Metodă | Rută | Corp | Răspuns |
|---|---|---|---|
| POST | `/api/v1/auth/register` | `{username, password, steamId?}` | `{userId, username, steamId64}` |
| POST | `/api/v1/auth/login` | `{username, password}` | `{userId, username, accessToken, accessTokenExpiresAt, refreshToken, refreshTokenExpiresAt}` |
| POST | `/api/v1/auth/refresh` | `{refreshToken}` | ca la login (token-uri noi; cel vechi e invalidat) |
| POST | `/api/v1/auth/logout` | `{refreshToken}` | 204 |

`steamId` acceptă SteamID64 sau `STEAM_X:Y:Z`.

## Jucător — cont, dispozitive, sesiuni (header `Authorization: Bearer <accessToken>`)

| Metodă | Rută | Note |
|---|---|---|
| GET | `/api/v1/me` | cont, SteamID, status, ban activ |
| POST | `/api/v1/devices` | `{fingerprint (sha256 hex), deviceName, osVersion?, clientVersion?}` — max. 3 dispozitive active |
| GET | `/api/v1/devices` | dispozitivele contului |
| DELETE | `/api/v1/devices/{id}` | revocă dispozitivul și sesiunile lui active |
| POST | `/api/v1/anticheat/sessions/` | `{deviceId, clientVersion, game?: "cs16"\|"cs2"}` → `{sessionId, sessionToken, expiresAt, heartbeatIntervalSeconds, heartbeatGraceSeconds}` |
| POST | `/api/v1/anticheat/sessions/heartbeat` | header `X-Playzek-Session` → `{valid, code, message, expiresAt, lastHeartbeatAt}` |
| POST | `/api/v1/anticheat/sessions/renew` | header `X-Playzek-Session` → token nou |
| POST | `/api/v1/anticheat/sessions/end` | oprește protecția |
| POST | `/api/v1/integrity-reports` | `{deviceId, windowsVersion, clientVersion, checks: [{category, result, severity, checkedAt?, summary?, evidenceReference?}]}` |

`result`: `Pass | Fail | Warning | Error | NotApplicable`.
`severity`: `Information | Warning | SuspiciousIndicator | HighConfidenceDetection` (`ConfirmedViolation` e rezervat deciziei umane).

## Server de joc (header `X-Playzek-Server-Key`, opțional `X-Playzek-Plugin-Version`)

| Metodă | Rută | Corp | Răspuns |
|---|---|---|---|
| GET | `/api/v1/game-server/ping` | — | identitatea serverului, ora serverului |
| POST | `/api/v1/game-server/players/authorize` | `{steamId, playerName?}` | `{allowed, code, message, messageEn, gameSessionId, sessionExpiresAt}` |
| POST | `/api/v1/game-server/players/disconnect` | `{gameSessionId}` | 204 |

Coduri de refuz: `account_not_linked`, `no_active_session`, `heartbeat_timeout`, `session_expired`, `session_revoked`, `session_ended`, `account_inactive`, `device_not_active`, `banned`, `game_mismatch`.

## Sănătate

| GET `/health/live` | procesul rulează |
|---|---|
| GET `/health/ready` | baza de date răspunde (503 altfel) |

## CLI operator (pe VPS)

```
dotnet Playzek.Api.dll migrate
dotnet Playzek.Api.dll create-server --name "PlayZek #1" --game cs16 --address 1.2.3.4:27015
```
