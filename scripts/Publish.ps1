param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "artifacts/PCtoPC-$Runtime" }
$target = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $target | Out-Null
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$common = @('-c','Release','-r',$Runtime,'--self-contained',$selfContained,'--nologo','--source','https://api.nuget.org/v3/index.json','-p:NuGetAudit=false','-p:UseSharedCompilation=false','-m:1')
& dotnet publish (Join-Path $repo 'src/PCtoPC.Client/PCtoPC.Client.csproj') @common -o $target
if ($LASTEXITCODE -ne 0) { throw 'Client publish failed.' }
& dotnet publish (Join-Path $repo 'src/PCtoPC.Agent/PCtoPC.Agent.csproj') @common -o (Join-Path $target 'agent')
if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }
if (-not $FrameworkDependent) {
    $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
    foreach ($component in @('microsoft.netcore.app','microsoft.windowsdesktop.app','microsoft.aspnetcore.app')) {
        $packagePath = Join-Path $packageRoot "$component.runtime.$Runtime"
        $version = Get-ChildItem -LiteralPath $packagePath -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if (-not $version) { throw "Runtime notices not found: $component" }
        $noticeDirectory = Join-Path $target "docs/runtime-licenses/$component"
        New-Item -ItemType Directory -Force -Path $noticeDirectory | Out-Null
        Get-ChildItem -LiteralPath $version.FullName -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' } | Copy-Item -Destination $noticeDirectory -Force
    }
}
Copy-Item -LiteralPath (Join-Path $repo 'README.md'),(Join-Path $repo 'LICENSE'),(Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $target -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repo 'scripts') -Destination $target -Recurse -Force
$label = if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }
Set-Content -LiteralPath (Join-Path $target 'BUILD.txt') -Value "PCtoPC 0.2.0 / $Runtime / $label`r`nSunshine and Moonlight must be installed separately."
Write-Output "Published: $target"
