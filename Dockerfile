# ── Stage 1: Build React frontend ──
FROM node:22-alpine AS frontend-build
WORKDIR /app/web
COPY src/RepoLens.Web/package.json src/RepoLens.Web/package-lock.json* ./
RUN npm ci
COPY src/RepoLens.Web/ .
RUN npm run build

# ── Stage 2: Build .NET API ──
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
WORKDIR /src

# Copy csproj files first for layer-cached restore
COPY src/RepoLens.Api/RepoLens.Api.csproj RepoLens.Api/
COPY src/RepoLens.Analysis/RepoLens.Analysis.csproj RepoLens.Analysis/
COPY src/RepoLens.Engine/RepoLens.Engine.csproj RepoLens.Engine/
COPY src/RepoLens.Shared/RepoLens.Shared.csproj RepoLens.Shared/
RUN dotnet restore RepoLens.Api/RepoLens.Api.csproj

# Copy full source and publish
COPY src/RepoLens.Api/ RepoLens.Api/
COPY src/RepoLens.Analysis/ RepoLens.Analysis/
COPY src/RepoLens.Engine/ RepoLens.Engine/
COPY src/RepoLens.Shared/ RepoLens.Shared/
RUN dotnet publish RepoLens.Api/RepoLens.Api.csproj -c Release -o /app/publish --no-restore

# ── Stage 3: Runtime ──
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Copy published API
COPY --from=backend-build /app/publish .

# Copy built frontend into wwwroot
COPY --from=frontend-build /app/web/dist ./wwwroot

# Cache directory (mount a volume here for persistence)
RUN mkdir -p /app/cache
ENV REPOLENS_CACHE_DIR=/app/cache

EXPOSE 5000
# Default to port 5000; Render overrides via PORT env var
ENV PORT=5000
ENV ASPNETCORE_ENVIRONMENT=Production

# Shell form so $PORT is expanded at runtime
CMD dotnet RepoLens.Api.dll --urls "http://+:$PORT"
