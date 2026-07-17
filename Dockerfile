# ============================================================
# MultiAgentSystem - 后端容器
# .NET 10 MCR 镜像（国内可访问），独立于前端
# ============================================================

# ---------- Stage 1: 编译 ----------
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
RUN mkdir -p /app/data

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5000
ENV DB_PATH=/app/data/multiagent.db
EXPOSE 5000

ENTRYPOINT ["dotnet", "MultiAgentSystem.Api.dll"]
