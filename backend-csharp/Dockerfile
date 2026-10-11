# ChatBranch API — ASP.NET Core 8 over SQLite.
#
# SQLite means the database is a FILE, so the data directory must be a volume:
# without one, every redeploy starts from an empty database that re-seeds the
# demo users. See docker-compose.yml (chatbranch-data).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore against the project files alone, so a code-only change reuses the
# cached restore layer.
COPY ChatBranch.sln ./
COPY src/ChatBranch.Api/ChatBranch.Api.csproj src/ChatBranch.Api/
COPY src/ChatBranch.Data/ChatBranch.Data.csproj src/ChatBranch.Data/
COPY src/ChatBranch.Domain/ChatBranch.Domain.csproj src/ChatBranch.Domain/
COPY tests/ChatBranch.Tests/ChatBranch.Tests.csproj tests/ChatBranch.Tests/
RUN dotnet restore ChatBranch.sln

COPY . .
RUN dotnet publish src/ChatBranch.Api -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# The API writes: the SQLite file, and media under the data dir. Both live in
# /data so a single volume covers all persistent state.
ENV ASPNETCORE_URLS=http://0.0.0.0:8090 \
    ConnectionStrings__Default="Data Source=/data/chatbranch.sqlite3" \
    DataDir=/data
# curl is the healthcheck client — the base aspnet:8.0 image ships neither
# curl nor wget, so the old wget-based HEALTHCHECK always failed and the
# container reported "unhealthy" despite serving fine. ffmpeg turns Ogg/Opus
# and AMR voice notes into M4A for Safari / iOS on request (the signed media
# link's ?format=m4a; MEDIA_FFMPEG=off disables it). Install both while root.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ffmpeg \
    && rm -rf /var/lib/apt/lists/*

# The aspnet:8.0 image already ships a non-root `app` user — reuse it rather
# than creating a second one (useradd fails on the existing name).
RUN mkdir -p /data && chown -R app /data /app
USER app

COPY --from=build /app/publish ./
EXPOSE 8090

# The status endpoint is anonymous by design, so it doubles as the healthcheck.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["/bin/sh", "-c", "curl -fsS http://127.0.0.1:8090/api/whatsapp/status >/dev/null || exit 1"]

ENTRYPOINT ["dotnet", "ChatBranch.Api.dll"]
