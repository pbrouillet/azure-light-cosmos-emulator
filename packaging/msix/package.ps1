[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $Binary,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$')]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Publisher,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Output,

    [string] $MakeAppxPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-WindowsSdkTool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ToolName,

        [string] $ExplicitPath
    )

    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "$ToolName was not found at '$ExplicitPath'."
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $command = Get-Command $ToolName -CommandType Application -ErrorAction SilentlyContinue |
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
                $candidate = Join-Path $sdkVersion.FullName "$hostArchitecture\$ToolName"
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    return (Resolve-Path -LiteralPath $candidate).Path
                }
            }
        }
        foreach ($hostArchitecture in $hostArchitectures) {
            $candidate = Join-Path $kitsRoot "$hostArchitecture\$ToolName"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
    }

    throw "$ToolName was not found. Install the Windows 10/11 SDK or pass its path explicitly."
}

function Invoke-NativeTool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Executable,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$Executable' exited with code $LASTEXITCODE."
    }
}

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string] $Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "Binary '$Path' is not a Windows PE executable."
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -gt ($stream.Length - 6)) {
            throw "Binary '$Path' has an invalid PE header offset."
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Binary '$Path' has an invalid PE signature."
        }
        return $reader.ReadUInt16()
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Test-FileContainsAscii {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Text
    )

    $stream = [System.IO.File]::OpenRead($Path)
    $buffer = [byte[]]::new(65536)
    $tail = ''
    try {
        while (($bytesRead = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $chunk = $tail + [System.Text.Encoding]::ASCII.GetString($buffer, 0, $bytesRead)
            if ($chunk.Contains($Text)) {
                return $true
            }
            $tailLength = [Math]::Min($Text.Length - 1, $chunk.Length)
            $tail = $chunk.Substring($chunk.Length - $tailLength)
        }
        return $false
    } finally {
        $stream.Dispose()
    }
}

function Get-PngDimensions {
    param([Parameter(Mandatory = $true)][string] $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $signature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    if ($bytes.Length -lt 24) {
        throw "MSIX asset '$Path' is not a valid PNG."
    }
    for ($index = 0; $index -lt $signature.Length; $index++) {
        if ($bytes[$index] -ne $signature[$index]) {
            throw "MSIX asset '$Path' is not a valid PNG."
        }
    }
    if ([System.Text.Encoding]::ASCII.GetString($bytes, 12, 4) -ne 'IHDR') {
        throw "MSIX asset '$Path' does not begin with a PNG IHDR chunk."
    }
    $width = ([uint32] $bytes[16] -shl 24) -bor
        ([uint32] $bytes[17] -shl 16) -bor
        ([uint32] $bytes[18] -shl 8) -bor
        [uint32] $bytes[19]
    $height = ([uint32] $bytes[20] -shl 24) -bor
        ([uint32] $bytes[21] -shl 16) -bor
        ([uint32] $bytes[22] -shl 8) -bor
        [uint32] $bytes[23]
    return @($width, $height)
}

$versionParts = $Version.Split('.')
if ($versionParts.Count -ne 4) {
    throw 'MSIX versions must contain exactly four numeric components.'
}
foreach ($part in $versionParts) {
    [UInt16] $number = 0
    if (-not [UInt16]::TryParse($part, [ref] $number)) {
        throw "MSIX version component '$part' must be between 0 and 65535."
    }
}

$binary = (Resolve-Path -LiteralPath $Binary).Path
$expectedMachine = if ($Architecture -eq 'x64') { 0x8664 } else { 0xAA64 }
if ((Get-PeMachine -Path $binary) -ne $expectedMachine) {
    throw "The binary PE architecture does not match requested MSIX architecture '$Architecture'."
}
if (-not (Test-FileContainsAscii -Path $binary -Text '/explorer/favicon.svg')) {
    throw 'The binary does not contain the embedded Explorer. Build cosmos-cli with default features.'
}

$output = [System.IO.Path]::GetFullPath($Output)
if ([System.IO.Path]::GetExtension($output) -ine '.msix') {
    throw "Output '$Output' must have a .msix extension."
}
$outputDirectory = Split-Path -Parent $output
if (-not $outputDirectory) {
    throw "Output '$Output' must resolve to a file path with a parent directory."
}
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$makeAppx = Resolve-WindowsSdkTool -ToolName 'makeappx.exe' -ExplicitPath $MakeAppxPath
$manifestTemplate = Join-Path $PSScriptRoot 'AppxManifest.xml.in'
$assetDirectory = Join-Path $PSScriptRoot 'Assets'
$requiredAssets = [ordered]@{
    'StoreLogo.png' = @(50, 50)
    'Square44x44Logo.png' = @(44, 44)
    'Square150x150Logo.png' = @(150, 150)
}

if (-not (Test-Path -LiteralPath $manifestTemplate -PathType Leaf)) {
    throw "Manifest template not found at '$manifestTemplate'."
}
foreach ($asset in $requiredAssets.Keys) {
    $assetPath = Join-Path $assetDirectory $asset
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Required MSIX asset not found at '$assetPath'."
    }
    $dimensions = Get-PngDimensions -Path $assetPath
    if ($dimensions[0] -ne $requiredAssets[$asset][0] -or
        $dimensions[1] -ne $requiredAssets[$asset][1]) {
        throw "MSIX asset '$asset' must be exactly $($requiredAssets[$asset][0])x$($requiredAssets[$asset][1]) pixels."
    }
}
$actualAssets = @(Get-ChildItem -LiteralPath $assetDirectory -File | Select-Object -ExpandProperty Name)
if (@($actualAssets | Where-Object { -not $requiredAssets.Contains($_) }).Count -ne 0 -or
    $actualAssets.Count -ne $requiredAssets.Count) {
    throw "The Assets directory must contain exactly: $($requiredAssets.Keys -join ', ')."
}

$workRoot = Join-Path $outputDirectory ('.msix-work-' + [Guid]::NewGuid().ToString('N'))
$layout = Join-Path $workRoot 'layout'
$unpacked = Join-Path $workRoot 'unpacked'

try {
    New-Item -ItemType Directory -Path (Join-Path $layout 'Assets') -Force | Out-Null
    Copy-Item -LiteralPath $binary -Destination (Join-Path $layout 'cosmos-emulator.exe')
    foreach ($asset in $requiredAssets.Keys) {
        Copy-Item -LiteralPath (Join-Path $assetDirectory $asset) -Destination (Join-Path $layout 'Assets')
    }

    $manifestPath = Join-Path $layout 'AppxManifest.xml'
    $manifestDocument = [System.Xml.XmlDocument]::new()
    $manifestDocument.PreserveWhitespace = $true
    $manifestDocument.Load($manifestTemplate)
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifestDocument.NameTable)
    $namespaceManager.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identityNode = $manifestDocument.SelectSingleNode('/f:Package/f:Identity', $namespaceManager)
    if (-not $identityNode) {
        throw 'Manifest template is missing Package/Identity.'
    }
    $identityNode.SetAttribute('Publisher', $Publisher)
    $identityNode.SetAttribute('Version', $Version)
    $identityNode.SetAttribute('ProcessorArchitecture', $Architecture)
    $writerSettings = [System.Xml.XmlWriterSettings]::new()
    $writerSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $writerSettings.Indent = $true
    $writer = [System.Xml.XmlWriter]::Create($manifestPath, $writerSettings)
    try {
        $manifestDocument.Save($writer)
    } finally {
        $writer.Dispose()
    }

    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Force
    }

    Invoke-NativeTool -Executable $makeAppx -Arguments @(
        'pack', '/o', '/v',
        '/d', $layout,
        '/p', $output
    )

    Invoke-NativeTool -Executable $makeAppx -Arguments @(
        'unpack', '/o', '/v',
        '/p', $output,
        '/d', $unpacked
    )

    $expectedBinaryHash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
    $packedBinary = Join-Path $unpacked 'cosmos-emulator.exe'
    if (-not (Test-Path -LiteralPath $packedBinary -PathType Leaf)) {
        throw 'Package verification failed: cosmos-emulator.exe is missing.'
    }
    $packedBinaryHash = (Get-FileHash -LiteralPath $packedBinary -Algorithm SHA256).Hash
    if ($packedBinaryHash -ne $expectedBinaryHash) {
        throw 'Package verification failed: the packaged binary hash does not match the staged binary.'
    }

    foreach ($asset in $requiredAssets.Keys) {
        $sourceAsset = Join-Path $assetDirectory $asset
        $packedAsset = Join-Path (Join-Path $unpacked 'Assets') $asset
        if (-not (Test-Path -LiteralPath $packedAsset -PathType Leaf) -or
            (Get-FileHash -LiteralPath $packedAsset -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $sourceAsset -Algorithm SHA256).Hash) {
            throw "Package verification failed: asset '$asset' is missing or changed."
        }
    }

    $expectedFiles = @(
        '[Content_Types].xml',
        'AppxBlockMap.xml',
        'AppxManifest.xml',
        'cosmos-emulator.exe',
        'Assets/StoreLogo.png',
        'Assets/Square44x44Logo.png',
        'Assets/Square150x150Logo.png'
    )
    $unpackedFiles = @(Get-ChildItem -LiteralPath $unpacked -File -Recurse | ForEach-Object {
        $_.FullName.Substring($unpacked.Length + 1).Replace('\', '/')
    })
    if (@($unpackedFiles | Where-Object { $_ -notin $expectedFiles }).Count -ne 0 -or
        @($expectedFiles | Where-Object { $_ -notin $unpackedFiles }).Count -ne 0) {
        throw "Package verification failed: unpacked payload did not contain exactly the expected files."
    }

    $verifiedManifest = [System.Xml.XmlDocument]::new()
    $verifiedManifest.Load((Join-Path $unpacked 'AppxManifest.xml'))
    $verifiedNamespaces = [System.Xml.XmlNamespaceManager]::new($verifiedManifest.NameTable)
    $verifiedNamespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $verifiedNamespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $verifiedNamespaces.AddNamespace('uap5', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/5')
    $verifiedNamespaces.AddNamespace('desktop4', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/4')
    $verifiedNamespaces.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
    $identity = $verifiedManifest.SelectSingleNode('/f:Package/f:Identity', $verifiedNamespaces)
    $application = $verifiedManifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $verifiedNamespaces)
    $alias = $verifiedManifest.SelectSingleNode(
        '/f:Package/f:Applications/f:Application/f:Extensions/uap5:Extension[@Category="windows.appExecutionAlias"]/uap5:AppExecutionAlias[@desktop4:Subsystem="console"]/uap5:ExecutionAlias',
        $verifiedNamespaces
    )
    $visualElements = $verifiedManifest.SelectSingleNode(
        '/f:Package/f:Applications/f:Application/uap:VisualElements',
        $verifiedNamespaces
    )
    $packageLogo = $verifiedManifest.SelectSingleNode(
        '/f:Package/f:Properties/f:Logo',
        $verifiedNamespaces
    )
    $fullTrust = $verifiedManifest.SelectSingleNode(
        '/f:Package/f:Capabilities/rescap:Capability[@Name="runFullTrust"]',
        $verifiedNamespaces
    )
    if (-not $identity -or
        $identity.GetAttribute('Name') -ne 'AzureLightCosmosEmulator' -or
        $identity.GetAttribute('Publisher') -cne $Publisher -or
        $identity.GetAttribute('Version') -ne $Version -or
        $identity.GetAttribute('ProcessorArchitecture') -ne $Architecture) {
        throw 'Package verification failed: manifest identity does not match the requested values.'
    }
    if (-not $application -or
        $application.GetAttribute('Executable') -ne 'cosmos-emulator.exe' -or
        $application.GetAttribute('EntryPoint') -ne 'Windows.FullTrustApplication' -or
        -not $fullTrust -or -not $alias -or
        $alias.GetAttribute('Alias') -ne 'cosmos-emulator.exe') {
        throw 'Package verification failed: full-trust application or execution alias is invalid.'
    }
    if (-not $visualElements -or -not $packageLogo -or
        $packageLogo.InnerText -ne 'Assets\StoreLogo.png' -or
        $visualElements.GetAttribute('Square44x44Logo') -ne 'Assets\Square44x44Logo.png' -or
        $visualElements.GetAttribute('Square150x150Logo') -ne 'Assets\Square150x150Logo.png') {
        throw 'Package verification failed: manifest asset references are invalid.'
    }

    Write-Host "Created and verified MSIX: $output"
} finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
