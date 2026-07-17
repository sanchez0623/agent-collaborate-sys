# ============================================================
# MultiAgentSystem.Api - Docker 多阶段构建
# .NET 11 Preview (nightly), 端口 5000
# 如遇镜像拉取失败，先执行: docker pull docker.io/dotnet/nightly/sdk:11.0-preview
# ============================================================

# ---------- Stage 1: 编译 ----------
FROM docker.io/dotnet/nightly/sdk:11.0-preview AS build
WORKDIR /src
COPY src/MultiAgentSystem.Api/*.csproj .
RUN dotnet restore
COPY src/MultiAgentSystem.Api/ .
RUN dotnet publish -c Release -o /app

# ---------- Stage 2: 运行 ----------
FROM docker.io/dotnet/nightly/aspnet:11.0-preview
WORKDIR /app
RUN mkdir -p /app/data
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5000
ENV DB_PATH=/app/data/multiagent.db
EXPOSE 5000

ENTRYPOINT ["dotnet", "MultiAgentSystem.Api.dll"]
