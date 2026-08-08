$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'src\AiUsageTray\AiUsageTray.csproj'
$dist = Join-Path $root 'dist'
$build = Join-Path $root 'artifacts'

New-Item -ItemType Directory -Force -Path $dist | Out-Null
New-Item -ItemType Directory -Force -Path $build | Out-Null

# 프레임워크 의존 exe는 더 이상 만들지 않는다. .NET 10 런타임을 따로 깔아야
# 하는데 런처가 자립형을 알아서 받아 오니 쓸 사람이 없고, 이름이 런처와
# 한 글자 차이라 어느 것을 받아야 하는지 헷갈리기만 했다.

# WPF는 네이티브 DLL(wpfgfx_cor3 등)을 쓴다. IncludeNativeLibrariesForSelfExtract
# 없이 단일 파일로 묶으면 그 DLL이 풀리지 않아 앱이 DllNotFoundException으로 죽는다.
# EnableCompressionInSingleFile을 빼면 크기가 두 배로 부푼다. 둘 다 반드시 넣을 것.
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o (Join-Path $build 'standalone')
if ($LASTEXITCODE -ne 0) { throw 'Standalone publish failed.' }

cargo build --release --manifest-path (Join-Path $root 'launcher\Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }

Copy-Item (Join-Path $build 'standalone\AiQuotaTray.exe') `
    (Join-Path $dist 'AiQuotaTray-standalone.exe') -Force
Copy-Item (Join-Path $root 'launcher\target\release\ai-quota-tray-launcher.exe') `
    (Join-Path $dist 'AI-Quota-Tray.exe') -Force

# 자립형 exe의 SHA256은 릴리스 노트에 반드시 넣어야 한다.
# 런처와 앱이 이 값으로 내려받은 파일을 검증한다. 없으면 자동 업데이트가 멈춘다.
Write-Host ''
Write-Host 'SHA256 (릴리스 노트의 Checksums 항목에 붙여넣을 것)' -ForegroundColor Cyan
Get-ChildItem -Path $dist -Filter *.exe | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    '{0}  {1}' -f $_.Name, $hash
}
