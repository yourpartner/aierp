# ===========================================
# .NET 8.0 后端 API Dockerfile
# ===========================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# 安装 Playwright 依赖和浏览器
# 注意：在 final 镜像中也需要安装浏览器二进制文件
RUN apt-get update && apt-get install -y curl && \
    curl -sL https://deb.nodesource.com/setup_20.x | bash - && \
    apt-get install -y nodejs && \
    npx playwright install-deps chromium && \
    npx playwright install chromium && \
    rm -rf /var/lib/apt/lists/*

# 设置 Playwright 浏览器路径环境变量（如果需要）
ENV PLAYWRIGHT_BROWSERS_PATH=/root/.cache/ms-playwright

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY ["Server.csproj", "."]
RUN dotnet restore "Server.csproj"
COPY . .
RUN dotnet build "Server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# 设置时区
ENV TZ=Asia/Tokyo

# 健康检查
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:80/health || exit 1

ENTRYPOINT ["dotnet", "Server.dll"]
