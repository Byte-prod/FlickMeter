param(
    [string] $OutputPath = (Join-Path $PSScriptRoot 'build\FlickMeter.exe')
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    throw 'The 64-bit .NET Framework compiler was not found.'
}

$output = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
$options = @(
    '/nologo', '/optimize+', '/platform:x64', '/target:winexe', '/codepage:65001',
    "/out:$output", "/win32manifest:$PSScriptRoot\app.manifest", "/win32icon:$PSScriptRoot\overlay.ico",
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Management.dll'
)
$options += (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Sort-Object Name).FullName
& $compiler @options
if ($LASTEXITCODE -ne 0) { throw "Compiler exited with code $LASTEXITCODE." }
Get-Item -LiteralPath $output
