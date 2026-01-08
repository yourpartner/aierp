# ===========================================
# Azure 资源初始化脚本 (PowerShell)
# ===========================================
# 运行前请先登录: az login
# ===========================================

param(
    [string]$ResourceGroup = "yanxia-rg",
    [string]$Location = "japaneast",
    [string]$AcrName = "yanxiaacr",
    [string]$DbServerName = "yanxia-db-server",
    [string]$DbAdminPassword = "",  # 必填
    [string]$AppServicePlan = "yanxia-plan"
)

# 检查参数
if (-not $DbAdminPassword) {
    Write-Host "错误: 请提供数据库密码 -DbAdminPassword" -ForegroundColor Red
    exit 1
}

Write-Host "=== 开始创建 Azure 资源 ===" -ForegroundColor Green

# 1. 创建资源组
Write-Host "`n[1/7] 创建资源组 $ResourceGroup..." -ForegroundColor Yellow
az group create --name $ResourceGroup --location $Location

# 2. 创建 Azure Container Registry
Write-Host "`n[2/7] 创建 Container Registry $AcrName..." -ForegroundColor Yellow
az acr create --resource-group $ResourceGroup --name $AcrName --sku Basic --admin-enabled true

# 获取 ACR 凭据
$acrCredentials = az acr credential show --name $AcrName | ConvertFrom-Json
$acrUsername = $acrCredentials.username
$acrPassword = $acrCredentials.passwords[0].value

Write-Host "  ACR 用户名: $acrUsername" -ForegroundColor Cyan
Write-Host "  ACR 密码: $acrPassword" -ForegroundColor Cyan

# 3. 创建 PostgreSQL Flexible Server
Write-Host "`n[3/7] 创建 PostgreSQL 数据库 $DbServerName..." -ForegroundColor Yellow
az postgres flexible-server create `
    --resource-group $ResourceGroup `
    --name $DbServerName `
    --location $Location `
    --admin-user postgres `
    --admin-password $DbAdminPassword `
    --sku-name Standard_B1ms `
    --tier Burstable `
    --storage-size 32 `
    --version 16 `
    --public-access 0.0.0.0

# 4. 创建 App Service Plan
Write-Host "`n[4/7] 创建 App Service Plan $AppServicePlan..." -ForegroundColor Yellow
az appservice plan create `
    --name $AppServicePlan `
    --resource-group $ResourceGroup `
    --sku B1 `
    --is-linux

# 5. 创建 Web Apps
Write-Host "`n[5/7] 创建 Web Apps..." -ForegroundColor Yellow

# API
az webapp create `
    --name yanxia-api `
    --resource-group $ResourceGroup `
    --plan $AppServicePlan `
    --deployment-container-image-name "mcr.microsoft.com/dotnet/aspnet:8.0"

# Agent
az webapp create `
    --name yanxia-agent `
    --resource-group $ResourceGroup `
    --plan $AppServicePlan `
    --deployment-container-image-name "node:20-alpine"

# Web
az webapp create `
    --name yanxia-web `
    --resource-group $ResourceGroup `
    --plan $AppServicePlan `
    --deployment-container-image-name "nginx:alpine"

# 6. 配置环境变量
Write-Host "`n[6/7] 配置环境变量..." -ForegroundColor Yellow

$dbConnectionString = "Host=$DbServerName.postgres.database.azure.com;Port=5432;Database=postgres;Username=postgres;Password=$DbAdminPassword;SSL Mode=Require"

az webapp config appsettings set `
    --name yanxia-api `
    --resource-group $ResourceGroup `
    --settings "ConnectionStrings__Default=$dbConnectionString"

# 7. 配置 ACR 访问
Write-Host "`n[7/7] 配置 ACR 访问权限..." -ForegroundColor Yellow

$apps = @("yanxia-api", "yanxia-agent", "yanxia-web")
foreach ($app in $apps) {
    az webapp config container set `
        --name $app `
        --resource-group $ResourceGroup `
        --docker-registry-server-url "https://$AcrName.azurecr.io" `
        --docker-registry-server-user $acrUsername `
        --docker-registry-server-password $acrPassword
}

Write-Host "`n=== Azure 资源创建完成 ===" -ForegroundColor Green

Write-Host "`n请将以下信息保存到 GitHub Secrets:" -ForegroundColor Magenta
Write-Host "  ACR_USERNAME: $acrUsername"
Write-Host "  ACR_PASSWORD: $acrPassword"
Write-Host "  DATABASE_URL: $dbConnectionString"
Write-Host "`n  AZURE_CREDENTIALS: 运行以下命令获取:"
Write-Host "  az ad sp create-for-rbac --name yanxia-github-actions --role contributor --scopes /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/$ResourceGroup --sdk-auth"

