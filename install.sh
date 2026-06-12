#!/usr/bin/env bash
# PaperlessAI – Erstinstallation
# Einmalig auf dem LXC ausführen:
#   curl -fsSL https://raw.githubusercontent.com/marcelbarner/paperless-ai-extended/main/install.sh | bash
set -euo pipefail

REPO="https://github.com/marcelbarner/paperless-ai-extended.git"
APP_DIR="/opt/paperless-ai"
SERVICE_USER="paperlessai"

echo "======================================="
echo " PaperlessAI – Erstinstallation"
echo "======================================="

# --- Abhängigkeiten ---
apt-get update -qq
apt-get install -y -qq curl git nginx

# .NET 10 SDK
if ! command -v dotnet &>/dev/null; then
  echo "[*] Installiere .NET 10 SDK..."
  curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb \
    -o /tmp/ms-prod.deb && dpkg -i /tmp/ms-prod.deb
  apt-get update -qq && apt-get install -y -qq dotnet-sdk-10.0
fi

# Node.js 20
if ! command -v node &>/dev/null; then
  echo "[*] Installiere Node.js 20..."
  curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
  apt-get install -y -qq nodejs
fi

# --- Service-User ---
id "$SERVICE_USER" &>/dev/null || useradd --system --no-create-home --shell /bin/false "$SERVICE_USER"

# --- Repo ---
if [ -d "$APP_DIR/.git" ]; then
  echo "[*] Repo bereits vorhanden – überspringe Clone"
else
  echo "[*] Klone Repository..."
  git clone "$REPO" "$APP_DIR"
fi

mkdir -p "$APP_DIR/data"

# --- Bauen ---
bash "$APP_DIR/update.sh" --skip-pull

# --- nginx ---
cat > /etc/nginx/sites-available/paperless-ai << 'EOF'
server {
    listen 80;
    server_name _;
    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_set_header   Host $host;
        proxy_set_header   X-Real-IP $remote_addr;
        proxy_read_timeout 120s;
    }
}
EOF
ln -sf /etc/nginx/sites-available/paperless-ai /etc/nginx/sites-enabled/
rm -f /etc/nginx/sites-enabled/default
nginx -t && systemctl reload nginx

# --- systemd ---
cat > /etc/systemd/system/paperless-ai.service << EOF
[Unit]
Description=PaperlessAI
After=network.target

[Service]
Type=simple
User=$SERVICE_USER
WorkingDirectory=$APP_DIR/publish
ExecStart=/usr/bin/dotnet $APP_DIR/publish/PaperlessAI.API.dll
Restart=on-failure
RestartSec=10
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now paperless-ai

# --- Update-Befehl global verfügbar machen ---
ln -sf "$APP_DIR/update.sh" /usr/local/bin/paperless-ai-update
chmod +x /usr/local/bin/paperless-ai-update

IP=$(hostname -I | awk '{print $1}')
echo ""
echo "======================================="
echo " Fertig!"
echo " Öffnen:  http://$IP"
echo " Update:  paperless-ai-update"
echo " Logs:    journalctl -u paperless-ai -f"
echo "======================================="
