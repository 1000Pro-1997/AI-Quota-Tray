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

Get-FileHash (Join-Path $dist '*.exe') -Algorithm SHA256 |
    Select-Object Path, Hash
