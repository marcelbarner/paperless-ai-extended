#!/usr/bin/env bash
# PaperlessAI – Update
# Ausführen mit:  paperless-ai-update
set -euo pipefail

APP_DIR="/opt/paperless-ai"
SERVICE_USER="paperlessai"
SKIP_PULL="${1:-}"

cd "$APP_DIR"

echo "======================================="
echo " PaperlessAI – Update"
echo "======================================="

# --- Git Pull ---
if [ "$SKIP_PULL" != "--skip-pull" ]; then
  echo "[*] Ziehe Updates von GitHub..."
  OLD=$(git rev-parse --short HEAD)
  git pull
  NEW=$(git rev-parse --short HEAD)
  if [ "$OLD" = "$NEW" ]; then
    echo "[*] Bereits aktuell ($NEW) – baue trotzdem neu"
  else
    echo "[*] $OLD → $NEW"
  fi
fi

# --- Backend bauen ---
echo "[*] Baue Backend..."
cd "$APP_DIR/src/PaperlessAI.API"
dotnet publish -c Release -o "$APP_DIR/publish" --no-self-contained -r linux-x64 -q

# --- Frontend bauen ---
echo "[*] Baue Frontend..."
cd "$APP_DIR/src/paperless-ai-frontend"
npm ci --silent
npx ng build --configuration production --no-progress 2>&1 | tail -3

# --- Frontend in publish/wwwroot ---
echo "[*] Kopiere Frontend..."
mkdir -p "$APP_DIR/publish/wwwroot"
cp -r "$APP_DIR/src/paperless-ai-frontend/dist/paperless-ai-frontend/browser/." \
      "$APP_DIR/publish/wwwroot/"

# --- Berechtigungen + Neustart ---
chown -R "$SERVICE_USER:$SERVICE_USER" "$APP_DIR/publish"
chown -R "$SERVICE_USER:$SERVICE_USER" "$APP_DIR/data"

if systemctl is-active --quiet paperless-ai 2>/dev/null; then
  echo "[*] Starte Dienst neu..."
  systemctl restart paperless-ai
  sleep 2
  systemctl is-active --quiet paperless-ai \
    && echo "[✓] Dienst läuft" \
    || { echo "[✗] Dienst nicht gestartet:"; journalctl -u paperless-ai -n 10 --no-pager; exit 1; }
fi

VERSION=$(git log -1 --format='%h – %s')
echo ""
echo "======================================="
echo " Update abgeschlossen!"
echo " Version: $VERSION"
echo "======================================="
