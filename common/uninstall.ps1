# Remove only this application's four HKCU registrations.
Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "install.ps1")
$temporaryRoot = Join-Path $env:TEMP ("FileXfer_uninstall_" + [Guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    if (-not (Import-RegistryLines (Build-RegistryLines $false) $temporaryRoot)) {
        throw "Registry removal failed."
    }
    Write-Host "FileXfer was removed from the context menus."
    exit 0
}
catch { Write-Error $_; exit 1 }
finally { if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force } }
