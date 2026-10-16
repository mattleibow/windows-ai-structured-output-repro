param(
    [ValidateSet('current', 'previous')][string]$SdkVariant = 'current',
    [ValidateSet('interactive', 'plain', 'structured')][string]$Case = 'interactive',
    [switch]$Raw,
    [switch]$MinimalSchema,
    [string]$Label = ('trial-' + [Guid]::NewGuid().ToString('N')),
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
if ($Label -notmatch '^[A-Za-z0-9._-]+$' -or $Label.Contains('..')) { throw 'Label must be a simple file name.' }
Push-Location $PSScriptRoot
try {
    if (-not $SkipBuild) { & .\Build.ps1 -SdkVariant $SdkVariant }
    $output = Join-Path $PSScriptRoot "bin\$SdkVariant\Debug\net10.0-windows10.0.19041.0\win-x64"
    $layout = Join-Path $output 'AppX'
    $manifest = Join-Path $layout 'AppxManifest.xml'
    if (-not (Test-Path $manifest)) { throw "Packaged layout not found: $manifest" }
    $packageJson = & powershell.exe -NoProfile -Command 'Get-AppxPackage -Name WindowsAIStructuredRepro | Select-Object PackageFullName,InstallLocation,PackageFamilyName | ConvertTo-Json -Compress'
    if ($packageJson) {
        $package = $packageJson | ConvertFrom-Json
        if (-not [string]::Equals($package.InstallLocation.TrimEnd('\'), $layout.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
            $savedResults = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\Results"
            if (Test-Path $savedResults) {
                New-Item -ItemType Directory -Path .\Results -Force | Out-Null
                Get-ChildItem -LiteralPath $savedResults -Filter '*.json' -File | Copy-Item -Destination .\Results
            }
            & powershell.exe -NoProfile -Command "Remove-AppxPackage -Package '$($package.PackageFullName.Replace("'","''"))'"
            if ($LASTEXITCODE -ne 0) { throw 'Could not switch this repro package to the other build layout.' }
        }
    }
    $register = "& { `$ErrorActionPreference='Stop'; Add-AppxPackage -Register '$($manifest.Replace("'","''"))' }"
    & powershell.exe -NoProfile -Command $register
    if ($LASTEXITCODE -ne 0) { throw 'MSIX registration failed. Close any running copy and retry.' }

    if (-not ('WindowsAIReproActivation' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IReproApplicationActivationManager {
    [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    [PreserveSig] int ActivateForFile(IntPtr itemArray, string verb, out uint processId);
    [PreserveSig] int ActivateForProtocol(IntPtr itemArray, out uint processId);
}
public static class WindowsAIReproActivation {
    public static uint Activate(string appId, string arguments) {
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true);
        object instance = Activator.CreateInstance(type);
        try {
            uint pid;
            int hr = ((IReproApplicationActivationManager)instance).ActivateApplication(appId, arguments, 0, out pid);
            Marshal.ThrowExceptionForHR(hr);
            return pid;
        } finally { Marshal.ReleaseComObject(instance); }
    }
}
'@
    }
    $package = (& powershell.exe -NoProfile -Command 'Get-AppxPackage -Name WindowsAIStructuredRepro | Select-Object PackageFamilyName,InstallLocation | ConvertTo-Json -Compress' | Out-String) | ConvertFrom-Json
    if (-not [string]::Equals($package.InstallLocation.TrimEnd('\'), $layout.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Windows registered a different layout: $($package.InstallLocation)"
    }
    $family = $package.PackageFamilyName
    if (-not $family) { throw 'Registered repro package was not found.' }
    $arguments = ''
    if ($Case -ne 'interactive') {
        $arguments = "--auto --exit --label=$Label"
        if ($Case -eq 'plain') { $arguments += ' --plain' }
        if ($Raw) { $arguments += ' --raw' }
        if ($MinimalSchema) { $arguments += ' --minimal' }
    }
    $processId = [WindowsAIReproActivation]::Activate("$family!App", $arguments)
    Write-Host "Activated package $family, PID $processId, SDK variant $SdkVariant"
    if ($Case -ne 'interactive') {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($process -and -not $process.WaitForExit(90000)) {
            throw "App did not exit after 90 seconds. Inspect PID $processId; this script does not kill it."
        }
        $result = Join-Path $env:LOCALAPPDATA "Packages\$family\LocalState\Results\result-$Label.json"
        if (-not (Test-Path $result)) { throw "No result report was produced: $result" }
        New-Item -ItemType Directory -Path .\Results -Force | Out-Null
        $destination = ".\Results\$SdkVariant-$Label.json"
        Copy-Item -LiteralPath $result -Destination $destination
        $report = Get-Content -LiteralPath $destination -Raw | ConvertFrom-Json
        $expectedHash = (Get-FileHash -LiteralPath (Join-Path $layout 'Microsoft.Windows.AI.Text.dll') -Algorithm SHA256).Hash
        $loaded = @($report.events | Where-Object { $_ -like '*MODULE Microsoft.Windows.AI.Text.dll *' })
        if ($loaded.Count -ne 1 -or $loaded[0] -notlike "*SHA256=$expectedHash") {
            throw 'The report did not load the selected native AI DLL; do not use it as a version comparison.'
        }
        Get-Content -LiteralPath $destination -Raw
    }
} finally {
    Pop-Location
}
