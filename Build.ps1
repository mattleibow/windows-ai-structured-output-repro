param([ValidateSet('current', 'previous')][string]$SdkVariant = 'current')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build .\WindowsAIStructuredRepro.csproj -p:SdkVariant=$SdkVariant -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
    $output = Join-Path $PSScriptRoot "bin\$SdkVariant\Debug\net10.0-windows10.0.19041.0\win-x64"
    [xml]$recipe = Get-Content (Join-Path $output 'WindowsAIStructuredRepro.build.appxrecipe')
    $layout = Join-Path $output 'AppX'
    New-Item -ItemType Directory -Path $layout -Force | Out-Null
    foreach ($file in $recipe.SelectNodes('//*[local-name()="AppxPackagedFile" or local-name()="AppXManifest"]')) {
        $destination = [IO.Path]::GetFullPath((Join-Path $layout $file.PackagePath))
        if (-not $destination.StartsWith($layout + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Invalid package destination: $($file.PackagePath)"
        }
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.Include -Destination $destination -Force
    }
    Write-Host "Staged official MSIX recipe at $layout"
} finally {
    Pop-Location
}
