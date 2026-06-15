#!/usr/bin/env bash
# PaperlessAI – Docker Image bauen und in eine Registry pushen
# Verwendung:
#   bash push-image.sh <registry/name> [tag]
#
# Beispiele:
#   bash push-image.sh ghcr.io/marcelbarner/paperless-ai
#   bash push-image.sh ghcr.io/marcelbarner/paperless-ai v1.2.0
#   bash push-image.sh myregistry.example.com/paperless-ai latest
set -euo pipefail

IMAGE="${1:-}"
TAG="${2:-latest}"

if [ -z "$IMAGE" ]; then
  echo "Verwendung: bash push-image.sh <registry/image-name> [tag]"
  echo "Beispiel:   bash push-image.sh ghcr.io/marcelbarner/paperless-ai v1.0.0"
  exit 1
fi

FULL_TAG="${IMAGE}:${TAG}"

# Git-Commit als zusätzlichen Tag
GIT_TAG="${IMAGE}:$(git rev-parse --short HEAD 2>/dev/null || echo 'local')"

echo "======================================="
echo " PaperlessAI – Image Build & Push"
echo " Image: $FULL_TAG"
echo " Git:   $GIT_TAG"
echo "======================================="

echo "[*] Baue Docker-Image..."
docker build -t "$FULL_TAG" -t "$GIT_TAG" .

echo "[*] Pushe $FULL_TAG ..."
docker push "$FULL_TAG"

echo "[*] Pushe $GIT_TAG ..."
docker push "$GIT_TAG"

echo ""
echo "======================================="
echo " Fertig!"
echo " $FULL_TAG"
echo " $GIT_TAG"
echo "======================================="
