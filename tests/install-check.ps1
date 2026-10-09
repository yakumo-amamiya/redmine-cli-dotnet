# Tries install.ps1 from a local zip: install, PATH, update over an existing exe, uninstall. Run by CI on Windows
# PowerShell 5.1 and on PowerShell 7. It changes the user PATH of the machine it runs on, so it is for CI runners only.
param(
    [Parameter(Mandatory = $true)][string]$Zip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# As `irm | iex` does: the text decoded as UTF-8, run as a script block.
$script = [scriptblock]::Create([IO.File]::ReadAllText((Join-Path $root 'install.ps1'), [Text.Encoding]::UTF8))
$dir = Join-Path ([IO.Path]::GetTempPath()) ('redmine-install-' + [guid]::NewGuid().ToString('N'))

function Get-UserPathParts {
    @((Get-ItemProperty HKCU:\Environment).Path -split ';')
}

& $script -ArchivePath $Zip -InstallDir $dir
if ((Get-UserPathParts) -notcontains $dir) { throw 'the user PATH was not updated' }
$version = & (Join-Path $dir 'redmine.exe') --version
if ($LASTEXITCODE -ne 0 -or -not $version) { throw 'the installed exe did not run' }

# Installing again over an existing exe (an update) keeps one PATH entry.
& $script -ArchivePath $Zip -InstallDir $dir
if (@(Get-UserPathParts | Where-Object { $_ -eq $dir }).Count -ne 1) { throw 'the user PATH got the folder twice' }

& $script -Uninstall -InstallDir $dir
if (Test-Path (Join-Path $dir 'redmine.exe')) { throw 'redmine.exe was not removed' }
if ((Get-UserPathParts) -contains $dir) { throw 'the user PATH still has the folder' }
Write-Host "install.ps1 works on PowerShell $($PSVersionTable.PSVersion) (redmine $version)"
