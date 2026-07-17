# ============================================================
# MultiAgentSystem.Api - Docker 多阶段构建
# .NET 11, 端口 5000
# ============================================================

# ---------- Stage 1: 编译 ----------
FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
WORKDIR /src
COPY src/MultiAgentSystem.Api/*.csproj .
RUN dotnet restore
COPY src/MultiAgentSystem.Api/ .
RUN dotnet publish -c Release -o /app

# ---------- Stage 2: 运行 ----------
FROM mcr.microsoft.com/dotnet/aspnet:11.0-preview
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "MultiAgentSystem.Api.dll"]
