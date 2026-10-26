param([switch]$Test, [string]$OutputName = 'PerpWatch.exe')
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$refs = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','System.Xaml.dll')
$outputPath = if ([IO.Path]::IsPathRooted($OutputName)) { $OutputName } else { Join-Path $PSScriptRoot $OutputName }
$arguments = @('/nologo','/optimize+','/target:winexe',('/out:' + $outputPath),('/resource:' + (Join-Path $PSScriptRoot 'MainWindow.xaml') + ',MainWindow.xaml'))
foreach ($ref in $refs) { $arguments += '/reference:' + (Join-Path $framework $ref) }
$arguments += @('Core.cs','Models.cs','Market.cs','App.cs','Dialogs.cs','Dock.cs','UiChecks.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if ($Test) {
    $testArgs = @('/nologo','/target:exe',('/out:' + (Join-Path $PSScriptRoot 'Tests.exe')),('/reference:' + (Join-Path $framework 'System.Web.Extensions.dll')))
    $testArgs += @('Core.cs','Models.cs','Market.cs','Tests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
    & $compiler @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
    & (Join-Path $PSScriptRoot 'Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}
Write-Output ("Build complete: " + $OutputName)
