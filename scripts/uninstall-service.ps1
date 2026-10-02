<#
  RemoteDesktop Host Windows 서비스 제거 (STEP 13) — 관리자 PowerShell에서 실행

    .\scripts\uninstall-service.ps1               # 서비스, 방화벽 규칙, 프로그램 파일 제거 (설정은 남김)
    .\scripts\uninstall-service.ps1 -RemoveData   # 설정(비밀번호, 신뢰된 장치, 인증서, 로그)까지 삭제
#>
param([switch]$RemoveData)

$ErrorActionPreference = "Stop"
$serviceName = "RemoteDesktopHost"
$installDir = Join-Path $env:ProgramFiles "RemoteDesktop"
$dataDir = Join-Path $env:ProgramData "RemoteDesktop"
$policyKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "관리자 권한 PowerShell에서 실행하세요."
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") { Stop-Service $serviceName -Force }
    sc.exe delete $serviceName | Out-Null
    Write-Host "서비스를 제거했습니다."
}

Get-NetFirewallRule -DisplayName "RemoteDesktop Host (service)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule

# Ctrl+Alt+Del 정책을 설치 전 값으로 되돌림
$backup = Join-Path $dataDir "sas-policy-backup.txt"
if (Test-Path $backup) {
    $previous = (Get-Content $backup -Raw).Trim()
    if ($previous -eq "none") {
        Remove-ItemProperty $policyKey -Name SoftwareSASGeneration -ErrorAction SilentlyContinue
    } else {
        Set-ItemProperty $policyKey -Name SoftwareSASGeneration -Value ([int]$previous) -Type DWord
    }
    Remove-Item $backup
    Write-Host "Ctrl+Alt+Del 정책을 원래 값($previous)으로 되돌렸습니다."
}

Start-Sleep 1
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }

if ($RemoveData -and (Test-Path $dataDir)) {
    Remove-Item $dataDir -Recurse -Force
    Write-Host "설정 폴더를 삭제했습니다: $dataDir"
} elseif (Test-Path $dataDir) {
    Write-Host "설정은 남겨 두었습니다: $dataDir (삭제하려면 -RemoveData)"
}

Write-Host "제거 완료"
