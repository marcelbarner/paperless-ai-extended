# paperless-ai-extended

Autonome Erweiterung für [Paperless-NGX](https://docs.paperless-ngx.com/), die Dokumente mithilfe von **Azure Document Intelligence** (OCR) und **Azure OpenAI** vollautomatisch verarbeitet.

**Stack:** .NET 10 · Angular 22 · Material Design · SQLite · Azure AI

---

## Inhaltsverzeichnis

1. [Features](#features)
2. [Voraussetzungen](#voraussetzungen)
3. [Installation (Proxmox LXC)](#installation-proxmox-lxc)
4. [Lokale Entwicklung](#lokale-entwicklung)
5. [Konfiguration](#konfiguration)
6. [Funktionsweise](#funktionsweise)
7. [UI-Dokumentation](#ui-dokumentation)
8. [Troubleshooting](#troubleshooting)

---

## Features

### OCR — Azure Document Intelligence
- PDF-Dokumente werden an Azure Document Intelligence gesendet
- Extrahierter Text wird als Dokumentinhalt in Paperless-NGX gespeichert
- Wählbares Ausgabeformat: **Plain Text** oder **Markdown** (Tabellen, Überschriften)
- Wählbares Modell: `prebuilt-read` (schnell) oder `prebuilt-layout` (strukturreich)

### KI-Verarbeitung — Azure OpenAI
- Analysiert den Dokumentinhalt und weist automatisch zu:
  - **Titel** – aussagekräftiger Dokumenttitel (max. 80 Zeichen)
  - **Datum** – Rechnungs-/Ausstellungsdatum aus dem Dokument
  - **Korrespondent** – wählt aus vorhandenen oder legt neu an (konfigurierbar)
  - **Dokumenttyp** – wählt aus vorhandenen oder legt neu an (konfigurierbar)
  - **Tags** – wählt aus vorhandenen oder legt neu an (konfigurierbar)
  - **Speicherpfad** – wählt aus vorhandenen oder legt neu an (konfigurierbar)
  - **Custom Fields** – befüllt benutzerdefinierte Felder

### Metadaten-Beschreibungen
- Die KI erhält **immer alle aktuellen Einträge live von Paperless** — kein manueller Sync nötig
- Zu jedem Korrespondenten, Dokumenttyp, Tag, Speicherpfad und Custom Field kann eine **KI-Beschreibung** gepflegt werden
- Diese Beschreibungen fließen als Kontext in den AI-Prompt ein und verbessern die Treffsicherheit erheblich
- Der Sync-Button in der UI dient nur dazu, Beschreibungen für bestehende Einträge vorzubereiten

### Verarbeitungs-Queue mit History
- Eigene persistente Queue (SQLite), überlebt Neustarts
- Dashboard zeigt alle Jobs mit Status, Zeitstempel und Details
- Für jeden Job: vollständiges Ergebnis (Titel, Datum, Felder), Reasoning der KI, **gesendeten Prompt**
- Fehlgeschlagene Jobs können per Retry-Button erneut gestartet werden

### KI-Anlegen-Berechtigungen
- Separat konfigurierbar ob die KI Korrespondenten, Dokumenttypen, Tags und Speicherpfade **neu anlegen** darf
- Neu angelegte Entitäten erscheinen sofort in der Metadata-Verwaltung

### Anpassbare KI-Prompts
- System-Prompt und User-Prompt vollständig über die UI editierbar
- "Auf Standard zurücksetzen"-Funktion
- Gesendeter Prompt im Job-Detail sichtbar (mit Kopieren-Button)

---

## Voraussetzungen

- **Paperless-NGX** (läuft lokal per Docker oder auf einem anderen Server)
- **Azure Document Intelligence** Ressource (kostenloser Tier reicht für den Einstieg)
- **Azure OpenAI** Ressource mit einem GPT-4o Deployment

---

## Installation (Proxmox LXC)

### Erstinstallation — ein Befehl

Auf dem LXC als `root`:

```bash
curl -fsSL https://raw.githubusercontent.com/marcelbarner/paperless-ai-extended/main/install.sh | bash
```

Das Skript erledigt alles automatisch:
- Installiert .NET 10 SDK, Node.js 20, nginx
- Klont das Repository nach `/opt/paperless-ai`
- Baut Backend und Frontend
- Richtet systemd-Dienst und nginx-Reverse-Proxy ein
- Legt globalen `paperless-ai-update`-Befehl an

Die App ist danach unter `http://<lxc-ip>` erreichbar.

### Updates — ein Befehl

```bash
paperless-ai-update
```

Führt automatisch aus: `git pull` → Backend neu bauen → Frontend neu bauen → Dienst neustarten.

> Die SQLite-Datenbank unter `/opt/paperless-ai/data/` bleibt bei jedem Update erhalten.

### Dienst verwalten

```bash
journalctl -u paperless-ai -f      # Live-Logs
systemctl status paperless-ai      # Status
systemctl restart paperless-ai     # Neustart
```

---

## Lokale Entwicklung

### Paperless-NGX starten

```bash
# Secret Key generieren
python3 -c "import secrets; print(secrets.token_hex(32))"
# Wert in docker-compose.env bei PAPERLESS_SECRET_KEY eintragen

docker compose up -d
# Paperless läuft auf http://localhost:8000

docker compose exec webserver python3 manage.py createsuperuser
```

### Backend starten

```powershell
cd src/PaperlessAI.API
dotnet run
# API:      http://localhost:5050
# API-Docs: http://localhost:5050/scalar
```

### Frontend starten

```powershell
cd src/paperless-ai-frontend
npm install
npx ng serve --port 4201 --proxy-config proxy.conf.json --open
# App: http://localhost:4201
```

---

## Konfiguration

Alle Einstellungen werden über **Einstellungen** in der Web-UI gepflegt und in SQLite gespeichert. Keine Konfigurationsdatei nötig.

### Verbindungen

| Einstellung | Beschreibung |
|---|---|
| **Paperless URL** | URL der Paperless-NGX Instanz, z.B. `http://192.168.1.10:8000` |
| **Paperless API Token** | Aus Paperless: Admin → Profil → API-Token |
| **Tag-Name für OCR** | Tag der OCR auslöst (Standard: `paperless-ai-ocr`) |
| **Tag-Name für KI-Verarbeitung** | Tag der KI-Verarbeitung auslöst (Standard: `paperless-ai-process`) |
| **Polling-Intervall** | Sekunden zwischen Paperless-Abfragen (Standard: 30) |

### Azure Document Intelligence

| Einstellung | Beschreibung |
|---|---|
| **Endpoint** | Aus dem Azure Portal, z.B. `https://xyz.cognitiveservices.azure.com/` |
| **Key** | API-Schlüssel aus dem Azure Portal |
| **OCR-Ausgabeformat** | `text` (Fließtext) oder `markdown` (Tabellen/Struktur) |
| **Analyse-Modell** | `auto` (empfohlen), `prebuilt-read` oder `prebuilt-layout` |

> **Hinweis:** Bei Markdown-Format wird automatisch `prebuilt-layout` verwendet, da `prebuilt-read` keine Strukturinformationen liefert.

### Azure OpenAI

| Einstellung | Beschreibung |
|---|---|
| **Endpoint** | Basis-URL ohne Pfad, z.B. `https://xyz.openai.azure.com/` |
| **Key** | API-Schlüssel |
| **Deployment Name** | Name des GPT-4o Deployments, z.B. `gpt-4o` |

### KI-Anlegen-Berechtigungen

Steuert ob die KI neue Einträge in Paperless anlegen darf wenn kein passender vorhanden ist:

| Toggle | Beschreibung |
|---|---|
| Korrespondenten anlegen | KI darf neue Korrespondenten erstellen |
| Dokumenttypen anlegen | KI darf neue Dokumenttypen erstellen |
| Tags anlegen | KI darf neue Tags erstellen |
| Speicherpfade anlegen | KI darf neue Speicherpfade erstellen |

> Neu angelegte Entitäten erscheinen sofort in der Metadaten-Verwaltung und können mit Beschreibungen versehen werden.

---

## Funktionsweise

### Tags

Die Anwendung erstellt beim ersten Start automatisch zwei Tags in Paperless:

| Tag | Farbe | Auslöser |
|---|---|---|
| `paperless-ai-ocr` | Blau | OCR via Azure Document Intelligence |
| `paperless-ai-process` | Grün | KI-Verarbeitung via Azure OpenAI |

### Verarbeitungs-Flow

```
Paperless-NGX
  └── Dokument mit Tag(s)
        │
        ▼
  Polling-Service (alle N Sekunden)
        │
        ▼
  Queue (SQLite)
        │
        ├─► OCR-Tag vorhanden?
        │     └── PDF → Azure Document Intelligence → Content in Paperless speichern
        │           └── OCR-Tag entfernen
        │
        └─► AI-Tag vorhanden (und kein OCR-Tag mehr)?
              └── Content + Metadaten-Kontext → Azure OpenAI
                    └── Titel, Datum, Korrespondent, Typ, Tags, Pfad, Custom Fields
                          └── Paperless aktualisieren → AI-Tag entfernen
```

**Wichtig:** Haben beide Tags: OCR läuft zuerst, AI folgt beim nächsten Poll-Zyklus automatisch.

### Metadaten-Kontext für die KI

Die KI erhält bei jeder Verarbeitung alle verfügbaren Metadaten aus Paperless inklusive der gepflegten Beschreibungen:

```
### Korrespondenten
- ID 1: Amazon — Online-Händler für Bestellungen und Retouren
- ID 2: Stadtwerke — Strom- und Gasversorger

### Dokumenttypen
- ID 1: Rechnung — Zahlungspflichtige Rechnungen aller Art
- ID 2: Kontoauszug — Monatliche Bankkontoauszüge
```

Je präziser die Beschreibungen, desto besser die KI-Ergebnisse.

---

## UI-Dokumentation

### Dashboard

Übersicht aller Verarbeitungsjobs mit:
- **Statistik-Kacheln**: Ausstehend / In Bearbeitung / Abgeschlossen / Fehlgeschlagen (klickbar als Filter)
- **Filter** nach Status und Pagination
- **Paperless-Link** pro Job öffnet das Dokument direkt in Paperless
- **Job-Detail-Dialog** zeigt:
  - OCR-Jobs: Zeichenanzahl, PDF-Größe, Verifikationsstatus, Textvorschau
  - AI-Jobs: Titel, Datum, alle zugewiesenen Felder inkl. neu angelegter Entitäten, Reasoning
  - **Gesendeter Prompt**: System-Prompt und User-Prompt mit Kopieren-Button
- **Retry** für fehlgeschlagene Jobs

### Metadaten

Für jeden Metadaten-Typ (Korrespondenten, Dokumenttypen, Tags, Speicherpfade, Custom Fields):
- Die KI sieht **immer automatisch alle aktuellen Einträge** aus Paperless — kein Sync nötig
- **Sync-Button** importiert die aktuelle Liste um **Beschreibungen** hinzufügen zu können
- **Inline-Bearbeitung** der KI-Beschreibung pro Eintrag
- Beschreibungen werden sofort im nächsten AI-Job berücksichtigt

### Einstellungen

Zwei Tabs:
- **Verbindungen**: alle Credentials und technischen Einstellungen
- **KI-Prompts**: System- und User-Prompt editierbar, zurücksetzbar auf Standard

---

## Troubleshooting

### Jobs schlagen mit "404 Not Found" fehl
→ Paperless API-Token ist nicht gesetzt oder falsch. Token in **Einstellungen → Paperless API Token** eintragen.

### KI weist immer null zu
→ Keine Metadaten synchronisiert. In **Metadaten → [Typ] → Von Paperless synchronisieren** klicken.

### OCR-Ergebnis sieht wie Fließtext aus, obwohl Markdown gewählt
→ Sicherstellen dass **Analyse-Modell** auf `auto` oder `prebuilt-layout` steht. `prebuilt-read` ignoriert das Format-Flag.

### Prompt enthält keine `new_*` Felder
→ In **Einstellungen → KI-Prompts → User-Prompt → Auf Standard zurücksetzen** klicken.

### Dienst startet nicht nach Update
```bash
journalctl -u paperless-ai -n 50 --no-pager
```

### App nicht erreichbar nach Installation
```bash
systemctl status paperless-ai
systemctl status nginx
nginx -t
```

---

## Referenz

- [Paperless-NGX Dokumentation](https://docs.paperless-ngx.com/)
- [Azure Document Intelligence](https://learn.microsoft.com/de-de/azure/ai-services/document-intelligence/)
- [Azure OpenAI](https://learn.microsoft.com/de-de/azure/ai-services/openai/)
