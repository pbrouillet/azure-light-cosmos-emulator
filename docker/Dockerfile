FROM node:20-bookworm-slim AS node

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY --from=node /usr/local/ /usr/local/

COPY ["Azure.Cosmos.LightEmulator.slnx", "Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["src/Auth/Azure.Cosmos.LightEmulator.Auth.csproj", "src/Auth/"]
COPY ["src/Cli/Azure.Cosmos.LightEmulator.Cli.csproj", "src/Cli/"]
COPY ["src/Core/Azure.Cosmos.LightEmulator.Core.csproj", "src/Core/"]
COPY ["src/Host/Azure.Cosmos.LightEmulator.Host.csproj", "src/Host/"]
COPY ["src/MongoDB/Azure.Cosmos.LightEmulator.MongoDB.csproj", "src/MongoDB/"]
COPY ["src/NoSql/Azure.Cosmos.LightEmulator.NoSql.csproj", "src/NoSql/"]
COPY ["src/Storage/Azure.Cosmos.LightEmulator.Storage.csproj", "src/Storage/"]
COPY ["src/Triggers/Azure.Cosmos.LightEmulator.Triggers.csproj", "src/Triggers/"]
COPY ["tests/Auth.Tests/Azure.Cosmos.LightEmulator.Auth.Tests.csproj", "tests/Auth.Tests/"]
COPY ["tests/Core.Tests/Azure.Cosmos.LightEmulator.Core.Tests.csproj", "tests/Core.Tests/"]
COPY ["tests/MongoDB.Tests/Azure.Cosmos.LightEmulator.MongoDB.Tests.csproj", "tests/MongoDB.Tests/"]
COPY ["tests/NoSql.Tests/Azure.Cosmos.LightEmulator.NoSql.Tests.csproj", "tests/NoSql.Tests/"]
COPY ["tests/Parity/Azure.Cosmos.LightEmulator.Parity.csproj", "tests/Parity/"]
COPY ["tests/Storage.Tests/Azure.Cosmos.LightEmulator.Storage.Tests.csproj", "tests/Storage.Tests/"]

RUN dotnet restore src/Host/Azure.Cosmos.LightEmulator.Host.csproj

COPY . .

RUN if [ -f src/Explorer/package.json ]; then \
      cd src/Explorer && npm ci && npm run build; \
    fi

RUN dotnet build src/Host/Azure.Cosmos.LightEmulator.Host.csproj -c Release --no-restore
RUN dotnet publish src/Host/Azure.Cosmos.LightEmulator.Host.csproj -c Release -o /app/publish --no-build

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8081 10255
ENV ASPNETCORE_URLS=http://+:8081
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "Azure.Cosmos.LightEmulator.Host.dll"]
