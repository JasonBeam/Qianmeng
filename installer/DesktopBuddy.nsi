Unicode true
!ifdef NSIS_WIN32_MAKENSIS
  !define HOST_SEP "\"
!else
  !define HOST_SEP "/"
!endif
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!ifndef APP_VERSION
  !error "APP_VERSION is required"
!endif
!ifndef APP_FILE_VERSION
  !error "APP_FILE_VERSION is required"
!endif
!ifndef APP_ARCH
  !error "APP_ARCH must be x64"
!endif
!if "${APP_ARCH}" != "x64"
  !error "This installer only supports APP_ARCH=x64"
!endif
!ifndef APP_DIR
  !error "APP_DIR is required"
!endif
!ifndef APP_DIST
  !error "APP_DIST is required"
!endif
!ifndef APP_ICON
  !error "APP_ICON is required"
!endif

!define APP_NAME "DesktopBuddy"
!define APP_TITLE "浅梦 · Windows 桌面伙伴"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopBuddy"

Name "${APP_TITLE} ${APP_VERSION} (${APP_ARCH})"
OutFile "${APP_DIST}${HOST_SEP}DesktopBuddy-Setup-${APP_VERSION}-win-${APP_ARCH}.exe"
InstallDir "$LOCALAPPDATA\Programs\DesktopBuddy"
InstallDirRegKey HKCU "Software\DesktopBuddy" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
ShowInstDetails show
ShowUninstDetails show
BrandingText "DesktopBuddy ${APP_VERSION}"

VIProductVersion "${APP_FILE_VERSION}"
VIAddVersionKey "FileVersion" "${APP_FILE_VERSION}"
VIAddVersionKey "ProductName" "DesktopBuddy"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "FileDescription" "浅梦 Windows 桌面伙伴安装程序 (${APP_ARCH})"
VIAddVersionKey "CompanyName" "DesktopBuddy"
VIAddVersionKey "LegalCopyright" "Copyright 2026 DesktopBuddy"

!define MUI_ABORTWARNING
!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
Var StartMenuFolder
!define MUI_STARTMENUPAGE_DEFAULTFOLDER "浅梦"
!define MUI_STARTMENUPAGE_REGISTRY_ROOT HKCU
!define MUI_STARTMENUPAGE_REGISTRY_KEY "Software\DesktopBuddy"
!define MUI_STARTMENUPAGE_REGISTRY_VALUENAME "StartMenuFolder"
!define MUI_FINISHPAGE_RUN "$INSTDIR\DesktopBuddy.exe"
!define MUI_FINISHPAGE_RUN_TEXT "安装完成后启动浅梦"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_STARTMENU Application $StartMenuFolder
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "此安装包需要 64 位 x64 Windows 10/11。" /SD IDOK
    Abort
  ${EndIf}
  SetShellVarContext current
  SetRegView 64
FunctionEnd

!macro FindBuddyWindow
  FindWindow $0 "" "DesktopBuddy.Pet"
  ${If} $0 == 0
    FindWindow $0 "" "小伴"
  ${EndIf}
!macroend

!macro DefineCloseBuddy PREFIX
Function ${PREFIX}CloseBuddy
  !insertmacro FindBuddyWindow
  ${If} $0 != 0
    ; New versions save state and release resources before exiting.
    System::Call 'user32::PostMessageW(p r0, i 0x802A, p 0, p 0)'
    StrCpy $1 0
wait_for_buddy_exit:
    Sleep 100
    !insertmacro FindBuddyWindow
    StrCmp $0 0 buddy_exit_finished
    IntOp $1 $1 + 1
    IntCmp $1 30 force_buddy_exit wait_for_buddy_exit force_buddy_exit
force_buddy_exit:
    ; Do not terminate applications that the user opened through the pet.
    ; USERDOMAIN may contain WORKGROUP in an SSH session. Ask Windows for the actual account.
    System::Call 'secur32::GetUserNameExW(i 2, w .r2, *i ${NSIS_MAX_STRLEN}) i.r3'
    IntOp $3 $3 & 0xFF ; GetUserNameEx returns an 8-bit BOOLEAN.
    ${If} $3 == 0
      MessageBox MB_OK|MB_ICONSTOP "无法确认当前 Windows 用户，请手动退出浅梦后重试。" /SD IDOK
      Abort
    ${EndIf}
    nsExec::ExecToLog '"$SYSDIR\taskkill.exe" /F /FI "USERNAME eq $2" /IM DesktopBuddy.exe'
    Pop $0
  ${EndIf}
buddy_exit_finished:
FunctionEnd
!macroend

!insertmacro DefineCloseBuddy ""
!insertmacro DefineCloseBuddy "un."

Function un.onInit
  SetShellVarContext current
  SetRegView 64
FunctionEnd

Section "Install" SEC_INSTALL
  SetShellVarContext current
  SetRegView 64
  DetailPrint "正在保存并关闭已运行的浅梦。"
  Call CloseBuddy

  SetOutPath "$INSTDIR"
  ; Extract to a separate file so a locked old EXE cannot cause a silent skip.
  ClearErrors
  Delete "$INSTDIR\DesktopBuddy.pending.exe"
  ClearErrors
  File /oname=DesktopBuddy.pending.exe "${APP_DIR}${HOST_SEP}DesktopBuddy.exe"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "无法解压程序文件，请检查安装目录权限和磁盘空间。" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  Delete "$INSTDIR\DesktopBuddy.previous.exe"
  ${If} ${FileExists} "$INSTDIR\DesktopBuddy.exe"
    ClearErrors
    Rename "$INSTDIR\DesktopBuddy.exe" "$INSTDIR\DesktopBuddy.previous.exe"
    ${If} ${Errors}
      Delete "$INSTDIR\DesktopBuddy.pending.exe"
      MessageBox MB_OK|MB_ICONSTOP "无法替换已运行的程序，请退出浅梦后重试。" /SD IDOK
      SetErrorLevel 1
      Abort
    ${EndIf}
  ${EndIf}
  ClearErrors
  Rename "$INSTDIR\DesktopBuddy.pending.exe" "$INSTDIR\DesktopBuddy.exe"
  ${If} ${Errors}
    Rename "$INSTDIR\DesktopBuddy.previous.exe" "$INSTDIR\DesktopBuddy.exe"
    MessageBox MB_OK|MB_ICONSTOP "无法更新程序，已尝试恢复上一版本。请检查安装目录权限后重试。" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  Delete "$INSTDIR\DesktopBuddy.previous.exe"
  ClearErrors
  File /r "${APP_DIR}${HOST_SEP}licenses"
  File /r "${APP_DIR}${HOST_SEP}tools"
  !if /FileExists "${APP_DIR}${HOST_SEP}models${HOST_SEP}default-companion.glb"
    File /r "${APP_DIR}${HOST_SEP}models"
  !endif
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "无法创建卸载程序，请检查安装目录权限后重试。" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}

  WriteRegStr HKCU "Software\DesktopBuddy" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "浅梦（DesktopBuddy）${APP_VERSION} (${APP_ARCH})"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "DesktopBuddy"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\DesktopBuddy.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Architecture" "${APP_ARCH}"

  !insertmacro MUI_STARTMENU_WRITE_BEGIN Application
    ${If} $StartMenuFolder != ""
      CreateDirectory "$SMPROGRAMS\$StartMenuFolder"
      Delete "$SMPROGRAMS\$StartMenuFolder\小伴.lnk"
      Delete "$SMPROGRAMS\$StartMenuFolder\卸载小伴.lnk"
      CreateShortcut "$SMPROGRAMS\$StartMenuFolder\浅梦.lnk" "$INSTDIR\DesktopBuddy.exe" "" "$INSTDIR\DesktopBuddy.exe" 0
      CreateShortcut "$SMPROGRAMS\$StartMenuFolder\卸载浅梦.lnk" "$INSTDIR\Uninstall.exe"
    ${EndIf}
  !insertmacro MUI_STARTMENU_WRITE_END

  SetAutoClose true
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  DetailPrint "正在关闭浅梦。"
  Call un.CloseBuddy

  ; Silent uninstall defaults to preserving user data and credentials.
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON1 \
    "是否保留浅梦的设置、成长数据和插件？选择“否”会同时删除本机 API Key。" \
    /SD IDYES IDYES keep_user_data
  DetailPrint "正在移除本机设置、成长数据、插件和 API Key。"
  ExecWait '"$INSTDIR\DesktopBuddy.exe" --uninstall-remove-user-data' $0
  IntCmp $0 0 user_data_removed user_data_failed user_data_failed
user_data_failed:
  MessageBox MB_OK|MB_ICONEXCLAMATION "用户数据没有完整移除；安装程序将保留程序文件，请重新运行卸载程序并选择保留数据，或检查文件权限。" /SD IDOK
  Abort
user_data_removed:
  DetailPrint "本机用户数据与 API Key 已移除。"
keep_user_data:

  ClearErrors
  Delete "$INSTDIR\DesktopBuddy.exe"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "无法移除程序文件。请退出所有会话中的浅梦后重试卸载。" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}

  !insertmacro MUI_STARTMENU_GETFOLDER Application $StartMenuFolder
  ${If} $StartMenuFolder != ""
    Delete "$SMPROGRAMS\$StartMenuFolder\小伴.lnk"
    Delete "$SMPROGRAMS\$StartMenuFolder\卸载小伴.lnk"
    Delete "$SMPROGRAMS\$StartMenuFolder\浅梦.lnk"
    Delete "$SMPROGRAMS\$StartMenuFolder\卸载浅梦.lnk"
    RMDir "$SMPROGRAMS\$StartMenuFolder"
  ${EndIf}
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "DesktopBuddy"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "Software\DesktopBuddy"
  Delete "$INSTDIR\DesktopBuddy.previous.exe"
  Delete "$INSTDIR\DesktopBuddy.pending.exe"
  Delete "$INSTDIR\licenses\Microsoft.NETCore.App\LICENSE.TXT"
  Delete "$INSTDIR\licenses\Microsoft.NETCore.App\THIRD-PARTY-NOTICES.TXT"
  Delete "$INSTDIR\licenses\Microsoft.WindowsDesktop.App\LICENSE.TXT"
  RMDir "$INSTDIR\licenses\Microsoft.NETCore.App"
  RMDir "$INSTDIR\licenses\Microsoft.WindowsDesktop.App"
  Delete "$INSTDIR\licenses\meshoptimizer\LICENSE.md"
  Delete "$INSTDIR\licenses\meshoptimizer\THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\licenses\meshoptimizer\PROVENANCE.md"
  RMDir "$INSTDIR\licenses\meshoptimizer"
  RMDir "$INSTDIR\licenses"
  Delete "$INSTDIR\tools\gltfpack.exe"
  RMDir "$INSTDIR\tools"
  Delete "$INSTDIR\models\default-companion.glb"
  RMDir "$INSTDIR\models"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
