# Build context is the backend repo root:  docker build -f services/gateway/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY building-blocks building-blocks
COPY services/gateway/src services/gateway/src
RUN dotnet publish services/gateway/src/Gateway.Api/Gateway.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Gateway.Api.dll"]
