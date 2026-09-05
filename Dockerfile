# Multi-stage build: web SPA -> API publish -> single runtime image that serves
# both the static frontend and the API on port 8080.

# --- Stage 1: build the web SPA -------------------------------------------
FROM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
# tsc runs via the build script; NODE_ENV pinned for reproducible deps above.
RUN npm run build

# --- Stage 2: build the API ------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
WORKDIR /src
COPY TsukiAI.sln ./
COPY TsukiAI.Core/ TsukiAI.Core/
COPY TsukiAI.Api/ TsukiAI.Api/
RUN dotnet publish TsukiAI.Api -c Release -o /app/publish

# --- Stage 3: runtime -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=api /app/publish ./
COPY --from=web /src/web/dist ./wwwroot
# Persona style anchor: PromptBuilder injects the Modelfile's PERSONALITY/SPEECH
# lines into every system prompt (third candidate path: AppContext.BaseDirectory/assets).
COPY assets/Modelfile ./assets/Modelfile

# Data dir for settings/history/provider state (mount a volume here).
ENV TSUKI_DATA_DIR=/data \
    ASPNETCORE_URLS=http://+:8080

RUN useradd --uid 1000 --no-create-home appuser \
    && mkdir -p /data \
    && chown -R appuser /data /app
USER appuser

EXPOSE 8080
ENTRYPOINT ["dotnet", "TsukiAI.Api.dll"]
