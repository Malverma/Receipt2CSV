; One-click POSscan installer: double-click, progress bar, app opens.
; No admin rights and no questions. Built by packaging/build-installer.sh.
;
; Programs go in %LOCALAPPDATA%\Programs\POSscan, which is replaced on every
; install. The store's data is in %LOCALAPPDATA%\POSscan and Documents\POSscan.
; Neither this installer nor the uninstaller touches those folders.

!ifndef VERSION | STAGE | OUTFILE
  !error "Run packaging/build-installer.sh instead of calling makensis directly"
!endif

!define APP "POSscan"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}"

Unicode true
SetCompressor /SOLID lzma
RequestExecutionLevel user
ManifestDPIAware true
ManifestSupportedOS Win10

Name "${APP}"
OutFile "${OUTFILE}"
Icon "..\src\POSscan.App\Assets\posscan.ico"
UninstallIcon "..\src\POSscan.App\Assets\posscan.ico"
Caption "Installing ${APP}"
UninstallCaption "Uninstall ${APP}"
BrandingText " "
ShowInstDetails nevershow
ShowUninstDetails nevershow
AutoCloseWindow true
AllowSkipFiles off

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "FileDescription" "${APP} installer"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" " "

; The progress bar is the only page.
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

; The folder is fixed, even if /D= is passed, because it gets deleted on
; every install and must never point at anything else.
Function .onInit
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\${APP}"
FunctionEnd

Function un.onInit
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\${APP}"
FunctionEnd

Section
  ; Updating over a running copy would fail halfway, and closing it for them
  ; could lose work. A running exe can't be opened for writing.
  check_running:
    IfFileExists "$INSTDIR\${APP}.exe" 0 not_running
    ClearErrors
    FileOpen $0 "$INSTDIR\${APP}.exe" a
    IfErrors running
    FileClose $0
    Goto not_running
  running:
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION \
      "POSscan is open.$\r$\n$\r$\nPlease close it, then click Retry." \
      /SD IDCANCEL IDRETRY check_running
    Abort "POSscan is still open."
  not_running:

  ; Clearing the folder stops files from an older version being left behind.
  RMDir /r "$INSTDIR"

  SetOutPath "$INSTDIR"
  File /r "${STAGE}\*"
  WriteUninstaller "$INSTDIR\Uninstall ${APP}.exe"

  CreateShortCut "$DESKTOP\${APP}.lnk" "$INSTDIR\${APP}.exe"
  CreateShortCut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\${APP}.exe"

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${APP}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP}.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall ${APP}.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall ${APP}.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${SIZE_KB}
SectionEnd

Function .onInstSuccess
  IfSilent done
  Exec '"$INSTDIR\${APP}.exe"'
  done:
FunctionEnd

Section Uninstall
  check_running:
    IfFileExists "$INSTDIR\${APP}.exe" 0 not_running
    ClearErrors
    FileOpen $0 "$INSTDIR\${APP}.exe" a
    IfErrors running
    FileClose $0
    Goto not_running
  running:
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION \
      "POSscan is open.$\r$\n$\r$\nPlease close it, then click Retry." \
      /SD IDCANCEL IDRETRY check_running
    Abort "POSscan is still open."
  not_running:

  Delete "$DESKTOP\${APP}.lnk"
  Delete "$SMPROGRAMS\${APP}.lnk"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  RMDir /r "$INSTDIR"
SectionEnd
