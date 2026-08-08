$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'src\AiUsageTray\AiUsageTray.csproj'
$dist = Join-Path $root 'dist'
$build = Join-Path $root 'artifacts'

New-Item -ItemType Directory -Force -Path $dist | Out-Null
New-Item -ItemType Directory -Force -Path $build | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o (Join-Path $build 'framework')
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent publish failed.' }

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o (Join-Path $build 'standalone')
if ($LASTEXITCODE -ne 0) { throw 'Standalone publish failed.' }

cargo build --release --manifest-path (Join-Path $root 'launcher\Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }

Copy-Item (Join-Path $build 'framework\AiQuotaTray.exe') `
    (Join-Path $dist 'AiQuotaTray.exe') -Force
Copy-Item (Join-Path $build 'standalone\AiQuotaTray.exe') `
    (Join-Path $dist 'AiQuotaTray-standalone.exe') -Force
Copy-Item (Join-Path $root 'launcher\target\release\ai-quota-tray-launcher.exe') `
    (Join-Path $dist 'AI-Quota-Tray-Setup.exe') -Force

# 자립형 exe의 SHA256은 릴리스 노트에 반드시 넣어야 한다.
# 런처와 앱이 이 값으로 내려받은 파일을 검증한다. 없으면 자동 업데이트가 멈춘다.
Write-Host ''
Write-Host 'SHA256 (릴리스 노트의 Checksums 항목에 붙여넣을 것)' -ForegroundColor Cyan
Get-ChildItem -Path $dist -Filter *.exe | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    '{0}  {1}' -f $_.Name, $hash
}
