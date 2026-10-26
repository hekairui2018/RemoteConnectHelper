$ErrorActionPreference='Stop'
$version='2.2.0-test.20261021.16'
$output=Join-Path $PSScriptRoot 'obj'
$setup=Join-Path $output "灰Desk-$version-安装包.exe"
$destination=Join-Path $env:LOCALAPPDATA 'Programs\GrayDesk'
$exe=Join-Path $destination '灰Desk.exe'
$oldVersion=(Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if($oldVersion -ne '2.2.0-test.20261021.15'){throw 'Expected installed .15; do not replace an unknown installation'}
$manifest=Get-Content (Join-Path $output '安装包构建记录.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($manifest.Version -ne $version -or (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne $manifest.InstallerSHA256){throw 'Installer manifest mismatch'}
$preferences=Join-Path $env:APPDATA 'GrayDeskV2\preferences.json'
$before=if(Test-Path -LiteralPath $preferences){[IO.File]::ReadAllText($preferences)}else{$null}
if($before){[IO.File]::WriteAllText((Join-Path $output 'preferences-before-upgrade-private.json'),$before)}
$rows=[Collections.Generic.List[object]]::new()
function Check([bool]$ok,[string]$name){$rows.Add([pscustomobject]@{Name=$name;Passed=$ok});if(!$ok){throw $name}}
$success=$false
try {
    $mainIds=@(Get-CimInstance Win32_Process -Filter "Name='灰Desk.exe'" | Where-Object {$_.ExecutablePath -eq $exe -and $_.CommandLine -notmatch '--ocr-worker'} | Select-Object -ExpandProperty ProcessId)
    $running=@(Get-Process -Name 灰Desk -ErrorAction SilentlyContinue | Where-Object {$mainIds -contains $_.Id})
    if($running.Count -gt 0){
        $blocked=Start-Process -FilePath $setup -ArgumentList '/S' -Wait -PassThru -WindowStyle Hidden
        Check ($blocked.ExitCode -eq 10) 'New installer blocks replacement while old assistant is running'
    }
    foreach($process in $running){Stop-Process -Id $process.Id -Force;Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue}
    Add-Type -ReferencedAssemblies System.Drawing -Path (Join-Path $PSScriptRoot 'Capture.cs')
    $wizard=Start-Process -FilePath $setup -PassThru
    Start-Sleep -Seconds 2
    $windows=@(Get-Process | Where-Object {$_.MainWindowTitle -eq '灰Desk 安装向导'})
    Check ($windows.Count -eq 1) 'Branded Chinese installer shown'
    [InstallerCapture]::SaveWindow($windows[0].MainWindowHandle,(Join-Path $output '新版安装向导.png'))
    [InstallerCapture]::SaveIcon($setup,(Join-Path $output '新版安装包图标.png'))
    $childIds=@(Get-CimInstance Win32_Process | Where-Object {$_.ParentProcessId -eq $wizard.Id} | Select-Object -ExpandProperty ProcessId)
    Stop-Process -Id $windows[0].Id -Force -ErrorAction SilentlyContinue
    foreach($child in $childIds){Stop-Process -Id $child -Force -ErrorAction SilentlyContinue}
    Stop-Process -Id $wizard.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    $install=Start-Process -FilePath $setup -ArgumentList '/S' -Wait -PassThru -WindowStyle Hidden
    Check ($install.ExitCode -eq 0) 'Upgrade .15 to .16 succeeds'
    foreach($file in $manifest.Files){
        $path=Join-Path $destination $file.Path
        if(!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.SHA256){throw "Installed file mismatch: $($file.Path)"}
    }
    Check $true ('Installed files match verified package: '+$manifest.Files.Count)
    if($before){Check ([IO.File]::ReadAllText($preferences) -eq $before) 'Upgrade preserves settings before startup'}
    $registration=Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GrayDesk'
    Check ($registration.DisplayVersion -eq $version -and $registration.InstallLocation -eq $destination) 'Uninstall entry has current version and path'
    $shell=New-Object -ComObject WScript.Shell
    foreach($link in @((Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) '灰Desk.lnk'),(Join-Path ([Environment]::GetFolderPath('Programs')) '灰Desk\灰Desk.lnk'))){
        Check ($shell.CreateShortcut($link).TargetPath -eq $exe) 'Shortcut targets installed executable'
    }
    Start-Process -FilePath $exe -WorkingDirectory $destination -WindowStyle Hidden
    Start-Sleep -Seconds 4
    $mainIds=@(Get-CimInstance Win32_Process -Filter "Name='灰Desk.exe'" | Where-Object {$_.ExecutablePath -eq $exe -and $_.CommandLine -notmatch '--ocr-worker'} | Select-Object -ExpandProperty ProcessId)
    $running=@(Get-Process -Name 灰Desk -ErrorAction SilentlyContinue | Where-Object {$mainIds -contains $_.Id})
    Check ($running.Count -eq 1 -and $running[0].Responding) 'One installed .16 process is running and responds'
    Check ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -eq $version) 'Actual running version is .16'
    $logs=Get-ChildItem (Join-Path $env:APPDATA 'GrayDeskV2\logs') -Filter '*.log' -File | Sort-Object LastWriteTime -Descending
    $startup=@($logs | Select-Object -First 2 | ForEach-Object {Get-Content $_.FullName -Tail 30 -Encoding UTF8} | Where-Object {$_ -match '启动后台发现：'})
    Check ($startup.Count -gt 0) 'Startup background discovery completed without user input'
    $uuPaths=@(Get-Process -Name GameViewer -ErrorAction SilentlyContinue | ForEach-Object {try{$_.Path}catch{}} | Where-Object {$_} | Select-Object -Unique)
    if($uuPaths.Count -gt 0){
        $prefs=Get-Content -LiteralPath $preferences -Raw -Encoding UTF8 | ConvertFrom-Json
        $uu=@($prefs.SoftwareEntries | Where-Object {$_.ConnectorId -eq 'uu'})
        Check ($uu.Count -eq 1 -and $uuPaths -contains $uu[0].Path) 'UU added once with actual running executable path'
        if($before){$oldPrefs=$before | ConvertFrom-Json;$oldUU=@($oldPrefs.SoftwareEntries | Where-Object {$_.ConnectorId -eq 'uu'});if($oldUU.Count -gt 0){Check ($uu[0].Name -eq $oldUU[0].Name) 'UU existing display name preserved'}}
    }
    $mainId=$running[0].Id
    $isolation=Start-Process -FilePath $exe -ArgumentList @('--verify-ocr-isolation',('"'+(Join-Path $output 'installed-ocr16-check')+'"')) -Wait -PassThru -WindowStyle Hidden
    Check ($isolation.ExitCode -eq 0) 'Installed package passes real OCR, cancellation, timeout, forced worker exit and fallback checks'
    Check ([bool](Get-Process -Id $mainId -ErrorAction SilentlyContinue)) 'Desktop assistant remains alive throughout installed OCR fault tests'
    $explicit=Start-Process -FilePath $exe -ArgumentList @('--verify-explicit-connect',('"'+(Join-Path $output 'installed-explicit16-check')+'"')) -Wait -PassThru -WindowStyle Hidden
    Check ($explicit.ExitCode -eq 0) 'Installed package passes explicit connect, retry, listening-off, status visibility and main toggle checks'
    $success=$true
} finally {
    [ordered]@{Passed=$success;Time=(Get-Date -Format o);Version=$version;InstalledPath=$destination;PreviousVersion=$oldVersion;Tests=$rows;Scope='3060 only; own installer capture; no remote software launched or operated; running-client recognition depends on user-opened clients'} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output '升级验收.json') -Encoding UTF8
}
$rows | Format-Table -AutoSize
