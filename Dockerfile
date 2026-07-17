# ============================================================
# MultiAgentSystem.Api - Docker 多阶段构建
# .NET 10 LTS (MCR 镜像，兼容国内镜像源), 端口 5000
# ============================================================

# ---------- Stage 1: 编译 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MultiAgentSystem.Api/*.csproj .
RUN dotnet restore -f net10.0
COPY src/MultiAgentSystem.Api/ .
RUN dotnet publish -c Release -f net10.0 -o /app

# ---------- Stage 2: 运行 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir -p /app/data
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5000
ENV DB_PATH=/app/data/multiagent.db
EXPOSE 5000

ENTRYPOINT ["dotnet", "MultiAgentSystem.Api.dll"]
