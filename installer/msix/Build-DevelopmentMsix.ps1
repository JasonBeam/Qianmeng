param(
    [Parameter(Mandatory=$true)][string]$MakeAppx,
    [string]$SourceExe = '',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path (Split-Path $PSScriptRoot)
if(-not $SourceExe){$SourceExe=Join-Path $projectRoot 'dist\win-x64\DesktopBuddy.exe'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $projectRoot 'dist\development-msix'}
if(-not (Test-Path -LiteralPath $SourceExe)){throw 'Build the x64 EXE first.'}
$toolSignature=Get-AuthenticodeSignature -LiteralPath $MakeAppx
if($toolSignature.Status -ne 'Valid' -or $toolSignature.SignerCertificate.Subject -notlike '*Microsoft Corporation*'){
    throw 'Use an authentic Microsoft Windows SDK MakeAppx tool.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stage=Join-Path $OutputDirectory ('stage-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $stage 'Assets') -Force | Out-Null
try {
    Copy-Item -LiteralPath $SourceExe -Destination (Join-Path $stage 'DesktopBuddy.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.Development.xml') -Destination (Join-Path $stage 'AppxManifest.xml')
    Add-Type -AssemblyName System.Drawing
    foreach($entry in @(@('StoreLogo.png',50),@('Square44x44Logo.png',44),@('Square150x150Logo.png',150))){
        $size=[int]$entry[1]
        $bitmap=[Drawing.Bitmap]::new($size,$size)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        $purple=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#6955D8'))
        try{
            $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.FillEllipse($purple,0,0,$size-1,$size-1)
            $graphics.FillEllipse([Drawing.Brushes]::White,[single]($size*.22),[single]($size*.18),[single]($size*.58),[single]($size*.64))
            $graphics.FillEllipse($purple,[single]($size*.40),[single]($size*.12),[single]($size*.48),[single]($size*.57))
            $bitmap.Save((Join-Path $stage ('Assets\'+$entry[0])),[Drawing.Imaging.ImageFormat]::Png)
        }finally{$purple.Dispose();$graphics.Dispose();$bitmap.Dispose()}
    }
    $package=Join-Path $OutputDirectory 'Qianmeng-Development-0.5.1-x64.msix'
    & $MakeAppx pack /d $stage /p $package /o
    if($LASTEXITCODE -ne 0){throw "MakeAppx failed: $LASTEXITCODE"}
    [pscustomobject]@{
        package=$package
        packageSha256=(Get-FileHash $package -Algorithm SHA256).Hash
        executableSha256=(Get-FileHash $SourceExe -Algorithm SHA256).Hash
        mode='Windows 11 unsigned development package; not installed'
    }|ConvertTo-Json|Set-Content (Join-Path $OutputDirectory 'development-package-build.json') -Encoding UTF8
}finally{
    if(Test-Path -LiteralPath $stage){Remove-Item -LiteralPath $stage -Recurse -Force}
}
