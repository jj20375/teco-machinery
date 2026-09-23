# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY lib/ ./lib/
COPY src/Teco.Hvac.Contracts/ ./src/Teco.Hvac.Contracts/
COPY src/Teco.Hvac.Domain/ ./src/Teco.Hvac.Domain/
COPY src/Teco.Hvac.Infrastructure/ ./src/Teco.Hvac.Infrastructure/
COPY src/Teco.Hvac.Collector/ ./src/Teco.Hvac.Collector/

RUN dotnet restore src/Teco.Hvac.Collector/Teco.Hvac.Collector.csproj
RUN dotnet publish src/Teco.Hvac.Collector/Teco.Hvac.Collector.csproj \
    -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV TZ=Asia/Taipei
ENV ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
# 同 api.Dockerfile：基礎映像檔沒有 curl，compose.yaml 的 healthcheck 要用，先裝起來。
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Teco.Hvac.Collector.dll"]
