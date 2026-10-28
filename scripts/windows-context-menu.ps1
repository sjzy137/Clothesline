<#
.SYNOPSIS
    Adds or removes the "挂到晾衣绳" / "Hang on Clothesline" entry in Windows Explorer.

.DESCRIPTION
    Adds Clothesline to Windows Explorer in two places, because they behave differently:

      * A classic verb "挂到晾衣绳" on files and folders. No administrator rights are needed.
        Windows 11 keeps classic verbs behind "显示更多选项" / "Show more options".

      * An entry in the "发送到" / "Send to" menu. This one stays in the normal Windows 11
        right-click menu, so it is the quicker of the two on Windows 11.

    Nothing is copied into system folders and no COM shell extension is installed. Running this
    script with -Uninstall removes everything it created.

.EXAMPLE
    .\scripts\windows-context-menu.ps1
    .\scripts\windows-context-menu.ps1 -Uninstall
    .\scripts\windows-context-menu.ps1 -ExePath "C:\Apps\Clothesline\Clothesline.exe"
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'

$verb   = 'ClotheslineHang'
$label  = '挂到晾衣绳'
$types  = @('*', 'Directory')   # files and folders
# Kept ASCII on purpose: this file name is what the Send to menu shows, and non ASCII script
# literals are read differently by Windows PowerShell 5.1 depending on the file encoding.
$sendToName = 'Clothesline.lnk'
$sendTo = Join-Path $env:APPDATA 'Microsoft\Windows\SendTo'

function Find-Clothesline {
    param([string]$Hint)

    if ($Hint) {
        if (-not (Test-Path -LiteralPath $Hint)) { throw "Clothesline.exe not found at: $Hint" }
        return (Resolve-Path -LiteralPath $Hint).Path
    }

    $root = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $root 'windows/Clothesline.Windows/bin/Release/net10.0-windows/Clothesline.exe')
        (Join-Path $root 'windows/Clothesline.Windows/bin/Debug/net10.0-windows/Clothesline.exe')
        (Join-Path $root 'windows/Clothesline.Windows/publish/Clothesline.exe')
        (Join-Path $env:LOCALAPPDATA 'Programs/Clothesline/Clothesline.exe')
        (Join-Path $env:LOCALAPPDATA 'Clothesline/Clothesline.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    throw @"
Could not locate Clothesline.exe. Build the Windows app first, or pass the path directly:

    .\scripts\windows-context-menu.ps1 -ExePath "C:\path\to\Clothesline.exe"
"@
}

function Remove-Verb {
    foreach ($type in $types) {
        $key = "HKCU:\Software\Classes\$type\shell\$verb"
        if (Test-Path -LiteralPath $key) {
            Remove-Item -LiteralPath $key -Recurse -Force
            Write-Host "  removed $key"
        }
    }
    $link = Join-Path $sendTo $sendToName
    if (Test-Path -LiteralPath $link) {
        Remove-Item -LiteralPath $link -Force
        Write-Host "  removed $link"
    }
}

if ($Uninstall) {
    Write-Host 'Removing the Clothesline Explorer integration...'
    Remove-Verb
    Write-Host 'Done. Explorer picks this up on its own within a few seconds.'
    return
}

$exe = Find-Clothesline -Hint $ExePath
Write-Host "Using $exe"

Remove-Verb   # start from a clean slate so a re-run never leaves stale values behind

foreach ($type in $types) {
    $key     = "HKCU:\Software\Classes\$type\shell\$verb"
    $command = Join-Path $key 'command'

    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -LiteralPath $key -Name '(Default)' -Value $label
    Set-ItemProperty -LiteralPath $key -Name 'Icon' -Value "$exe,0"
    # MultiSelectDontDeselect keeps the selection alive, though Explorer still launches one process
    # per item. Each copy drops its path in %LOCALAPPDATA%\Clothesline\pending and the running
    # instance hangs all of them.
    Set-ItemProperty -LiteralPath $key -Name 'MultiSelectModel' -Value 'Player'

    New-Item -Path $command -Force | Out-Null
    Set-ItemProperty -LiteralPath $command -Name '(Default)' -Value "`"$exe`" `"%1`""
    Write-Host "  registered $key"
}

# The Send to menu is populated by shortcuts, and Windows passes the selected files to the target
# as command line arguments, which is exactly the shape Clothesline already accepts.
$link = Join-Path $sendTo $sendToName
New-Item -ItemType Directory -Path $sendTo -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($link)
$shortcut.TargetPath = $exe
$shortcut.Description = 'Hang on Clothesline'
$shortcut.Save()
Write-Host "  registered $link"

Write-Host ''
Write-Host 'Done. Right-click a file or folder:'
Write-Host '  - 发送到 / Send to  ->  Clothesline            (both Windows 10 and 11)'
Write-Host '  - Windows 11: choose 显示更多选项 / Show more options, then 挂到晾衣绳'
Write-Host '  - Windows 10: 挂到晾衣绳 appears directly'
Write-Host 'Run this script again with -Uninstall to remove them.'
