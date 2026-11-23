# PLAYZEK ANTI-CHEAT

Ecosistem anti-cheat original pentru platforma PlayZek: aplicație Windows pentru jucători, serviciu de protecție,
backend pe Ubuntu, dashboard de administrare și integrări pentru serverele CS 1.6 și CS2.

> Starea reală a fiecărei componente: [documentation/STATUS.md](documentation/STATUS.md).
> Arhitectura și alegerea tehnologiilor: [documentation/architecture/ARCHITECTURE.md](documentation/architecture/ARCHITECTURE.md).
> API: [documentation/api/API.md](documentation/api/API.md).
> Instalarea clientului: [documentation/installation/CLIENT.md](documentation/installation/CLIENT.md).

## Structură

```
client/
  core/                motor de protecție, client API, verificări Windows, IPC, stocare DPAPI
  service/             PLAYZEK Protection Service (serviciu Windows, LocalService)
  desktop-app/         aplicația PLAYZEK ANTI-CHEAT (WPF, fără privilegii)
  installer/           MSI (WiX 6)
build/                 build-installer.ps1
backend/
  api/                 host ASP.NET Core (.NET 10): endpoint-uri, rate limiting, health, CLI
  authentication/      parole, JWT, dispozitive, sesiuni anti-cheat, autorizare servere de joc
  data/                entități EF Core + migrații PostgreSQL
infrastructure/
  database/            script SQL idempotent generat din migrații
integrations/cs16/     plugin AMXX (versiunea veche, va fi rescris în faza 4)
tests/unit, tests/integration
documentation/         arhitectură, API, stare, contextul proiectului
legacy/                prototipul anterior (Electron + Node), păstrat ca referință
```

## Rulare locală a testelor

Necesită .NET SDK 10.

```
dotnet tool restore
dotnet test Playzek.AntiCheat.slnx
```

Testele de integrare pornesc API-ul real în memorie, pe SQLite. Pentru PostgreSQL:

```
PLAYZEK_TEST_POSTGRES="Host=localhost;Username=postgres;Password=..." dotnet test tests/integration/Playzek.Tests.Integration
```

## Configurare (variabile de mediu)

Vezi [backend/api/Playzek.Api/.env.example](backend/api/Playzek.Api/.env.example). Niciun secret nu se pune în repo.

## Licență

Apache 2.0 — vezi [LICENSE](LICENSE).
