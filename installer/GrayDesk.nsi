Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

!ifndef PAYLOAD
  !error "Pass /DPAYLOAD=<verified program directory>"
!endif
!ifndef OUTPUT
  !error "Pass /DOUTPUT=<installer exe path>"
!endif
!ifndef APPICON
  !error "Pass /DAPPICON=<application ico>"
!endif

!define APP_VERSION "2.2.0-test.20261021.20"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\GrayDesk"
Name "灰Desk"
Caption "灰Desk 安装向导"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\GrayDesk"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
ManifestDPIAware true
Icon "${APPICON}"
UninstallIcon "${APPICON}"
SetCompressor /SOLID lzma
SetCompressorDictSize 64
VIProductVersion "2.2.0.20"
VIAddVersionKey /LANG=2052 "ProductName" "灰Desk"
VIAddVersionKey /LANG=2052 "FileDescription" "灰Desk 中文离线安装包"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "FileVersion" "2.2.0.20"
VIAddVersionKey /LANG=2052 "LegalCopyright" "灰Desk"

!define MUI_ICON "${APPICON}"
!define MUI_UNICON "${APPICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEFINISHPAGE_BITMAP "assets\welcome.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "assets\header.bmp"
!define MUI_WELCOMEPAGE_TITLE "欢迎安装灰Desk"
!define MUI_WELCOMEPAGE_TEXT "识别远程码，核对后快速连接。$\r$\n$\r$\n此安装包包含程序与识别模型，安装时无需联网。$\r$\n$\r$\n安装或升级会保留已有设置。请先从托盘退出正在运行的灰Desk。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\灰Desk.exe"
!define MUI_FINISHPAGE_RUN_TEXT "运行灰Desk"
!define MUI_FINISHPAGE_TEXT "灰Desk已安装。桌面和开始菜单均已创建快捷方式。"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    IfSilent +2
    MessageBox MB_ICONSTOP "灰Desk需要64位 Windows 10 或 Windows 11。"
    SetErrorLevel 11
    Quit
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    IfSilent +2
    MessageBox MB_ICONSTOP "灰Desk需要 Windows 10 版本2004（19041）或更高版本。"
    SetErrorLevel 11
    Quit
  ${EndIf}
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  ${If} $0 < 19041
    IfSilent +2
    MessageBox MB_ICONSTOP "灰Desk需要 Windows 10 版本2004（19041）或更高版本。"
    SetErrorLevel 11
    Quit
  ${EndIf}
  Call CheckRunning
FunctionEnd

!macro CHECK_RUNNING PREFIX
Function ${PREFIX}CheckRunning
  System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\GrayDeskV2-SingleInstance") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    IfSilent +2
    MessageBox MB_ICONEXCLAMATION "灰Desk正在运行。请从托盘菜单退出灰Desk，再重试安装或卸载。"
    SetErrorLevel 10
    Quit
  ${EndIf}
FunctionEnd
!macroend
!insertmacro CHECK_RUNNING ""
!insertmacro CHECK_RUNNING "un."

Section "灰Desk" MainSection
  Call CheckRunning
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${PAYLOAD}\*.*"
  WriteUninstaller "$INSTDIR\卸载灰Desk.exe"
  CreateDirectory "$SMPROGRAMS\灰Desk"
  CreateShortcut "$SMPROGRAMS\灰Desk\灰Desk.lnk" "$INSTDIR\灰Desk.exe" "" "$INSTDIR\灰Desk.exe" 0
  CreateShortcut "$SMPROGRAMS\灰Desk\卸载灰Desk.lnk" "$INSTDIR\卸载灰Desk.exe"
  CreateShortcut "$DESKTOP\灰Desk.lnk" "$INSTDIR\灰Desk.exe" "" "$INSTDIR\灰Desk.exe" 0
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "灰Desk"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\灰Desk.exe,0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\卸载灰Desk.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\卸载灰Desk.exe$\" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0
SectionEnd

Function un.onInit
  SetShellVarContext current
  Call un.CheckRunning
FunctionEnd

Section "Uninstall"
  Call un.CheckRunning
  SetShellVarContext current
  !include "obj\uninstall-files.nsh"
  Delete "$INSTDIR\卸载灰Desk.exe"
  Delete "$DESKTOP\灰Desk.lnk"
  Delete "$SMPROGRAMS\灰Desk\灰Desk.lnk"
  Delete "$SMPROGRAMS\灰Desk\卸载灰Desk.lnk"
  RMDir "$SMPROGRAMS\灰Desk"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ; Keep preferences, history/logs and user-added files. Only shipped files are removed.
SectionEnd
