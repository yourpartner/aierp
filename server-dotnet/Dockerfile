# ===========================================
# .NET 8.0 后端 API Dockerfile
# ===========================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# 安装 Playwright 运行依赖（浏览器二进制后续用 Microsoft.Playwright 自带脚本安装，确保版本匹配）
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN apt-get update && apt-get install -y \
    curl \
    libglib2.0-0 \
    libnss3 \
    libnspr4 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libdbus-1-3 \
    libxcb1 \
    libxkbcommon0 \
    libatspi2.0-0 \
    libx11-6 \
    libxcomposite1 \
    libxdamage1 \
    libxext6 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libpango-1.0-0 \
    libcairo2 \
    libasound2 \
    && rm -rf /var/lib/apt/lists/*

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

# 使用 Microsoft.Playwright 自带 CLI 安装浏览器（避免 npx Playwright 与 .NET Playwright 浏览器版本不一致）
# 需要将浏览器下载安装到 PLAYWRIGHT_BROWSERS_PATH，供运行时查找，例如 /ms-playwright/chromium-1140/...
RUN mkdir -p /ms-playwright && \
    chmod -R 777 /ms-playwright && \
    node ./.playwright/package/cli.js install chromium

# 设置时区
ENV TZ=Asia/Tokyo

# 健康检查
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:80/health || exit 1

ENTRYPOINT ["dotnet", "Server.dll"]
