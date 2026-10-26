$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$output=Join-Path $root 'obj'
$setup=Join-Path $output '灰Desk-2.2.0-test.20261021.13-安装包.exe'
$destination=Join-Path $env:LOCALAPPDATA 'Programs\GrayDesk'
$oldExe='E:\开发产物\灰DeskV2\2.2.0-test.20261021.13\程序\灰Desk.exe'
$installedExe=Join-Path $destination '灰Desk.exe'
$reg='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GrayDesk'
$preferences=Join-Path $env:APPDATA 'GrayDeskV2\preferences.json'
$desktopLink=Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) '灰Desk.lnk'
$programLink=Join-Path ([Environment]::GetFolderPath('Programs')) '灰Desk\灰Desk.lnk'
$manifest=Get-Content (Join-Path $output '安装包构建记录.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$rows=[Collections.Generic.List[object]]::new()
function Check([bool]$ok,[string]$name){$rows.Add([pscustomobject]@{Name=$name;Passed=$ok});if(!$ok){throw $name}}
function RunSetup {
    $p=Start-Process -FilePath $setup -ArgumentList '/S' -Wait -PassThru -WindowStyle Hidden
    return $p.ExitCode
}
function VerifyFiles {
    foreach($f in $manifest.Files){
        $path=Join-Path $destination $f.Path
        if(!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $f.SHA256){throw "Installed file mismatch: $($f.Path)"}
    }
    Check $true ('Installed payload matches source: '+$manifest.Files.Count+' files')
}
if(Test-Path -LiteralPath $destination){throw 'Existing installation directory found; refusing to overwrite outside this controlled test'}
if(Test-Path -LiteralPath $reg){throw 'Existing uninstall registration found; refusing to replace outside this controlled test'}
$oldLinkBackup=Join-Path $output 'desktop-shortcut-before.lnk'
if(Test-Path -LiteralPath $desktopLink){Copy-Item -LiteralPath $desktopLink -Destination $oldLinkBackup -Force}
$prefHash=if(Test-Path -LiteralPath $preferences){(Get-FileHash -LiteralPath $preferences -Algorithm SHA256).Hash}else{$null}
$success=$false
try {
    $old=@(Get-Process -Name 灰Desk -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $oldExe})
    if($old.Count -gt 0){Check ((RunSetup) -eq 10) 'Installer blocks replacement while assistant is running'}
    foreach($p in $old){Stop-Process -Id $p.Id -Force;Wait-Process -Id $p.Id -Timeout 10 -ErrorAction SilentlyContinue}
    # Show only the new installer welcome page; capture its own window without driving desktop controls.
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
public static class SetupCapture {
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
 public struct RECT { public int L,T,R,B; }
 public static void Save(IntPtr hwnd,string path) { RECT r; if(!GetWindowRect(hwnd,out r)||r.R<=r.L)throw new Exception("No installer window");using(var b=new Bitmap(r.R-r.L,r.B-r.T)){using(var g=Graphics.FromImage(b)){var dc=g.GetHdc();try{if(!PrintWindow(hwnd,dc,2))throw new Exception("Installer capture failed");}finally{g.ReleaseHdc(dc);}}b.Save(path);}}
}
'@
    $wizard=Start-Process -FilePath $setup -PassThru
    Start-Sleep -Seconds 2
    $wizard.Refresh()
    $windows=@(Get-Process | Where-Object {$_.MainWindowTitle -eq '灰Desk 安装向导'})
    Check ($windows.Count -eq 1) 'Chinese installer welcome page shown'
    [SetupCapture]::Save($windows[0].MainWindowHandle,(Join-Path $output '安装向导.png'))
    # Welcome page only: no install transaction has started.
    $childIds=@(Get-CimInstance Win32_Process | Where-Object {$_.ParentProcessId -eq $wizard.Id} | Select-Object -ExpandProperty ProcessId)
    Stop-Process -Id $windows[0].Id -Force -ErrorAction SilentlyContinue
    foreach($child in $childIds){Stop-Process -Id $child -Force -ErrorAction SilentlyContinue}
    Stop-Process -Id $wizard.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Check ((RunSetup) -eq 0) 'Fresh per-user install succeeds'
    VerifyFiles
    $registration=Get-ItemProperty -LiteralPath $reg
    Check ($registration.DisplayVersion -eq $manifest.Version -and $registration.InstallLocation -eq $destination) 'Version and uninstall registration are correct'
    $shell=New-Object -ComObject WScript.Shell
    Check ($shell.CreateShortcut($desktopLink).TargetPath -eq $installedExe) 'Desktop shortcut targets installed executable'
    Check ($shell.CreateShortcut($programLink).TargetPath -eq $installedExe) 'Start menu shortcut targets installed executable'
    Check ((RunSetup) -eq 0) 'Same-version reinstall/upgrade succeeds'
    VerifyFiles
    $marker=Join-Path $destination 'user-file-preservation-test.txt'
    [IO.File]::WriteAllText($marker,'Controlled marker; remove only after preservation test.')
    $uninstaller=Join-Path $destination '卸载灰Desk.exe'
    $un=Start-Process -FilePath $uninstaller -ArgumentList '/S' -Wait -PassThru -WindowStyle Hidden
    for($i=0;$i -lt 30 -and (Test-Path -LiteralPath $installedExe);$i++){Start-Sleep -Milliseconds 200}
    Check (!(Test-Path -LiteralPath $installedExe) -and !(Test-Path -LiteralPath $reg)) 'Uninstall removes executable and registration'
    Check (!(Test-Path -LiteralPath $desktopLink) -and !(Test-Path -LiteralPath $programLink)) 'Uninstall removes installed shortcuts'
    Check (Test-Path -LiteralPath $marker) 'Uninstall preserves user-added files'
    if($prefHash){Check ((Get-FileHash -LiteralPath $preferences -Algorithm SHA256).Hash -eq $prefHash) 'Install/upgrade/uninstall preserve preferences byte for byte'}
    Remove-Item -LiteralPath $marker
    Check ((RunSetup) -eq 0) 'Reinstall after uninstall succeeds'
    VerifyFiles
    Start-Process -FilePath $installedExe -WorkingDirectory $destination -WindowStyle Hidden
    Start-Sleep -Seconds 3
    $running=@(Get-Process -Name 灰Desk -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $installedExe})
    Check ($running.Count -eq 1 -and $running[0].Responding) 'One installed assistant process starts and responds'
    Check ((Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion -eq $manifest.Version) 'Actual running executable version is .13'
    $success=$true
} finally {
    [ordered]@{Passed=$success;Time=(Get-Date -Format o);Version=$manifest.Version;InstalledPath=$destination;Tests=$rows;Scope='3060 only; no customer deployment; no remote client controls or connection attempts'} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output '安装卸载验收.json') -Encoding UTF8
    if(!$success){
        # Preserve the previous usable desktop entry on a test failure.
        if(Test-Path -LiteralPath $oldLinkBackup){Copy-Item -LiteralPath $oldLinkBackup -Destination $desktopLink -Force}
        if(!(Get-Process -Name 灰Desk -ErrorAction SilentlyContinue)){Start-Process -FilePath $oldExe -WorkingDirectory (Split-Path $oldExe) -WindowStyle Hidden}
    }
}
$rows | Format-Table -AutoSize
