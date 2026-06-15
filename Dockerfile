# ── Stage 1: Angular Frontend bauen ──────────────────────────
FROM node:20-alpine AS frontend-build
WORKDIR /app
COPY src/paperless-ai-frontend/package*.json ./
RUN npm ci --silent
COPY src/paperless-ai-frontend/ ./
RUN npx ng build --configuration production

# ── Stage 2: .NET Backend bauen ──────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY src/PaperlessAI.API/ ./PaperlessAI.API/
RUN dotnet publish PaperlessAI.API/PaperlessAI.API.csproj \
    -c Release -o /publish --no-self-contained

# ── Stage 3: Runtime-Image ───────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=backend-build /publish ./
COPY --from=frontend-build /app/dist/paperless-ai-frontend/browser ./wwwroot/

VOLUME /app/data
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_PRINT_TELEMETRY_MESSAGE=false

ENTRYPOINT ["dotnet", "PaperlessAI.API.dll"]
