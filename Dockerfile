FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props ./
COPY src/Turno.Contracts/Turno.Contracts.csproj src/Turno.Contracts/
COPY src/Turno.Core/Turno.Core.csproj src/Turno.Core/
COPY src/Turno.Infrastructure/Turno.Infrastructure.csproj src/Turno.Infrastructure/
COPY src/Turno.Worker/Turno.Worker.csproj src/Turno.Worker/
COPY src/Turno.Api/Turno.Api.csproj src/Turno.Api/
COPY src/Turno.Dashboard/Turno.Dashboard.csproj src/Turno.Dashboard/
COPY samples/Turno.Samples/Turno.Samples.csproj samples/Turno.Samples/
RUN dotnet restore src/Turno.Api/Turno.Api.csproj \
 && dotnet restore src/Turno.Worker/Turno.Worker.csproj
COPY src/ src/
COPY samples/ samples/
RUN dotnet publish src/Turno.Api/Turno.Api.csproj -c Release -o /out/api --no-restore \
 && dotnet publish src/Turno.Worker/Turno.Worker.csproj -c Release -o /out/worker --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=1
COPY --from=build /out/api .
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=30s --retries=6 CMD curl -fsS http://localhost:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "Turno.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS worker
WORKDIR /app
ENV DOTNET_gcServer=1
COPY --from=build /out/worker .
USER $APP_UID
ENTRYPOINT ["dotnet", "Turno.Worker.dll"]
