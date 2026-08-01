[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $PackagePath,

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

$signingParameters = @{
    FilePath = $PackagePath
    CertificateThumbprint = $CertificateThumbprint
    CertificateStoreLocation = $CertificateStoreLocation
}
if ($TimestampUrl) {
    $signingParameters.TimestampUrl = $TimestampUrl
}
if ($SignToolPath) {
    $signingParameters.SignToolPath = $SignToolPath
}

& (Join-Path $PSScriptRoot 'Sign-WindowsFile.ps1') @signingParameters
