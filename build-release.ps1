$ErrorActionPreference = 'Stop'

$SolutionPath = Join-Path $PSScriptRoot "MfgUnlockerInstaller.sln"
$AppProject   = Join-Path $PSScriptRoot "src\MfgUnlockerInstaller\MfgUnlockerInstaller.csproj"
$TestProject  = Join-Path $PSScriptRoot "tests\MfgUnlockerInstaller.Tests\MfgUnlockerInstaller.Tests.csproj"
$PublishDir   = Join-Path $PSScriptRoot "publish"

Write-Host "==> Building solution (Release)..." -ForegroundColor Cyan
dotnet build $SolutionPath -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> Running automated tests..." -ForegroundColor Cyan
dotnet run --project $TestProject -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> Publishing single-file win-x64 executable..." -ForegroundColor Cyan
dotnet publish $AppProject -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $PublishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> Done! Output at: $PublishDir\MfgUnlockerInstaller.exe" -ForegroundColor Green
