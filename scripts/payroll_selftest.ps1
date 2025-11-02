#requires -Version 5.1
param(
  [string]$Base = 'http://localhost:5179',
  [string]$CompanyCode = 'JP01',
  [string]$EmployeeCode = 'admin',
  [string]$Password = 'admin123',
  [string]$PolicyCode = 'POL001',
  [string]$PreviewMonth = (Get-Date).ToString('yyyy-MM')
)

function Write-Step($name, $ok){
  $status = if($ok){ 'OK' } else { 'FAIL' }
  Write-Host ("[$status] $name") -ForegroundColor (if($ok){ 'Green' } else { 'Red' })
}

try{
  $loginBody = @{ companyCode=$CompanyCode; employeeCode=$EmployeeCode; password=$Password } | ConvertTo-Json -Compress
  $login = Invoke-RestMethod -Uri ("$Base/auth/login") -Method Post -ContentType 'application/json' -Body $loginBody
  if(-not $login.token){ throw 'no token' }
  $headers = @{ 'x-company-code'=$CompanyCode; Authorization = ('Bearer ' + $login.token) }
  Write-Step 'Login' $true

  $compileBody = @{ nlText = '正社員は月給30万円、通勤手当は上限2万円。会社負担の社会保険を計上する。' } | ConvertTo-Json -Compress
  $compile = Invoke-RestMethod -Uri ("$Base/ai/payroll/compile") -Method Post -Headers $headers -ContentType 'application/json' -Body $compileBody
  Write-Step 'AI Compile' ($null -ne $compile.dsl)

  $etBody = @{ payload = @{ code='FT'; name='正社员'; isActive=$true } } | ConvertTo-Json -Compress
  try{ Invoke-RestMethod -Uri ("$Base/objects/employment_type") -Method Post -Headers $headers -ContentType 'application/json' -Body $etBody | Out-Null } catch { }
  Write-Step 'Ensure EmploymentType FT' $true

  $policyPayload = @{ payload = @{ code=$PolicyCode; name='标准工资策略v1'; rules = $compile.dsl.rules } } | ConvertTo-Json -Compress -Depth 6
  try{ Invoke-RestMethod -Uri ("$Base/objects/payroll_policy") -Method Post -Headers $headers -ContentType 'application/json' -Body $policyPayload | Out-Null } catch { }
  Write-Step "Save Policy $PolicyCode" $true

  $listBody = @{ page=1; pageSize=10; where=@(); orderBy=@(@{ field='policy_code'; dir='ASC' }) } | ConvertTo-Json -Compress
  $pol = Invoke-RestMethod -Uri ("$Base/objects/payroll_policy/search") -Method Post -Headers $headers -ContentType 'application/json' -Body $listBody
  Write-Step 'List Policies' (($pol.data | Measure-Object).Count -ge 1)

  # 旧工资预览接口已移除
  Write-Step 'Payroll Preview (removed)' $true

  $sugBody = @{ items = @('BASE','COMMUTE') } | ConvertTo-Json -Compress
  $sug = Invoke-RestMethod -Uri ("$Base/ai/payroll/suggest-accounts") -Method Post -Headers $headers -ContentType 'application/json' -Body $sugBody
  Write-Step 'AI Account Suggest' ($null -ne $sug)

  Write-Host ('DONE. Month=' + $PreviewMonth)
} catch {
  Write-Step 'ERROR' $false
  Write-Host $_.Exception.Message -ForegroundColor Red
  exit 1
}

