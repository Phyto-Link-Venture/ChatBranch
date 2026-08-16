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
# The aspnet:8.0 image already ships a non-root `app` user — reuse it rather
# than creating a second one (useradd fails on the existing name).
RUN mkdir -p /data && chown -R app /data /app
USER app

COPY --from=build /app/publish ./
EXPOSE 8090

# The status endpoint is anonymous by design, so it doubles as the healthcheck.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["/bin/sh", "-c", "wget -qO- http://127.0.0.1:8090/api/whatsapp/status >/dev/null || exit 1"]

ENTRYPOINT ["dotnet", "ChatBranch.Api.dll"]
