<#
  RemoteDesktop Host를 Windows 서비스로 설치 (STEP 13) — 관리자 PowerShell에서 실행

    .\scripts\publish-windows.ps1                 # 먼저 배포 파일 만들기 (dist\host)
    .\scripts\install-service.ps1                 # 설치 (LAN만)
    .\scripts\install-service.ps1 -HostArgs "--signal","wss://signal.example.com/ws"   # 인터넷 연결도

  서비스로 설치하면:
    - Windows에 로그인하기 전(로그인 화면), 잠금 화면, UAC 확인 창도 원격으로 보고 조작할 수 있습니다.
    - 원격에서 Ctrl+Alt+Del을 보낼 수 있습니다.
    - PC를 다시 시작해도 자동으로 실행됩니다.

  하는 일:
    1. 실행 파일을 C:\Program Files\RemoteDesktop 으로 복사 (일반 사용자가 바꿀 수 없는 위치)
    2. 접속 비밀번호 설정 (서비스는 접속 코드를 보여 줄 화면이 없으므로 비밀번호 필수)
    3. Windows 정책 SoftwareSASGeneration = 1 (서비스가 Ctrl+Alt+Del을 보낼 수 있게. 이전 값은 백업)
    4. 서비스 RemoteDesktopHost 등록 (자동 시작, 실패 시 다시 시작)
    5. 방화벽: 개인 네트워크에서만 이 프로그램의 연결 허용
#>
param(
    [string]$Source = "",
    [string[]]$HostArgs = @()
)

$ErrorActionPreference = "Stop"
$serviceName = "RemoteDesktopHost"
$installDir = Join-Path $env:ProgramFiles "RemoteDesktop"
$dataDir = Join-Path $env:ProgramData "RemoteDesktop"
$firewallRule = "RemoteDesktop Host (service)"
$policyKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "관리자 권한 PowerShell에서 실행하세요. (시작 메뉴 → PowerShell 우클릭 → 관리자 권한으로 실행)"
}

if (-not $Source) {
    $root = Split-Path $PSScriptRoot -Parent
    $Source = Join-Path $root "dist\host"
}

$sourceExe = Join-Path $Source "RemoteDesktop.Host.exe"
if (-not (Test-Path $sourceExe)) {
    throw "$sourceExe 이 없습니다. 먼저 .\scripts\publish-windows.ps1 을 실행하거나 -Source 로 폴더를 지정하세요."
}

# 1. 기존 서비스 정지 후 파일 복사
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "기존 서비스를 멈춥니다..."
    if ($existing.Status -ne "Stopped") { Stop-Service $serviceName -Force }
    sc.exe delete $serviceName | Out-Null
    Start-Sleep 2
}

Write-Host "== 1. 파일 복사: $installDir"
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item (Join-Path $Source "*") $installDir -Recurse -Force
$exe = Join-Path $installDir "RemoteDesktop.Host.exe"

# 2. 비밀번호 (이미 있으면 건너뜀). 이 명령이 데이터 폴더 권한(SYSTEM, Administrators만)도 설정합니다.
$hostJson = Join-Path $dataDir "host.json"
$hasPassword = (Test-Path $hostJson) -and ((Get-Content $hostJson -Raw) -match '"password"\s*:\s*\{')
if ($hasPassword) {
    Write-Host "== 2. 비밀번호: 이미 설정됨 (바꾸려면: & `"$exe`" service password set)"
} else {
    Write-Host "== 2. 원격 접속 비밀번호를 정하세요 (8자 이상, 다른 곳에 쓰지 않는 비밀번호 권장)"
    & $exe service password set
    if ($LASTEXITCODE -ne 0) { throw "비밀번호를 설정하지 못했습니다." }
}

# 3. Ctrl+Alt+Del 정책
Write-Host "== 3. Ctrl+Alt+Del 정책 (SoftwareSASGeneration)"
$current = (Get-ItemProperty $policyKey -Name SoftwareSASGeneration -ErrorAction SilentlyContinue).SoftwareSASGeneration
$backup = Join-Path $dataDir "sas-policy-backup.txt"
if (-not (Test-Path $backup)) {
    Set-Content $backup -Value $(if ($null -eq $current) { "none" } else { "$current" }) -Encoding ascii
}
if ($null -eq $current -or (($current -band 1) -ne 1)) {
    $newValue = if ($null -eq $current) { 1 } else { $current -bor 1 }
    Set-ItemProperty $policyKey -Name SoftwareSASGeneration -Value $newValue -Type DWord
}

# 4. 서비스 등록
Write-Host "== 4. 서비스 등록: $serviceName"
$quotedArgs = ($HostArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join " "
$binaryPath = "`"$exe`" service run $quotedArgs".Trim()
New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName "RemoteDesktop Host" `
    -Description "원격 데스크톱 Host (로그인/잠금/UAC 화면 포함)" -StartupType Automatic | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null

# 5. 방화벽 (개인 네트워크만)
Write-Host "== 5. 방화벽: 개인 네트워크만 허용"
Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Program $exe -Action Allow -Profile Private | Out-Null

Start-Service $serviceName
Start-Sleep 3
$service = Get-Service $serviceName
$hostId = (Get-Content $hostJson -Raw | ConvertFrom-Json).host_id

Write-Host ""
Write-Host "============================================================"
Write-Host "  서비스 상태 : $($service.Status)"
Write-Host "  Host ID     : $hostId"
Write-Host "  로그        : $dataDir\service.log, agent.log"
Write-Host "  접속        : 원격 PC에서 이 PC의 IP(또는 '같은 네트워크에서 찾기')로 연결 → 비밀번호"
Write-Host "  제거        : .\scripts\uninstall-service.ps1"
Write-Host "============================================================"
Write-Host "참고: 서비스가 50505 포트를 쓰므로, 이 PC에서 RemoteDesktop 앱의 [내 PC 원격 허용]은 끄세요."
