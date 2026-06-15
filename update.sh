#!/usr/bin/env bash
# PaperlessAI – Update (Docker)
# Ausführen mit:  paperless-ai-update
set -euo pipefail

APP_DIR="/opt/paperless-ai"
SKIP_PULL="${1:-}"

cd "$APP_DIR"

echo "======================================="
echo " PaperlessAI – Update"
echo "======================================="

# Git Pull
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

# PaperlessAI neu bauen und starten (andere Services laufen weiter)
echo "[*] Baue PaperlessAI Docker-Image..."
docker compose build paperless-ai

echo "[*] Starte PaperlessAI neu..."
docker compose up -d --no-deps paperless-ai

sleep 3
if docker compose ps paperless-ai | grep -q "running\|Up"; then
  VERSION=$(git log -1 --format='%h – %s')
  echo ""
  echo "======================================="
  echo " Update abgeschlossen!"
  echo " Version: $VERSION"
  echo "======================================="
else
  echo "FEHLER: Container nicht gestartet."
  docker compose logs --tail=20 paperless-ai
  exit 1
fi
