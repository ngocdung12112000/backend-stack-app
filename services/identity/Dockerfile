# Build context is the backend repo root:  docker build -f services/identity/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY building-blocks building-blocks
COPY services/identity/src services/identity/src
RUN dotnet publish services/identity/src/Identity.Api/Identity.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Writable dir for the dev-only generated signing key (mounted as a volume in docker-compose).
RUN mkdir /keys && chown $APP_UID /keys
USER $APP_UID
ENTRYPOINT ["dotnet", "Identity.Api.dll"]
