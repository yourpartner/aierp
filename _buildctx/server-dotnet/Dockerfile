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

# Playwright 的 .NET 包内部 CLI 是 Node 脚本；在 build/publish 阶段安装 Node，
# 然后把下载好的浏览器目录拷贝到 final 镜像，避免 final 镜像也要装 Node。
RUN apt-get update && apt-get install -y nodejs npm && rm -rf /var/lib/apt/lists/*

COPY ["Server.csproj", "."]
RUN dotnet restore "Server.csproj"
COPY . .
RUN dotnet build "Server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# 安装与 Microsoft.Playwright 版本匹配的浏览器到 /ms-playwright
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN mkdir -p /ms-playwright && \
    node /app/publish/.playwright/package/cli.js install chromium

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=publish /ms-playwright /ms-playwright

# 设置时区
ENV TZ=Asia/Tokyo

# 健康检查
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:80/health || exit 1

ENTRYPOINT ["dotnet", "Server.dll"]
