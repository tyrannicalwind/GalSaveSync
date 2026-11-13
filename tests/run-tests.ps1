param([Parameter(Mandatory=$true)][string]$ScratchDirectory, [string]$WebDavUrl = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
New-Item -ItemType Directory -Path $ScratchDirectory -Force | Out-Null
$out = Join-Path $ScratchDirectory 'Tests.exe'
$source = @('Models.cs','Snapshots.cs','SyncEngine.cs','WebDav.cs','JournalStore.cs') | ForEach-Object { Join-Path $root ('src\' + $_) }
$source += Join-Path $PSScriptRoot 'Tests.cs'
$source += Join-Path $PSScriptRoot 'TimeSyncTests.cs'
& $compiler /nologo /target:exe /main:TimeSyncTests /utf8output ('/out:' + $out) /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Security.dll /r:System.Xml.dll /r:System.Core.dll @source
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
$fixture = Join-Path $ScratchDirectory ('fixtures-' + [guid]::NewGuid().ToString('N'))
if ($WebDavUrl) { & $out $fixture $WebDavUrl } else { & $out $fixture }
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
if ($WebDavUrl) {
    $fakeGame = Join-Path $ScratchDirectory 'FakeGame.exe'
    & $compiler /nologo /target:winexe ('/out:' + $fakeGame) (Join-Path $PSScriptRoot 'FakeGame.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Toy game build failed.' }
    $uiTest = Join-Path $ScratchDirectory 'UiFlowTests.exe'
    $uiSource = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
    $uiSource += Join-Path $PSScriptRoot 'UiFlowTests.cs'
    & $compiler /nologo /target:exe /main:UiFlowTests ('/out:' + $uiTest) /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Security.dll /r:System.Xml.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll @uiSource
    if ($LASTEXITCODE -ne 0) { throw 'UI flow test build failed.' }
    & $uiTest $fixture $WebDavUrl (Resolve-Path -LiteralPath $fakeGame).Path
    if ($LASTEXITCODE -ne 0) { throw 'UI flow test failed.' }
}
