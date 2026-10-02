<#
  배포 파일 만들기 (Windows)
    .\scripts\publish-windows.ps1

  결과 (dist\):
    RemoteDesktop-app-win-x64.zip     통합 앱 RemoteDesktop.exe (내 PC 원격 허용 + 원격 PC에 연결)
    RemoteDesktop-host-win-x64.zip    콘솔 Host (서버/무인 PC용, 명령줄 설정)
    RemoteDesktop-signaling.zip       시그널링 서버 (dotnet 런타임 필요, Linux/Windows 공용)

  .NET 런타임이 없는 PC에서도 실행되도록 self-contained 단일 파일로 만듭니다.
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

function Publish($project, $output, [string[]]$extra) {
    Write-Host "== $project"
    dotnet publish $project -c $Configuration -o $output --nologo @extra
    if ($LASTEXITCODE -ne 0) { throw "publish 실패: $project" }
}

$single = @("-r", $Runtime, "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=none")

$app = Join-Path $dist "app"
$hostOut = Join-Path $dist "host"
$signal = Join-Path $dist "signaling"
Remove-Item $app, $hostOut, $signal -Recurse -Force -ErrorAction SilentlyContinue

Publish (Join-Path $root "windows\Client\RemoteDesktop.Client\RemoteDesktop.Client.csproj") $app $single
Publish (Join-Path $root "windows\Host\RemoteDesktop.Host\RemoteDesktop.Host.csproj") $hostOut $single
Publish (Join-Path $root "signaling\Signaling.Server\Signaling.Server.csproj") $signal @("-p:DebugType=none")

Compress-Archive -Path "$app\*" -DestinationPath (Join-Path $dist "RemoteDesktop-app-$Runtime.zip") -Force
Compress-Archive -Path "$hostOut\*" -DestinationPath (Join-Path $dist "RemoteDesktop-host-$Runtime.zip") -Force
Compress-Archive -Path "$signal\*" -DestinationPath (Join-Path $dist "RemoteDesktop-signaling.zip") -Force

Get-ChildItem $dist -Filter *.zip | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table
