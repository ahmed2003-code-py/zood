# ZAWED API (ASP.NET Core) image. Railway builds it from this folder (rootDirectory apps/api-dotnet,
# builder DOCKERFILE), exactly as it builds apps/api today. Tests are not run here: they need Docker
# (Testcontainers) and run in CI. ADR 0029.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
# Restore first, so a source-only change reuses this layer.
COPY global.json Directory.Build.props Directory.Packages.props Zawed.slnx ./
COPY src/Zawed.Domain/Zawed.Domain.csproj src/Zawed.Domain/
COPY src/Zawed.Api/Zawed.Api.csproj src/Zawed.Api/
RUN dotnet restore src/Zawed.Api/Zawed.Api.csproj
COPY db db
COPY src src
RUN dotnet publish src/Zawed.Api/Zawed.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The image's non-root user (uid 1654).
USER $APP_UID
# Railway sets PORT; the API listens on it (8080 when unset). The same variables as the Java API:
# SPRING_PROFILES_ACTIVE=deployed, ZAWED_*, PG* (ADR 0029, README).
ENV DOTNET_gcServer=0 \
    DOTNET_GCHeapHardLimitPercent=0x4B \
    ASPNETCORE_HTTP_PORTS=
EXPOSE 8080
ENTRYPOINT ["dotnet", "Zawed.Api.dll"]
