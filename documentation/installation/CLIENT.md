# Instalarea clientului PLAYZEK ANTI-CHEAT (Windows)

## Cerințe

- Windows 10 22H2 (build 19045) sau Windows 11, 64 de biți — verificat de instalator.
- Nu trebuie instalat .NET separat (aplicația e publicată self-contained).
- Nu se dezactivează Windows Defender, Secure Boot, antivirusul sau alte protecții.

## Instalare

```powershell
msiexec /i PLAYZEK-ANTICHEAT-Setup.msi BACKENDURL=https://api.exemplu.ro/ /l*v "$env:TEMP\playzek-install.log"
```

- `BACKENDURL` — adresa backend-ului PlayZek (HTTPS obligatoriu; HTTP e acceptat doar pentru `127.0.0.1`/`localhost`).
  Se salvează în `HKLM\SOFTWARE\PLAYZEK\AntiCheat\BackendUrl` și se păstrează la actualizări.
- `/l*v` — jurnalul complet de instalare.

Ce instalează:

| Componentă | Loc | Privilegii |
|---|---|---|
| Aplicația desktop `PlayzekAntiCheat.exe` | `C:\Program Files\PLAYZEK\AntiCheat\` | utilizatorul curent (`asInvoker`) |
| Serviciul `PlayzekProtectionService` | `...\AntiCheat\service\` | `NT AUTHORITY\LocalService`, pornire automată, repornire la eșec |
| Scurtătură | Start → PLAYZEK → PLAYZEK ANTI-CHEAT | — |

## Actualizare, reparare, dezinstalare

- **Actualizare**: rulezi MSI-ul versiunii noi (MajorUpgrade înlocuiește versiunea veche; downgrade-ul e refuzat).
- **Reparare**: Setări → Aplicații → PLAYZEK ANTI-CHEAT → Modificare → Reparare, sau `msiexec /fa PLAYZEK-ANTICHEAT-Setup.msi`.
- **Dezinstalare**: Setări → Aplicații, sau `msiexec /x PLAYZEK-ANTICHEAT-Setup.msi`. Serviciul e oprit și șters.

## Unde stau datele

- Refresh token-ul de login: criptat cu DPAPI în profilul contului LocalService
  (`C:\Windows\ServiceProfiles\LocalService\AppData\Local\PLAYZEK\AntiCheat\session.bin`). Aplicația desktop nu vede token-urile.
- Nu se salvează parole.

## Construirea MSI-ului (dezvoltatori)

```powershell
./build/build-installer.ps1 -Version 0.1.0
```

Rezultat: `artifacts\installer\PLAYZEK-ANTICHEAT-Setup.msi`. CI-ul de pe GitHub îl construiește la fiecare push (artefact descărcabil).

## Semnare cod (obligatoriu pentru producție)

MSI-ul și executabilele **nu sunt încă semnate**: Windows SmartScreen va afișa un avertisment. Pentru producție e nevoie de un
certificat de code signing (OV/EV) cumpărat de PlayZek, apoi:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f cert.pfx /p <parola> PLAYZEK-ANTICHEAT-Setup.msi
```

## Dezvoltare fără instalare

```powershell
dotnet run --project client/service/Playzek.ProtectionService   # serviciul, ca aplicație de consolă
dotnet run --project client/desktop-app/Playzek.Desktop          # interfața
```
