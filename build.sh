#!/usr/bin/env bash
# Baut Backend + Frontend und legt alles in ./publish/ ab
set -euo pipefail

echo "=== Build Backend ==="
dotnet publish src/PaperlessAI.API/PaperlessAI.API.csproj \
  -c Release \
  -o ./publish \
  --no-self-contained \
  -r linux-x64

echo "=== Build Frontend ==="
cd src/paperless-ai-frontend
npm ci
npx ng build --configuration production
cd ../..

echo "=== Fertig ==="
echo "Backend: ./publish/"
echo "Frontend: ./src/paperless-ai-frontend/dist/paperless-ai-frontend/browser/"
