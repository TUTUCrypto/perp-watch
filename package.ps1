param([ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$')][string]$Version = '2.0.0')
$ErrorActionPreference = 'Stop'
$release = Join-Path $PSScriptRoot 'release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$staging = Join-Path $release ('build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
$binary = Join-Path $staging 'PerpWatch-v2.exe'
& (Join-Path $PSScriptRoot 'build.ps1') -Test -OutputName $binary
foreach ($name in @('使用说明.md', '更新说明.md', 'LICENSE', 'REFERENCES.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $staging
}
$archive = Join-Path $release ("PerpWatch-v$Version-windows.zip")
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
($hash + '  ' + [IO.Path]::GetFileName($archive)) | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ASCII
Write-Output ("Portable package: " + $archive)
