param(
    [string]$Payload = 'E:\开发产物\灰DeskV2\2.2.0-test.20261021.20\程序',
    [string]$OutputDirectory = "$PSScriptRoot\obj"
)
$ErrorActionPreference = 'Stop'
$version = '2.2.0-test.20261021.20'
$expectedExe = '6DA45D8D1F656C0E36BE7A393A735C8EF6FE31E20110F2CF60AEC9371F32498E'
$compiler = Join-Path $PSScriptRoot 'tools\nsis-3.13\makensis.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Install the official NSIS 3.13 portable package under installer\tools first. See README.md.' }
if ((Get-FileHash -LiteralPath (Join-Path $Payload '灰Desk.exe') -Algorithm SHA256).Hash -ne $expectedExe) { throw 'Payload differs from verified .20 release' }
New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'obj') -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$payloadRoot = (Resolve-Path -LiteralPath $Payload).Path.TrimEnd('\')
$files = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse | Sort-Object FullName)
$deletes = [Collections.Generic.List[string]]::new()
$manifest = @()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($payloadRoot.Length + 1)
    $deletes.Add('Delete "$INSTDIR\' + $relative + '"')
    $manifest += [pscustomobject]@{Path=$relative;Length=$file.Length;SHA256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
foreach ($directory in (Get-ChildItem -LiteralPath $payloadRoot -Directory -Recurse | Sort-Object {$_.FullName.Length} -Descending)) {
    $deletes.Add('RMDir "$INSTDIR\' + $directory.FullName.Substring($payloadRoot.Length + 1) + '"')
}
[IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'obj\uninstall-files.nsh'), $deletes, [Text.UTF8Encoding]::new($true))
$output = Join-Path $OutputDirectory "灰Desk-$version-安装包.exe"
$icon = Join-Path $PSScriptRoot '..\RemoteConnectHelper\Assets\app.ico'
& $compiler /V2 "/DPAYLOAD=$payloadRoot" "/DOUTPUT=$output" "/DAPPICON=$icon" (Join-Path $PSScriptRoot 'GrayDesk.nsi')
if ($LASTEXITCODE -ne 0) { throw 'NSIS compiler failed' }
$record = [ordered]@{
    Version=$version;BuildTime=(Get-Date -Format o);InstallerPath=$output
    InstallerSHA256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    InstallerBytes=(Get-Item -LiteralPath $output).Length
    PayloadExeSHA256=$expectedExe
    BaseCommit='ebfe4be9e91d8e20d20e9bfafa8032efa8f9e77a';NewCommit=$null
    Tool='NSIS 3.13';ToolURL='https://nsis.sourceforge.io/Download'
    ToolZipSHA256='BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8'
    InstallScope='Current Windows user; 3060 local delivery only'
    DefaultInstallDirectory='%LOCALAPPDATA%\Programs\GrayDesk'
    ConfigurationPolicy='Install/upgrade/uninstall retain %APPDATA%\GrayDeskV2; uninstall only removes shipped files'
    Files=$manifest
}
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory '安装包构建记录.json') -Encoding UTF8
($record.InstallerSHA256 + '  ' + (Split-Path $output -Leaf)) | Set-Content -LiteralPath ($output+'.sha256') -Encoding UTF8
$record | Select-Object Version,BuildTime,InstallerBytes,InstallerSHA256
