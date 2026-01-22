# ===========================================
# Vue 3 前端 Dockerfile (Nginx 静态托管)
# ===========================================
FROM node:20-slim AS build

WORKDIR /app

# 复制依赖文件
COPY package*.json ./

# 安装依赖（Windows 生成的 lock 可能缺少 Linux 可选依赖，导致 rollup 原生模块缺失）
RUN rm -f package-lock.json && npm install --include=optional --no-audit --no-fund

# 复制源代码
COPY . .

# 构建参数（可在构建时传入 API 地址）
ARG VITE_API_BASE_URL=/api
ENV VITE_API_BASE_URL=${VITE_API_BASE_URL}

# 调试信息
RUN echo "Node version: $(node --version)" && echo "NPM version: $(npm --version)"

# 增加 Node 内存限制并构建
ENV NODE_OPTIONS="--max-old-space-size=4096"
RUN npm run build

# ===========================================
# 生产阶段 - 使用 Nginx
# ===========================================
FROM nginx:alpine AS production

# 复制构建产物
COPY --from=build /app/dist /usr/share/nginx/html

# 复制 Nginx 配置
COPY nginx.conf /etc/nginx/conf.d/default.conf

# 构建期校验 nginx 配置，避免因语法错误导致容器启动即退出（Azure 会直接报 Application Error）
RUN nginx -t

# 暴露端口
EXPOSE 80

# 健康检查
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:80/ || exit 1

CMD ["nginx", "-g", "daemon off;"]

