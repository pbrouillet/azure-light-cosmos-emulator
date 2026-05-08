FROM node:24-alpine AS spa-build
WORKDIR /spa

COPY src/Explorer/package.json src/Explorer/package-lock.json ./
RUN npm ci

COPY src/Explorer/ ./
RUN npm run build -- --outDir /spa/dist --emptyOutDir

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /src

COPY --link ["Azure.Cosmos.LightEmulator.slnx", \
             "Directory.Build.props", \
             "Directory.Packages.props", \
             "global.json", "./"]
COPY --link src/Auth/Azure.Cosmos.LightEmulator.Auth.csproj         src/Auth/
COPY --link src/Cli/Azure.Cosmos.LightEmulator.Cli.csproj           src/Cli/
COPY --link src/Core/Azure.Cosmos.LightEmulator.Core.csproj         src/Core/
COPY --link src/Host/Azure.Cosmos.LightEmulator.Host.csproj         src/Host/
COPY --link src/Kql/Azure.Cosmos.LightEmulator.Kql.csproj           src/Kql/
COPY --link src/MongoDB/Azure.Cosmos.LightEmulator.MongoDB.csproj   src/MongoDB/
COPY --link src/NoSql/Azure.Cosmos.LightEmulator.NoSql.csproj       src/NoSql/
COPY --link src/Storage/Azure.Cosmos.LightEmulator.Storage.csproj   src/Storage/
COPY --link src/Triggers/Azure.Cosmos.LightEmulator.Triggers.csproj src/Triggers/

RUN dotnet restore -a $TARGETARCH src/Host/Azure.Cosmos.LightEmulator.Host.csproj

COPY --link --from=spa-build /spa/dist/ src/Host/wwwroot/explorer/
COPY --link src/ src/

RUN dotnet publish -a $TARGETARCH -c Release --no-restore \
      -o /app/publish \
      -p:DebugType=none \
      -p:DebugSymbols=false \
      -p:GenerateDocumentationFile=false \
      -p:UseAppHost=false \
      -p:SkipExplorerBuild=true \
      src/Host/Azure.Cosmos.LightEmulator.Host.csproj \
 && rm -rf /app/publish/wwwroot/explorer


FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

COPY --link --chown=$APP_UID:$APP_UID --from=build /app/publish .

EXPOSE 8081 10255
ENV ASPNETCORE_URLS=http://+:8081

# Persistent data volume. The orchestrator must initialise it with UID
# 1654 ownership (see docker-compose.yml `cosmos-data-init` service).
VOLUME ["/data"]

# USER instruction is implicit: chiseled images run as 1654 by default.
ENTRYPOINT ["dotnet", "Azure.Cosmos.LightEmulator.Host.dll"]
