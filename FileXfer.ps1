[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$FileXferArguments
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$startupFailureExitCode = 3

function Write-FileXferLauncherLog {
    param(
        [string]$Level,
        [string]$Message
    )

    try {
        $localData = [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)
        if ([string]::IsNullOrWhiteSpace($localData)) {
            return
        }

        $logDirectory = Join-Path $localData 'FileXfer\logs'
        if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        }

        $logPath = Join-Path $logDirectory (
            'FileXfer_' + (Get-Date -Format 'yyyyMMdd') + '.log')
        $line = '[' + (Get-Date -Format 'HH:mm:ss') + '] ' +
            '[' + $Level + '] ' + $Message
        [IO.File]::AppendAllText(
            $logPath,
            $line + [Environment]::NewLine,
            (New-Object Text.UTF8Encoding($false)))
    }
    catch {
    }
}

function Get-FileXferRuntimeKey {
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        return 'netfx48-win'
    }

    $platform = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') { 'win' } else { 'unix' }
    return 'net{0}-{1}' -f [Environment]::Version.Major, $platform
}

function Get-FileXferCacheBase {
    $cacheBase = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not [string]::IsNullOrWhiteSpace($cacheBase)) {
        return $cacheBase
    }

    if (-not [string]::IsNullOrWhiteSpace($env:XDG_CACHE_HOME)) {
        return $env:XDG_CACHE_HOME
    }

    $profileFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ([string]::IsNullOrWhiteSpace($profileFolder)) {
        throw 'FileXfer could not find a per-user cache folder.'
    }

    return Join-Path $profileFolder '.cache'
}

function Get-FileXferSourceHash {
    param(
        [System.IO.FileInfo[]]$SourceFiles,
        [string]$RuntimeKey
    )

    $hash = [System.Security.Cryptography.SHA256]::Create()
    try {
        $prefix = "ferry-add-type-cache-v1" + [char]0 + $RuntimeKey + [char]0
        $header = [Text.Encoding]::UTF8.GetBytes($prefix)
        [void]$hash.TransformBlock($header, 0, $header.Length, $header, 0)

        foreach ($file in $SourceFiles) {
            $name = [Text.Encoding]::UTF8.GetBytes($file.Name + [char]0)
            [void]$hash.TransformBlock($name, 0, $name.Length, $name, 0)

            $bytes = [IO.File]::ReadAllBytes($file.FullName)
            if ($bytes.Length -gt 0) {
                [void]$hash.TransformBlock($bytes, 0, $bytes.Length, $bytes, 0)
            }
        }

        $empty = New-Object byte[] 0
        [void]$hash.TransformFinalBlock($empty, 0, 0)
        return ([BitConverter]::ToString($hash.Hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
}

function Get-FileXferCombinedSource {
    param([System.IO.FileInfo[]]$SourceFiles)

    $combined = ($SourceFiles | ForEach-Object {
        [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
    }) -join [Environment]::NewLine

    $usingPattern = '(?m)^\s*using\s+[A-Za-z_][A-Za-z0-9_.]*\s*;\s*$'
    $usings = [regex]::Matches($combined, $usingPattern) |
        ForEach-Object { $_.Value.Trim() } |
        Sort-Object -Unique
    $body = [regex]::Replace($combined, $usingPattern, '')
    return ($usings -join [Environment]::NewLine) +
        [Environment]::NewLine + [Environment]::NewLine + $body
}

# The FileXfer window: MiniUI's engine (runtime\windows-x64) draws ui\index.html, and ui\MiniUI.App.dll beside it
# runs FileXfer (local server for the phone, QR, camera, file dialogs). Nothing is compiled here.
function Invoke-FileXferWindow {
    param([string[]]$Arguments)

    $runtime = Join-Path $PSScriptRoot 'runtime\windows-x64'
    $ui = Join-Path $PSScriptRoot 'ui'
    foreach ($file in @('runtime\windows-x64\MiniUI.Bridge.dll', 'runtime\windows-x64\MiniUI.Engine.dll', 'ui\MiniUI.App.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $file) -PathType Leaf)) {
            throw "FileXfer could not find $file."
        }
    }

    # Windows PowerShell does not load a .NET assembly marked as downloaded (Mark of the Web, as in a copy
    # extracted from a downloaded zip). As the previous launcher did for WebView2, FileXfer then uses its own
    # copy of the two MiniUI files without the mark.
    $bridge = Join-Path $runtime 'MiniUI.Bridge.dll'
    $zone = 0
    try { $zone = [int][Security.Policy.Zone]::CreateFromUrl('file://' + $bridge).SecurityZone } catch { }
    if ($zone -ge 3) {
        $runtime = Get-FileXferUnmarkedRuntime -Runtime $runtime
        $bridge = Join-Path $runtime 'MiniUI.Bridge.dll'
    }

    $stateDirectory = Join-Path ([Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)) 'FileXfer\miniui'
    [void](New-Item -ItemType Directory -Path $stateDirectory -Force)

    [void][Reflection.Assembly]::LoadFrom($bridge)
    $appArguments = @('--home', $PSScriptRoot) + @($Arguments)
    $json = '{"root":' + (ConvertTo-Json -InputObject ([string]$ui) -Compress) +
        ',"stateDir":' + (ConvertTo-Json -InputObject ([string]$stateDirectory) -Compress) +
        ',"title":"FileXfer","width":1180,"height":800,"arguments":[' +
        ((@($appArguments) | ForEach-Object { ConvertTo-Json -InputObject ([string]$_) -Compress }) -join ',') + ']}'
    return [MiniUI.Bridge]::Run((Join-Path $ui 'index.html'), $json)
}

# MiniUI.Bridge.dll and MiniUI.Engine.dll rewritten under %LOCALAPPDATA%\FileXfer\runtime: a file written from
# bytes has no zone mark (Copy-Item would copy the mark). One folder per pair of files.
function Get-FileXferUnmarkedRuntime {
    param([string]$Runtime)

    $names = @('MiniUI.Bridge.dll', 'MiniUI.Engine.dll')
    $key = ($names | ForEach-Object {
        $item = Get-Item -LiteralPath (Join-Path $Runtime $_)
        '{0}:{1}:{2}' -f $_, $item.Length, $item.LastWriteTimeUtc.Ticks
    }) -join '|'
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $id = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($key)))).Replace('-', '').Substring(0, 16).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }

    $target = Join-Path (Get-FileXferCacheBase) ('FileXfer\runtime\' + $id)
    $ready = Join-Path $target 'complete'
    if (-not (Test-Path -LiteralPath $ready -PathType Leaf)) {
        [void](New-Item -ItemType Directory -Path $target -Force)
        foreach ($name in $names) {
            $source = Join-Path $Runtime $name
            $copy = Join-Path $target $name
            try {
                [IO.File]::WriteAllBytes($copy, [IO.File]::ReadAllBytes($source))
            }
            catch [IO.IOException] {
                # Another FileXfer started at the same moment and already uses the copy.
                if (-not (Test-Path -LiteralPath $copy -PathType Leaf) -or
                    (Get-Item -LiteralPath $copy).Length -ne (Get-Item -LiteralPath $source).Length) {
                    throw
                }
            }
        }
        [IO.File]::WriteAllText($ready, $key, [Text.Encoding]::ASCII)
    }
    return $target
}

# --no-browser runs only the local HTTP server: the C# sources are compiled by this PowerShell.
function Invoke-FileXferServer {
    param([string[]]$Arguments)

    $isWindowsPowerShell = $PSVersionTable.PSEdition -eq 'Desktop'
    $sourceDirectory = Join-Path $PSScriptRoot 'src'
    $sourceFiles = if (Test-Path -LiteralPath $sourceDirectory -PathType Container) {
        @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File -Recurse | Sort-Object FullName)
    }
    else {
        @()
    }
    if ($sourceFiles.Count -eq 0) {
        throw "No C# source files were found under src."
    }

    $webDirectory = Join-Path $PSScriptRoot 'web'
    $webFiles = if (Test-Path -LiteralPath $webDirectory -PathType Container) {
        @(Get-ChildItem -LiteralPath $webDirectory -File | Sort-Object Name)
    }
    else {
        @()
    }
    $libraryDirectory = Join-Path $PSScriptRoot 'lib'
    $zxingPath = Join-Path $libraryDirectory 'zxing.dll'
    if (-not (Test-Path -LiteralPath $zxingPath -PathType Leaf)) {
        throw "FileXfer could not find lib\zxing.dll."
    }

    $buildFiles = @($sourceFiles) + @($webFiles) + @(Get-Item -LiteralPath $zxingPath)
    $runtimeKey = Get-FileXferRuntimeKey
    $sourceHash = Get-FileXferSourceHash -SourceFiles $buildFiles -RuntimeKey $runtimeKey
    $cacheRoot = Join-Path (Get-FileXferCacheBase) 'FileXfer\add-type-cache-v1'
    $cacheDirectory = Join-Path (Join-Path $cacheRoot $runtimeKey) $sourceHash
    $assemblyPath = Join-Path $cacheDirectory 'FileXfer.Host.dll'
    $readyPath = Join-Path $cacheDirectory 'complete'

    if (-not ((Test-Path -LiteralPath $assemblyPath -PathType Leaf) -and
              (Test-Path -LiteralPath $readyPath -PathType Leaf))) {
        [void](New-Item -ItemType Directory -Path $cacheDirectory -Force)
        $temporaryAssembly = Join-Path $cacheDirectory ("FileXfer.Host.{0}.tmp.dll" -f $PID)

        try {
            Write-Host ("Compiling FileXfer C# for {0}..." -f $runtimeKey)
            $source = Get-FileXferCombinedSource -SourceFiles $sourceFiles
            if ($isWindowsPowerShell) {
                Add-Type -AssemblyName System.IO.Compression
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                $references = @(
                    'System.Drawing'
                    'System.IO.Compression'
                    'System.IO.Compression.FileSystem'
                    'System.Xml'
                    $zxingPath
                )
                Add-Type -TypeDefinition $source `
                    -OutputAssembly $temporaryAssembly `
                    -OutputType Library `
                    -ReferencedAssemblies $references
            }
            else {
                $referenceDirectory = Join-Path $PSHOME 'ref'
                if (-not (Test-Path -LiteralPath $referenceDirectory -PathType Container)) {
                    throw "PowerShell reference assemblies were not found: $referenceDirectory"
                }
                $frameworkAssemblies = @(
                    Get-ChildItem -LiteralPath $referenceDirectory -Filter '*.dll' -File |
                        Select-Object -ExpandProperty FullName)
                $coreSource = "#define NET9_0_OR_GREATER`n" + $source
                Add-Type -TypeDefinition $coreSource `
                    -OutputAssembly $temporaryAssembly `
                    -OutputType Library `
                    -ReferencedAssemblies @($frameworkAssemblies + $zxingPath)
            }

            if (Test-Path -LiteralPath $assemblyPath -PathType Leaf) {
                Remove-Item -LiteralPath $assemblyPath -Force
            }
            Move-Item -LiteralPath $temporaryAssembly -Destination $assemblyPath
            [IO.File]::WriteAllText($readyPath, $sourceHash, [Text.Encoding]::ASCII)
        }
        finally {
            if (Test-Path -LiteralPath $temporaryAssembly -PathType Leaf) {
                Remove-Item -LiteralPath $temporaryAssembly -Force
            }
        }
    }
    else {
        Write-Host ("Using cached FileXfer C# for {0}." -f $runtimeKey)
    }

    [void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($zxingPath))
    [void][Reflection.Assembly]::LoadFrom($assemblyPath)
    $env:FileXfer_BUILD_ID = $sourceHash
    return [FileXfer.Program]::Run($PSScriptRoot, [string[]]$Arguments)
}

try {
    $arguments = @($FileXferArguments | Where-Object { $null -ne $_ })
    $onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
    if (-not $onWindows -or $arguments -contains '--no-browser' -or $arguments -contains '--help' -or $arguments -contains '-h') {
        exit (Invoke-FileXferServer -Arguments $arguments)
    }

    $exitCode = Invoke-FileXferWindow -Arguments $arguments
    if ($exitCode -ne 0) {
        Write-FileXferLauncherLog 'ERROR' ("MiniUI ended with exit code {0}" -f $exitCode)
        exit $startupFailureExitCode
    }
    exit 0
}
catch {
    $location = ''
    if ($null -ne $_.InvocationInfo) {
        $location = ' (' + $_.InvocationInfo.ScriptName + ':' +
            $_.InvocationInfo.ScriptLineNumber + ')'
    }
    $detail = 'launcher failed' + $location + ' ' + $_.Exception.ToString()
    Write-FileXferLauncherLog 'ERROR' $detail
    [Console]::Error.WriteLine('FileXfer: ' + $detail)
    exit $startupFailureExitCode
}
