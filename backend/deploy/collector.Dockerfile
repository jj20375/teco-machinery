# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props ./
# 先只複製 .csproj 做 restore，跟下面複製完整原始碼/lib 的 layer 分開——這樣改 .cs 檔不會讓
# restore（連網解析 NuGet）的 cache 失效，只有動到套件相依時才會重新 restore。
# lib/ 底下是廠商 DLL，用 HintPath 相對路徑參照，restore 階段不需要實體檔案存在，
# 放到 publish 前複製即可，不影響 restore layer 的快取命中率。
COPY src/Teco.Hvac.Contracts/*.csproj src/Teco.Hvac.Contracts/
COPY src/Teco.Hvac.Domain/*.csproj src/Teco.Hvac.Domain/
COPY src/Teco.Hvac.Infrastructure/*.csproj src/Teco.Hvac.Infrastructure/
COPY src/Teco.Hvac.Collector/*.csproj src/Teco.Hvac.Collector/
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Teco.Hvac.Collector/Teco.Hvac.Collector.csproj

COPY lib/ ./lib/
COPY src/Teco.Hvac.Contracts/ src/Teco.Hvac.Contracts/
COPY src/Teco.Hvac.Domain/ src/Teco.Hvac.Domain/
COPY src/Teco.Hvac.Infrastructure/ src/Teco.Hvac.Infrastructure/
COPY src/Teco.Hvac.Collector/ src/Teco.Hvac.Collector/
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Teco.Hvac.Collector/Teco.Hvac.Collector.csproj \
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
