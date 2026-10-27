param(
    [string]$SigningThumbprint = '',
    [string]$TimestampUrl = 'https://timestamp.digicert.com',
    [string]$ProjectDirectory = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ProjectDirectory)) { throw 'Pass -ProjectDirectory when invoking build commands without a script file.' }
$projectRoot = [IO.Path]::GetFullPath($ProjectDirectory)

# Optional release signing. The private key stays in the Windows certificate store.
if ($SigningThumbprint) {
    if ($SigningThumbprint -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid certificate thumbprint.' }
    if (-not (Get-Command signtool -ErrorAction SilentlyContinue)) { throw 'Install Windows SDK SignTool and add it to PATH.' }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$SigningThumbprint"
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'A current code-signing certificate with its private key is required.' }
    if (@($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) { throw 'Certificate does not permit code signing.' }
    $timestampUri = [uri]$TimestampUrl
    if (-not $timestampUri.IsAbsoluteUri -or $timestampUri.Scheme -ne 'https') { throw 'The timestamp service must use HTTPS.' }
}

function Sign-ReleaseFile([string]$Path) {
    if (-not $SigningThumbprint) { return }
    & signtool sign /sha1 $SigningThumbprint /s My /fd SHA256 /tr $TimestampUrl /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $Path" }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate) { throw "Signature or timestamp validation failed: $Path" }
}

$project = Join-Path $projectRoot 'src\DesktopBuddy.App\DesktopBuddy.App.csproj'
$installerScript = Join-Path $projectRoot 'installer\DesktopBuddy.nsi'
$dist = Join-Path $projectRoot 'dist\releases\0.5.13'
$installerDist = Join-Path $dist 'installers'
$version = '0.5.13'
$fileVersion = '0.5.13.0'

$dotnetCandidates = @()
if ($env:QIANMENG_DOTNET_PATH -and (Test-Path -LiteralPath $env:QIANMENG_DOTNET_PATH)) { $dotnetCandidates += $env:QIANMENG_DOTNET_PATH }
$pathDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($pathDotnet) { $dotnetCandidates += $pathDotnet.Source }
$dotnet = $null
$sdkVersion = $null
$originalDotnetRoot = $env:DOTNET_ROOT
foreach ($candidate in ($dotnetCandidates | Select-Object -Unique)) {
    $env:DOTNET_ROOT = Split-Path -Parent $candidate
    $candidateVersion = (& $candidate --version 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $candidateVersion.StartsWith('10.')) {
        $dotnet = $candidate
        $sdkVersion = $candidateVersion
        break
    }
}
if (-not $dotnet) {
    if ($null -eq $originalDotnetRoot) { Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue }
    else { $env:DOTNET_ROOT = $originalDotnetRoot }
    throw 'No usable .NET 10 SDK found. Install the .NET 10 SDK.'
}

$makensis = Get-Command makensis -ErrorAction SilentlyContinue
$makensisPath = $null
if (-not $makensis) {
    $candidate = [Environment]::ExpandEnvironmentVariables('%ProgramFiles(x86)%\NSIS\makensis.exe')
    if (Test-Path -LiteralPath $candidate) { $makensis = Get-Item -LiteralPath $candidate }
}
if ($makensis) {
    if ($makensis.Source) { $makensisPath = $makensis.Source }
    else { $makensisPath = $makensis.FullName }
}
if (-not $makensis) { throw '未找到 makensis。请安装 NSIS 3，并将 makensis 加入 PATH。' }
New-Item -ItemType Directory -Force -Path $installerDist | Out-Null

$installerDistForNsis = $installerDist
$iconForNsis = (Join-Path $projectRoot 'src\DesktopBuddy.App\app.ico')

foreach ($arch in @('x64')) {
    $rid = "win-$arch"
    $output = Join-Path $dist $rid
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    Write-Host "Publishing $rid -> $output"
    & $dotnet publish $project `
        --configuration Release `
        --runtime $rid `
        --self-contained true `
        --output $output `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$version `
        -p:FileVersion=$fileVersion `
        -p:AssemblyVersion=$fileVersion
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid (exit $LASTEXITCODE)." }

    $executable = Join-Path $output 'DesktopBuddy.exe'
    if (-not (Test-Path $executable) -or (Get-Item $executable).Length -eq 0) { throw "未生成 $executable。" }
    Sign-ReleaseFile $executable
    Write-Host "Creating NSIS installer for $rid..."
    & $makensisPath -V2 `
        "-DAPP_VERSION=$version" `
        "-DAPP_FILE_VERSION=$fileVersion" `
        "-DAPP_ARCH=$arch" `
        "-DAPP_DIR=$($output)" `
        "-DAPP_DIST=$installerDistForNsis" `
        "-DAPP_ICON=$iconForNsis" `
        $installerScript
    if ($LASTEXITCODE -ne 0) { throw "NSIS packaging failed for $rid (exit $LASTEXITCODE)." }
}

Write-Host ''
Write-Host 'Windows setup installers:'
$installerPath = Join-Path $installerDist "DesktopBuddy-Setup-$version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installerPath) -or (Get-Item -LiteralPath $installerPath).Length -eq 0) { throw "未生成 x64 安装器 $installerPath。" }
Sign-ReleaseFile $installerPath
$staleX86Installer = Join-Path $installerDist "DesktopBuddy-Setup-$version-win-x86.exe"
if (Test-Path -LiteralPath $staleX86Installer) { Remove-Item -LiteralPath $staleX86Installer -Force }
$installers = @(Get-Item -LiteralPath $installerPath)
$installers | Select-Object Name, Length
$checksums = foreach ($installer in $installers) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer.FullName).Hash.ToLowerInvariant()
    "$hash  $($installer.Name)"
}
Set-Content -LiteralPath (Join-Path $installerDist 'SHA256SUMS.txt') -Value $checksums -Encoding ascii
Write-Host "SHA-256 manifest: $(Join-Path $installerDist 'SHA256SUMS.txt')"
if ($SigningThumbprint) {
    Write-Host 'The application and installer have valid timestamped signatures. Target Windows policy and runtime acceptance are still required.'
} else {
    Write-Host 'The x64 installer is unsigned; Windows installation and runtime still need acceptance on Windows 10/11.'
}
