#!/usr/bin/env bash
# PaperlessAI – Erstinstallation (Docker)
# Einmalig auf dem LXC/Server ausführen:
#   curl -fsSL https://raw.githubusercontent.com/marcelbarner/paperless-ai-extended/main/install.sh | bash
set -euo pipefail

REPO="https://github.com/marcelbarner/paperless-ai-extended.git"
APP_DIR="/opt/paperless-ai"

echo "======================================="
echo " PaperlessAI – Erstinstallation"
echo "======================================="

# Docker installieren falls nicht vorhanden
if ! command -v docker &>/dev/null; then
  echo "[*] Installiere Docker..."
  curl -fsSL https://get.docker.com | sh
fi

# Repo klonen
if [ -d "$APP_DIR/.git" ]; then
  echo "[*] Repo bereits vorhanden"
else
  echo "[*] Klone Repository..."
  git clone "$REPO" "$APP_DIR"
fi

cd "$APP_DIR"

# Secret Key für Paperless generieren falls noch nicht gesetzt
if grep -q "change-me-use-a-long-random-string" docker-compose.env 2>/dev/null; then
  SECRET=$(python3 -c "import secrets; print(secrets.token_hex(32))")
  sed -i "s/change-me-use-a-long-random-string/$SECRET/" docker-compose.env
  echo "[*] Paperless Secret Key generiert"
fi

# Alle Services bauen und starten
echo "[*] Baue und starte alle Services (dauert beim ersten Mal einige Minuten)..."
docker compose up -d --build

echo ""
echo "======================================="
echo " Installation abgeschlossen!"
IP=$(hostname -I | awk '{print $1}')
echo " Paperless-NGX: http://$IP:8000"
echo " PaperlessAI:   http://$IP:8001"
echo ""
echo " Nächste Schritte:"
echo " 1. Paperless Admin-User anlegen:"
echo "    docker compose exec webserver python3 manage.py createsuperuser"
echo " 2. Paperless API-Token holen und in PaperlessAI unter :8001/settings eintragen"
echo " 3. Azure-Credentials unter :8001/settings eintragen"
echo " 4. Daten importieren:"
echo "    bash /opt/paperless-ai/data/import.sh \\"
echo "      --paperless-url http://webserver:8000 \\"
echo "      --paperless-token <TOKEN> \\"
echo "      --app-url http://localhost:5000"
echo ""
echo " Update: paperless-ai-update"
echo "======================================="

# Update-Befehl global verfügbar machen
ln -sf "$APP_DIR/update.sh" /usr/local/bin/paperless-ai-update
chmod +x /usr/local/bin/paperless-ai-update
