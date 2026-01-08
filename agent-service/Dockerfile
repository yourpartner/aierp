# ===========================================
# Node.js Agent Service Dockerfile
# ===========================================
FROM node:20-alpine

WORKDIR /app

# 复制依赖文件
COPY package*.json ./

# 安装依赖
RUN npm install --only=production

# 复制源代码
COPY . .

# 设置时区
ENV TZ=Asia/Tokyo

# 暴露端口
EXPOSE 3000

# 健康检查
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:3000/health || exit 1

# 启动
CMD ["node", "index.js"]

