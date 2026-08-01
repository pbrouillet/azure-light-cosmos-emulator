[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $FilePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string] $CertificateThumbprint,

    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string] $CertificateStoreLocation = 'CurrentUser',

    [string] $TimestampUrl,

    [string] $SignToolPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-SignTool {
    param([string] $ExplicitPath)

    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "signtool.exe was not found at '$ExplicitPath'."
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $command = Get-Command 'signtool.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $command.Source).Path
    }

    $kitsRoots = @()
    foreach ($registryPath in @(
        'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots'
    )) {
        if (Test-Path -LiteralPath $registryPath) {
            $installedRoots = Get-ItemProperty -LiteralPath $registryPath
            foreach ($property in @('KitsRoot10', 'KitsRoot11')) {
                $rootProperty = $installedRoots.PSObject.Properties[$property]
                if ($rootProperty -and $rootProperty.Value) {
                    $kitsRoots += (Join-Path $rootProperty.Value 'bin')
                }
            }
        }
    }
    if (${env:ProgramFiles(x86)}) {
        $kitsRoots += (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin')
    }
    $kitsRoots = @($kitsRoots | Select-Object -Unique |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container })

    $hostArchitectures = if (
        [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64'
    ) {
        @('arm64', 'x64', 'x86')
    } else {
        @('x64', 'x86', 'arm64')
    }

    foreach ($kitsRoot in $kitsRoots) {
        $sdkVersions = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version] $_.Name } -Descending
        foreach ($sdkVersion in $sdkVersions) {
            foreach ($hostArchitecture in $hostArchitectures) {
                $candidate = Join-Path $sdkVersion.FullName "$hostArchitecture\signtool.exe"
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    return (Resolve-Path -LiteralPath $candidate).Path
                }
            }
        }
        foreach ($hostArchitecture in $hostArchitectures) {
            $candidate = Join-Path $kitsRoot "$hostArchitecture\signtool.exe"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
    }

    throw 'signtool.exe was not found. Install the Windows 10/11 SDK or pass -SignToolPath.'
}

function Invoke-SignTool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Executable,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe exited with code $LASTEXITCODE."
    }
}

$file = (Resolve-Path -LiteralPath $FilePath).Path
$extension = [System.IO.Path]::GetExtension($file)
if ($extension -ine '.exe' -and $extension -ine '.msix') {
    throw "FilePath '$FilePath' must have an .exe or .msix extension."
}

$signTool = Resolve-SignTool -ExplicitPath $SignToolPath
$normalizedThumbprint = $CertificateThumbprint.ToUpperInvariant()
$signArguments = @(
    'sign',
    '/fd', 'SHA256',
    '/sha1', $normalizedThumbprint,
    '/s', 'My'
)
if ($CertificateStoreLocation -eq 'LocalMachine') {
    $signArguments += '/sm'
}
if ($TimestampUrl) {
    $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
}
$signArguments += $file

Invoke-SignTool -Executable $signTool -Arguments $signArguments

$verifyArguments = @('verify', '/pa', '/all', '/v')
if ($TimestampUrl) {
    $verifyArguments += '/tw'
}
$verifyArguments += $file
Invoke-SignTool -Executable $signTool -Arguments $verifyArguments

$signature = Get-AuthenticodeSignature -LiteralPath $file
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    -not $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne $normalizedThumbprint) {
    throw "Signing verification failed: $($signature.StatusMessage)"
}

Write-Host "Signed and verified Windows file: $file"
