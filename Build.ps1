param([switch]$Test, [switch]$Package, [switch]$IncludeRuntime, [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64', [string]$RuntimePackageSource = 'https://api.nuget.org/v3/index.json')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$previousLib = [Environment]::GetEnvironmentVariable('LIB', 'Process')
try {
    # C# builds do not need native linker paths inherited from toolchain shells.
    Remove-Item Env:LIB -ErrorAction SilentlyContinue
    & dotnet build (Join-Path $projectRoot 'DevTools.sln') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Test) {
        & dotnet (Join-Path $projectRoot 'tests/DevTools.Tests/bin/Release/net10.0-windows/DevTools.Tests.dll') $projectRoot
        if ($LASTEXITCODE -ne 0) { throw 'Validation failed.' }
    }
    if ($Package) {
        # A fresh staging directory cannot accidentally ship a user's Data folder or old runtime files.
        $portable = Join-Path $projectRoot ('artifacts/package-' + $Runtime + '-' + [guid]::NewGuid().ToString('N'))
        $packageCache = Join-Path $projectRoot 'artifacts/runtime-packages'
        if ($IncludeRuntime) {
            & dotnet publish (Join-Path $projectRoot 'src/DevTools.App/DevTools.App.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:RestorePackagesPath=$packageCache --source $RuntimePackageSource -o $portable
        } else {
            & dotnet publish (Join-Path $projectRoot 'src/DevTools.App/DevTools.App.csproj') -c Release -r $Runtime --no-self-contained -p:PublishSingleFile=false -p:PublishTrimmed=false -o $portable
        }
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        $sample = Join-Path $portable 'Extensions/SystemInfo'
        New-Item -ItemType Directory -Force -Path $sample | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $projectRoot 'extensions/DevTools.SampleExtension/bin/Release/net10.0-windows') -File |
            Where-Object { $_.Name -match '^DevTools.SampleExtension\.(dll|deps\.json)$|^extension\.json$' } |
            Copy-Item -Destination $sample
        Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $portable
        Copy-Item -LiteralPath (Join-Path $projectRoot 'CONTRIBUTING.md') -Destination $portable
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $portable -Recurse -Force
        if ($IncludeRuntime) {
            if (!(Test-Path -LiteralPath (Join-Path $portable 'coreclr.dll')) -or !(Test-Path -LiteralPath (Join-Path $portable 'PresentationFramework.dll'))) { throw 'The portable package is missing its .NET/WPF runtime.' }
        } elseif ((Test-Path -LiteralPath (Join-Path $portable 'coreclr.dll')) -or (Test-Path -LiteralPath (Join-Path $portable 'PresentationFramework.dll'))) { throw 'The smaller package unexpectedly contains runtime files.' }
        Set-Content -LiteralPath (Join-Path $projectRoot 'artifacts/latest-portable-path.txt') -Value $portable
        Compress-Archive -Path (Join-Path $portable '*') -DestinationPath (Join-Path $projectRoot 'artifacts/DevTools-Studio-Windows.zip') -Force
    }
} finally {
    [Environment]::SetEnvironmentVariable('LIB', $previousLib, 'Process')
}
