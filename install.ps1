# redmine (redmine-cli-dotnet) のインストーラー。Windows PowerShell 5.1 と PowerShell 7 で動く。
#
# 入れる・更新する (最新版):
#   irm https://raw.githubusercontent.com/yakumo-amamiya/redmine-cli-dotnet/main/install.ps1 | iex
# 版を指定する / 外す:
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/yakumo-amamiya/redmine-cli-dotnet/main/install.ps1))) -Version 0.1.0
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/yakumo-amamiya/redmine-cli-dotnet/main/install.ps1))) -Uninstall
#
# やること: Release から redmine-win-x64.zip を落とし、SHA256SUMS と照合して、%LOCALAPPDATA%\Programs\redmine-cli\redmine.exe
# に置き、そのフォルダをユーザーの PATH に足す (初回だけ)。管理者権限は要らない。
#
# iex で読み込まれると呼び出し元のシェルで動くので、exit は使わない (シェルごと閉じてしまう)。失敗は throw で知らせる。

param(
    # 入れる版 (0.1.0 など)。既定は最新版
    [string]$Version = 'latest',
    # 置き場所
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\redmine-cli'),
    # 外す
    [switch]$Uninstall,
    # ユーザーの PATH を書き換えない
    [switch]$NoPathUpdate,
    # ダウンロードせず、手元の zip から入れる (確認用)
    [string]$ArchivePath = ''
)

function Get-RedmineUserPath {
    # The raw value (%USERPROFILE% and such unexpanded), so that writing it back keeps them as they were.
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
    try {
        if ($null -eq $key) { return '' }
        return [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
    finally {
        if ($null -ne $key) { $key.Dispose() }
    }
}

function Set-RedmineUserPath([string]$Value) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
    try {
        $key.SetValue('Path', $Value, [Microsoft.Win32.RegistryValueKind]::ExpandString)
    }
    finally {
        $key.Dispose()
    }
    # Tell running programs (Explorer, new terminals) that the environment changed: setting a user variable broadcasts it.
    [Environment]::SetEnvironmentVariable('REDMINE_CLI_INSTALLER', '1', 'User')
    [Environment]::SetEnvironmentVariable('REDMINE_CLI_INSTALLER', $null, 'User')
}

function Test-RedmineSameDir([string]$A, [string]$B) {
    return [Environment]::ExpandEnvironmentVariables($A).TrimEnd('\') -ieq [Environment]::ExpandEnvironmentVariables($B).TrimEnd('\')
}

function Add-RedmineUserPath([string]$Dir) {
    $current = Get-RedmineUserPath
    $parts = @($current -split ';' | Where-Object { $_ -ne '' })
    if (@($parts | Where-Object { Test-RedmineSameDir $_ $Dir }).Count -eq 0) {
        Set-RedmineUserPath ((@($parts) + $Dir) -join ';')
        Write-Host "ユーザーの PATH に足しました: $Dir (新しく開いたシェルから使えます)"
    }
    if (@($env:Path -split ';' | Where-Object { $_ -ne '' -and (Test-RedmineSameDir $_ $Dir) }).Count -eq 0) {
        $env:Path = "$env:Path;$Dir"
    }
}

function Remove-RedmineUserPath([string]$Dir) {
    $current = Get-RedmineUserPath
    $parts = @($current -split ';' | Where-Object { $_ -ne '' })
    $kept = @($parts | Where-Object { -not (Test-RedmineSameDir $_ $Dir) })
    if ($kept.Count -ne $parts.Count) {
        Set-RedmineUserPath ($kept -join ';')
        Write-Host "ユーザーの PATH から外しました: $Dir"
    }
    $env:Path = (@($env:Path -split ';' | Where-Object { $_ -ne '' -and -not (Test-RedmineSameDir $_ $Dir) }) -join ';')
}

function Invoke-RedmineInstaller {
    param([string]$Version, [string]$InstallDir, [bool]$Uninstall, [bool]$NoPathUpdate, [string]$ArchivePath)

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    $repo = 'yakumo-amamiya/redmine-cli-dotnet'
    $asset = 'redmine-win-x64.zip'
    $target = Join-Path $InstallDir 'redmine.exe'

    if ($Uninstall) {
        foreach ($file in @($target, "$target.old")) {
            if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
        }
        if ((Test-Path -LiteralPath $InstallDir) -and @(Get-ChildItem -LiteralPath $InstallDir -Force).Count -eq 0) {
            Remove-Item -LiteralPath $InstallDir -Force
        }
        if (-not $NoPathUpdate) { Remove-RedmineUserPath $InstallDir }
        Write-Host "外しました: $target"
        return
    }

    if (-not [Environment]::Is64BitOperatingSystem) {
        throw '64 ビット版の Windows が必要です。'
    }
    if ($PSVersionTable.PSVersion.Major -lt 6) {
        # Windows PowerShell 5.1 may not offer TLS 1.2 by default, which GitHub requires.
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }

    $work = Join-Path ([IO.Path]::GetTempPath()) ('redmine-cli-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        if ($ArchivePath -ne '') {
            $zip = (Resolve-Path -LiteralPath $ArchivePath).Path
        }
        else {
            if ($Version -eq 'latest') {
                $base = "https://github.com/$repo/releases/latest/download"
            }
            else {
                $base = "https://github.com/$repo/releases/download/v$($Version.TrimStart('v'))"
            }
            $zip = Join-Path $work $asset
            $sums = Join-Path $work 'SHA256SUMS'
            Write-Host "ダウンロード中: $base/$asset"
            try {
                Invoke-WebRequest -UseBasicParsing -Uri "$base/$asset" -OutFile $zip
                Invoke-WebRequest -UseBasicParsing -Uri "$base/SHA256SUMS" -OutFile $sums
            }
            catch {
                throw "ダウンロードできませんでした ($base/$asset): $($_.Exception.Message)。版の指定 (-Version) と、社内プロキシの設定 (Windows のプロキシ設定) を確認してください。"
            }
            $line = Get-Content -LiteralPath $sums | Where-Object { $_ -match "^([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($asset))$" } | Select-Object -First 1
            if ($null -eq $line) {
                throw "SHA256SUMS に $asset がありません。"
            }
            $expected = ($line -split '\s+')[0]
            $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
            if ($actual -ine $expected) {
                throw "ダウンロードした $asset のハッシュが SHA256SUMS と一致しません。入れずに中止しました。"
            }
        }

        $unpacked = Join-Path $work 'unpacked'
        Expand-Archive -LiteralPath $zip -DestinationPath $unpacked -Force
        $exe = Join-Path $unpacked 'redmine.exe'
        if (-not (Test-Path -LiteralPath $exe)) {
            throw "$asset に redmine.exe が入っていません。"
        }

        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        $old = "$target.old"
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $target) {
            # A running exe cannot be overwritten, but it can be renamed out of the way.
            Move-Item -LiteralPath $target -Destination $old -Force
        }
        Copy-Item -LiteralPath $exe -Destination $target
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue }
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    $installed = & $target --version
    Write-Host "redmine $installed を入れました: $target"

    if (-not $NoPathUpdate) {
        try {
            Add-RedmineUserPath $InstallDir
        }
        catch {
            Write-Warning "ユーザーの PATH を書き換えられませんでした ($($_.Exception.Message))。$InstallDir を手で PATH に足してください。"
        }
        $found = Get-Command redmine -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $found -and $found.Source -and -not (Test-RedmineSameDir $found.Source $target)) {
            Write-Warning "PATH では別の redmine が先に見つかります: $($found.Source)。Node 版 (npm link) なら、その clone で npm unlink -g redmine-cli を実行して外してください。"
        }
    }
    Write-Host '次は対象のリポジトリで redmine target / redmine doctor を実行してください (使い方: redmine --help)。'
}

Invoke-RedmineInstaller -Version $Version -InstallDir $InstallDir -Uninstall $Uninstall.IsPresent -NoPathUpdate $NoPathUpdate.IsPresent -ArchivePath $ArchivePath
