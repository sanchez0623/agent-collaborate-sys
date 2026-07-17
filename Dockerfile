# ============================================================
# MultiAgentSystem - 单容器部署
# 仅依赖 MCR 镜像（国内可访问），无需 Docker Hub
# 构建前：cd frontend && npm run build
# ============================================================

# ---------- Stage 1: 编译后端 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MultiAgentSystem.Api/*.csproj .
# 本地开发用 net11.0，Docker 用 net10.0 SDK，临时替换
RUN sed -i 's/net11.0/net10.0/g' MultiAgentSystem.Api.csproj
RUN dotnet restore
COPY src/MultiAgentSystem.Api/ .
RUN sed -i 's/net11.0/net10.0/g' MultiAgentSystem.Api.csproj
RUN dotnet publish -c Release -o /app

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
