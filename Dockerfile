# ============================================================
# MultiAgentSystem - 单容器部署
# 前提：先在本地构建前端 → cd frontend && npm run build
# 后端使用 MCR 镜像（国内可访问），无需 Docker Hub
# ============================================================

# ---------- Stage 1: 编译后端 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MultiAgentSystem.Api/*.csproj .
RUN dotnet restore -p:TargetFramework=net10.0
COPY src/MultiAgentSystem.Api/ .
RUN dotnet publish -c Release -p:TargetFramework=net10.0 -o /app

# ---------- Stage 2: 运行 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir -p /app/data /app/wwwroot

COPY --from=build /app .
COPY frontend/dist ./wwwroot

ENV ASPNETCORE_URLS=http://+:5000
ENV DB_PATH=/app/data/multiagent.db
EXPOSE 5000

ENTRYPOINT ["dotnet", "MultiAgentSystem.Api.dll"]
