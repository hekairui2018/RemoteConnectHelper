$ErrorActionPreference='Stop'
$version='2.2.0-test.20261021.15'
$setup=Join-Path $PSScriptRoot '灰Desk-2.2.0-test.20261021.15-安装包.exe'
$setupHash='2920650EFCC95D5D45EA82B73668FFDEF3305E0C36BC5C3B4EE226B5B1B007D2'
$exeHash='C5828AD551A6CD842909B15C94478DB502A730E14674937DECFCEB1B0A55859A'
$destination=Join-Path $env:LOCALAPPDATA 'Programs\GrayDesk'
$exe=Join-Path $destination '灰Desk.exe'
$preferences=Join-Path $env:APPDATA 'GrayDeskV2\preferences.json'
$result=Join-Path $PSScriptRoot '灰Desk15安装结果.txt'
$backup=Join-Path $env:LOCALAPPDATA ('GrayDesk-InstallBackup\'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$registration='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GrayDesk'
$oldVersion=if(Test-Path -LiteralPath $exe){(Get-Item -LiteralPath $exe).VersionInfo.ProductVersion}else{$null}
$replaced=$false
$success=$false
try {
    if(!(Test-Path -LiteralPath $setup) -or (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne $setupHash){throw '安装包校验失败，请重新获取15安装包。'}
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    if(Test-Path -LiteralPath $destination){Copy-Item -LiteralPath $destination -Destination (Join-Path $backup 'Program') -Recurse}
    if(Test-Path -LiteralPath $preferences){Copy-Item -LiteralPath $preferences -Destination (Join-Path $backup 'preferences.json')}
    foreach($process in @(Get-Process -Name 灰Desk -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe})){
        Stop-Process -Id $process.Id -Force
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    $replaced=$true
    $install=Start-Process -FilePath $setup -ArgumentList '/S' -Wait -PassThru -WindowStyle Hidden
    if($install.ExitCode -ne 0){throw "安装器返回代码 $($install.ExitCode)"}
    if((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $exeHash -or (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -ne $version){throw '安装后的程序不是已核验的15版本。'}
    $shell=New-Object -ComObject WScript.Shell
    $desktop=[Environment]::GetFolderPath('DesktopDirectory')
    if($shell.CreateShortcut((Join-Path $desktop '灰Desk.lnk')).TargetPath -ne $exe){throw '桌面快捷方式目标不正确。'}
    Start-Process -FilePath $exe -WorkingDirectory $destination -WindowStyle Hidden
    Start-Sleep -Seconds 4
    $mainIds=@(Get-CimInstance Win32_Process -Filter "Name='灰Desk.exe'" | Where-Object {$_.ExecutablePath -eq $exe -and $_.CommandLine -notmatch '--ocr-worker'} | Select-Object -ExpandProperty ProcessId)
    $running=@(Get-Process -Name 灰Desk -ErrorAction SilentlyContinue | Where-Object {$mainIds -contains $_.Id})
    if($running.Count -ne 1 -or !$running[0].Responding){throw '新版启动未通过检查。'}
    @("安装完成：$version","实际程序：$exe","进程：$($running[0].Id)","程序SHA256：$exeHash","备份：$backup","时间：$(Get-Date -Format o)","设置已保留；默认锁定两区；OCR已隔离，原客户闪退根因尚待复测。") | Set-Content -LiteralPath $result -Encoding UTF8
    $success=$true
    Write-Host '灰Desk 15 已安装并启动。' -ForegroundColor Green
} catch {
    $message=$_.Exception.Message
    if($replaced -and (Test-Path -LiteralPath (Join-Path $backup 'Program\灰Desk.exe'))){
        try {
            foreach($process in @(Get-Process -Name 灰Desk -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe})){Stop-Process -Id $process.Id -Force}
            Copy-Item -Path (Join-Path $backup 'Program\*') -Destination $destination -Recurse -Force
            if(Test-Path -LiteralPath (Join-Path $backup 'preferences.json')){Copy-Item -LiteralPath (Join-Path $backup 'preferences.json') -Destination $preferences -Force}
            if($oldVersion -and (Test-Path -LiteralPath $registration)){Set-ItemProperty -LiteralPath $registration -Name DisplayVersion -Value $oldVersion}
            Start-Process -FilePath $exe -WorkingDirectory $destination -WindowStyle Hidden
            $message+='；已恢复原程序和设置。'
        } catch {$message+='；恢复未完成，请保留备份并联系开发者。'}
    }
    @('安装未通过验收。',$message,"备份：$backup","时间：$(Get-Date -Format o)") | Set-Content -LiteralPath $result -Encoding UTF8
    Write-Host $message -ForegroundColor Red
} finally {
    Write-Host "结果记录：$result"
}
if(!$success){exit 1}
