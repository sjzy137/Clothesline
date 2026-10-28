$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
function Run-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $Arguments" }
}
Run-DotNet -Arguments @('run','--project','windows/Clothesline.Tests','-c','Release')
Run-DotNet -Arguments @('publish','windows/Clothesline.Windows','-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=false','-o','build/windows/publish')
$report = Join-Path $PWD 'build/windows/self-test.json'
$process = Start-Process 'build/windows/publish/Clothesline.exe' -ArgumentList @('--self-test', ('"' + $report + '"')) -Wait -PassThru
if (Test-Path $report) { Get-Content $report }
if ($process.ExitCode -ne 0 -or !(Test-Path $report)) { throw ('Windows app self-test failed; exit=' + $process.ExitCode) }
Get-Content $report
if (!(Get-Content $report | ConvertFrom-Json).passed) { throw 'Windows self-test report failed' }
New-Item -ItemType Directory -Path 'build/windows/dist' -Force | Out-Null
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
if (!(Test-Path $compiler)) { throw 'Install Inno Setup 6 to build the Windows installer.' }
& $compiler 'windows/installer.iss'
if ($LASTEXITCODE -ne 0) { throw 'Installer compiler failed' }
Compress-Archive -Path 'build/windows/publish/*' -DestinationPath 'build/windows/dist/Clothesline-0.1.0-windows-rc.1-x64-portable.zip' -Force
$assets = Get-ChildItem 'build/windows/dist' -File | Where-Object { $_.Extension -in @('.exe','.zip') }
$assets | ForEach-Object { $digest = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); "$digest  $($_.Name)" } | Set-Content 'build/windows/dist/SHA256SUMS.txt' -Encoding ascii
# Install into a disposable directory, test the installed app, and uninstall it. No user files are used.
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Clothesline-Install-Test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$destination = Join-Path $fixture 'App'
$sentinel = Join-Path $fixture 'original-user-file.txt'
Set-Content $sentinel 'original source survives install/uninstall'
try {
    $setup = (Get-ChildItem 'build/windows/dist/*Setup.exe').FullName
    $installed = Start-Process $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $destination + '"')) -Wait -PassThru
    if ($installed.ExitCode -ne 0) { throw 'Silent installation failed' }
    $installedReport = Join-Path $fixture 'installed-test.json'
    $probe = Start-Process (Join-Path $destination 'Clothesline.exe') -ArgumentList @('--self-test',('"' + $installedReport + '"')) -Wait -PassThru
    if ($probe.ExitCode -ne 0 -or !(Get-Content $installedReport | ConvertFrom-Json).passed) { throw 'Installed application smoke test failed' }
    $removed = Start-Process (Join-Path $destination 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if ($removed.ExitCode -ne 0 -or !(Test-Path $sentinel)) { throw 'Uninstall failed or source sentinel was removed' }
    if ((Get-Content $sentinel -Raw).Trim() -ne 'original source survives install/uninstall') { throw 'Source sentinel changed' }
    'PASS: install, installed-app smoke test, uninstall and source-file preservation'
} finally { Remove-Item $fixture -Recurse -Force }
