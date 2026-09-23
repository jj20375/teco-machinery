# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY src/Teco.Hvac.Contracts/ ./src/Teco.Hvac.Contracts/
COPY src/Teco.Hvac.Domain/ ./src/Teco.Hvac.Domain/
COPY src/Teco.Hvac.Infrastructure/ ./src/Teco.Hvac.Infrastructure/
COPY src/Teco.Hvac.Api/ ./src/Teco.Hvac.Api/

RUN dotnet restore src/Teco.Hvac.Api/Teco.Hvac.Api.csproj
RUN dotnet publish src/Teco.Hvac.Api/Teco.Hvac.Api.csproj \
    -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV TZ=Asia/Taipei
ENV ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
# aspnet 基礎映像檔沒有 curl，但 compose.yaml 的 healthcheck 要用它探測 /readyz。
# 沒裝的話 healthcheck 永遠報 unhealthy（exec: "curl": executable file not found），
# 即使程式本身完全正常——這是實測抓到的問題，不是猜測。
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Teco.Hvac.Api.dll"]
