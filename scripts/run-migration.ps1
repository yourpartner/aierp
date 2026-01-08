# ===========================================
# 数据库迁移脚本 (PowerShell)
# ===========================================
# 用于在本地或 CI/CD 中运行数据库迁移
# ===========================================

param(
    [Parameter(Mandatory=$true)]
    [string]$DatabaseUrl,  # PostgreSQL 连接字符串
    
    [switch]$IncludeIncrementalMigrations  # 是否运行增量迁移
)

Write-Host "=== 开始数据库迁移 ===" -ForegroundColor Green

# 检查 psql 是否可用
$psqlPath = Get-Command psql -ErrorAction SilentlyContinue
if (-not $psqlPath) {
    Write-Host "错误: 未找到 psql 命令。请安装 PostgreSQL 客户端。" -ForegroundColor Red
    Write-Host "  Windows: winget install PostgreSQL.PostgreSQL" -ForegroundColor Yellow
    exit 1
}

# 获取脚本所在目录
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
$migrateFile = Join-Path $projectRoot "server-dotnet/migrate.sql"
$migrationsDir = Join-Path $projectRoot "server-dotnet/sql/migrations"

# 1. 运行主迁移脚本
if (Test-Path $migrateFile) {
    Write-Host "`n[1/2] 运行主迁移脚本: migrate.sql" -ForegroundColor Yellow
    
    $env:PGPASSWORD = ($DatabaseUrl -split "Password=")[1] -split ";" | Select-Object -First 1
    
    psql $DatabaseUrl -f $migrateFile
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  ✓ 主迁移完成" -ForegroundColor Green
    } else {
        Write-Host "  ✗ 主迁移失败" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "警告: 未找到 migrate.sql" -ForegroundColor Yellow
}

# 2. 运行增量迁移（如果启用）
if ($IncludeIncrementalMigrations -and (Test-Path $migrationsDir)) {
    Write-Host "`n[2/2] 运行增量迁移..." -ForegroundColor Yellow
    
    $migrationFiles = Get-ChildItem -Path $migrationsDir -Filter "*.sql" | Sort-Object Name
    
    foreach ($file in $migrationFiles) {
        Write-Host "  应用: $($file.Name)" -ForegroundColor Cyan
        psql $DatabaseUrl -f $file.FullName
        
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ✗ 迁移失败: $($file.Name)" -ForegroundColor Red
            exit 1
        }
    }
    
    Write-Host "  ✓ 增量迁移完成" -ForegroundColor Green
} else {
    Write-Host "`n[2/2] 跳过增量迁移" -ForegroundColor Gray
}

Write-Host "`n=== 数据库迁移完成 ===" -ForegroundColor Green

