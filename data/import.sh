#!/usr/bin/env bash
# ============================================================
# PaperlessAI – Daten-Import
# Legt Dokumententypen, Tags, Custom Fields und Speicherpfade
# in Paperless-NGX an und speichert KI-Beschreibungen in der
# PaperlessAI-Anwendung.
#
# Verwendung:
#   bash import.sh \
#     --paperless-url  http://192.168.1.100:8000 \
#     --paperless-token cb7f599026... \
#     --app-url        http://192.168.1.100
#
# Das Skript ist idempotent – bereits vorhandene Einträge
# (gleicher Name) werden übersprungen.
# ============================================================
set -euo pipefail

# --- Argument-Parsing ---
PAPERLESS_URL=""
PAPERLESS_TOKEN=""
APP_URL=""

while [[ $# -gt 0 ]]; do
  case $1 in
    --paperless-url)   PAPERLESS_URL="${2%/}";   shift 2 ;;
    --paperless-token) PAPERLESS_TOKEN="$2";     shift 2 ;;
    --app-url)         APP_URL="${2%/}";         shift 2 ;;
    *) echo "Unbekanntes Argument: $1"; exit 1 ;;
  esac
done

if [[ -z "$PAPERLESS_URL" || -z "$PAPERLESS_TOKEN" || -z "$APP_URL" ]]; then
  echo "Verwendung: bash import.sh --paperless-url URL --paperless-token TOKEN --app-url URL"
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# --- Hilfsfunktionen ---

P_AUTH=(-H "Authorization: Token $PAPERLESS_TOKEN" -H "Content-Type: application/json")
APP_H=(-H "Content-Type: application/json")

# Paperless GET
p_get() { curl -sf "${P_AUTH[@]}" "$PAPERLESS_URL/api/$1"; }

# Paperless POST
p_post() { curl -sf -X POST "${P_AUTH[@]}" -d "$2" "$PAPERLESS_URL/api/$1"; }

# App PUT
app_put() { curl -sf -X PUT "${APP_H[@]}" -d "$2" "$APP_URL/api/$1"; }

# App POST
app_post() { curl -sf -X POST "${APP_H[@]}" -d "$2" "$APP_URL/api/$1" 2>/dev/null || true; }

# Name URL-kodieren (für Query-Parameter)
urlencode() { python3 -c "import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1]))" "$1"; }

# Vorhandene Entitäten nach Namen suchen → gibt ID zurück oder ""
find_id() {
  local endpoint="$1" name="$2"
  local enc; enc=$(urlencode "$name")
  p_get "${endpoint}/?name=${enc}" | jq -r '.results[0].id // empty' 2>/dev/null || echo ""
}

# Beschreibung in der App aktualisieren
save_description() {
  local app_type="$1" entity_id="$2" name="$3" description="$4"
  local body; body=$(jq -n --arg n "$name" --arg d "$description" '{"name":$n,"description":$d}')
  app_put "metadata/${app_type}/${entity_id}/description" "$body" > /dev/null
}

# Fortschritt
ok()   { echo "  ✓ $1"; }
skip() { echo "  ~ $1 (bereits vorhanden)"; }
warn() { echo "  ✗ $1"; }

echo ""
echo "============================================"
echo " PaperlessAI – Daten-Import"
echo " Paperless: $PAPERLESS_URL"
echo " App:       $APP_URL"
echo "============================================"

# --- 1. Dokumententypen ---
echo ""
echo "[ Dokumententypen ]"
dok_file="$SCRIPT_DIR/dokumententypen.json"
dok_count=0

while IFS= read -r entry; do
  name=$(echo "$entry" | jq -r '.name')
  match=$(echo "$entry" | jq -r '.match // ""')
  algo=$(echo "$entry" | jq -r '.matching_algorithm // 0')
  insensitive=$(echo "$entry" | jq -r '.is_insensitive // true')
  desc=$(echo "$entry" | jq -r '.document_ai_description // ""')

  existing_id=$(find_id "document_types" "$name")

  if [[ -n "$existing_id" ]]; then
    skip "$name (ID=$existing_id)"
  else
    body=$(jq -n \
      --arg n "$name" --arg m "$match" \
      --argjson a "$algo" --argjson i "$insensitive" \
      '{"name":$n,"match":$m,"matching_algorithm":$a,"is_insensitive":$i}')
    result=$(p_post "document_types/" "$body")
    existing_id=$(echo "$result" | jq -r '.id')
    ok "$name (ID=$existing_id)"
    (( dok_count++ )) || true
  fi

  if [[ -n "$existing_id" && -n "$desc" ]]; then
    save_description "document-types" "$existing_id" "$name" "$desc"
  fi
done < <(jq -c '.[]' "$dok_file")

echo "  → $dok_count Dokumententypen angelegt"

# --- 2. Tags ---
echo ""
echo "[ Tags ]"
tags_file="$SCRIPT_DIR/tags.json"
tag_count=0

while IFS= read -r entry; do
  name=$(echo "$entry" | jq -r '.name')
  color=$(echo "$entry" | jq -r '.color // "#6B7280"')
  inbox=$(echo "$entry" | jq -r '.is_inbox_tag // false')
  desc=$(echo "$entry" | jq -r '.description // ""')

  existing_id=$(find_id "tags" "$name")

  if [[ -n "$existing_id" ]]; then
    skip "$name (ID=$existing_id)"
  else
    body=$(jq -n \
      --arg n "$name" --arg c "$color" --argjson i "$inbox" \
      '{"name":$n,"color":$c,"is_inbox_tag":$i}')
    result=$(p_post "tags/" "$body")
    existing_id=$(echo "$result" | jq -r '.id')
    ok "$name (ID=$existing_id)"
    (( tag_count++ )) || true
  fi

  if [[ -n "$existing_id" && -n "$desc" ]]; then
    save_description "tags" "$existing_id" "$name" "$desc"
  fi
done < <(jq -c '.[]' "$tags_file")

echo "  → $tag_count Tags angelegt"

# --- 3. Custom Fields ---
echo ""
echo "[ Custom Fields ]"
felder_file="$SCRIPT_DIR/felder.json"
feld_count=0

# Custom Fields haben keinen Name-Filter in der API → alle laden und lokal suchen
all_fields=$(p_get "custom_fields/?page_size=200")

while IFS= read -r entry; do
  name=$(echo "$entry" | jq -r '.name')
  dtype=$(echo "$entry" | jq -r '.data_type // "string"')
  desc=$(echo "$entry" | jq -r '.description // ""')

  existing_id=$(echo "$all_fields" | jq -r --arg n "$name" \
    '.results[] | select(.name == $n) | .id' 2>/dev/null | head -1)

  if [[ -n "$existing_id" ]]; then
    skip "$name (ID=$existing_id)"
  else
    body=$(jq -n --arg n "$name" --arg d "$dtype" '{"name":$n,"data_type":$d}')
    result=$(p_post "custom_fields/" "$body")
    existing_id=$(echo "$result" | jq -r '.id')
    ok "$name [$dtype] (ID=$existing_id)"
    (( feld_count++ )) || true
  fi

  if [[ -n "$existing_id" && -n "$desc" ]]; then
    save_description "custom-fields" "$existing_id" "$name" "$desc"
  fi
done < <(jq -c '.[]' "$felder_file")

echo "  → $feld_count Custom Fields angelegt"

# --- 4. Speicherpfade ---
echo ""
echo "[ Speicherpfade ]"
pfade_file="$SCRIPT_DIR/speicherpfade.json"
pfad_count=0

while IFS= read -r entry; do
  name=$(echo "$entry" | jq -r '.name')
  path=$(echo "$entry" | jq -r '.path')
  algo=$(echo "$entry" | jq -r '.matching_algorithm // 0')
  desc=$(echo "$entry" | jq -r '.description // ""')

  existing_id=$(find_id "storage_paths" "$name")

  if [[ -n "$existing_id" ]]; then
    skip "$name (ID=$existing_id)"
  else
    body=$(jq -n --arg n "$name" --arg p "$path" --argjson a "$algo" \
      '{"name":$n,"path":$p,"matching_algorithm":$a}')
    result=$(p_post "storage_paths/" "$body")
    existing_id=$(echo "$result" | jq -r '.id')
    ok "$name (ID=$existing_id)"
    (( pfad_count++ )) || true
  fi

  if [[ -n "$existing_id" && -n "$desc" ]]; then
    save_description "storage-paths" "$existing_id" "$name" "$desc"
  fi
done < <(jq -c '.[]' "$pfade_file")

echo "  → $pfad_count Speicherpfade angelegt"

# --- 5. Token in App-Einstellungen speichern ---
echo ""
echo "[ App-Einstellungen ]"
settings_body=$(jq -n \
  --arg url "$PAPERLESS_URL" \
  --arg token "$PAPERLESS_TOKEN" \
  '{"Paperless:BaseUrl": $url, "Paperless:Token": $token}')

if app_put "settings" "$settings_body" > /dev/null 2>&1; then
  ok "Paperless-URL und Token in App gespeichert"
else
  warn "App-Einstellungen konnten nicht gespeichert werden (App läuft?)"
fi

echo ""
echo "============================================"
echo " Import abgeschlossen!"
echo " Nächste Schritte:"
echo " 1. App öffnen → Einstellungen → Azure-Credentials eintragen"
echo " 2. Metadaten-Seiten aufrufen und Beschreibungen prüfen"
echo "============================================"
