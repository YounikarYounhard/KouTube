; KouTube for Windows: one per-user install (no admin rights), shortcuts in Start and on the desktop, an entry in
; "Apps & features". A newer setup installs over the old one; the tools it downloaded and the library stay.
; The release workflow builds it:  makensis /DVERSION=x.y.z /DEXE=<path>\KouTube.exe /DOUT=<path>\KouTube-Setup-x.y.z.exe installer.nsi

Unicode true
!include "MUI2.nsh"

!define UNINST "Software\Microsoft\Windows\CurrentVersion\Uninstall\KouTube"

Name "KouTube ${VERSION}"
OutFile "${OUT}"
InstallDir "$LOCALAPPDATA\Programs\KouTube"
InstallDirRegKey HKCU "Software\KouTube" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma

!define MUI_ICON "KouTube\koutube.ico"
!define MUI_UNICON "KouTube\koutube.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\KouTube.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить KouTube"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

Section
  ; an open KouTube holds its exe; it keeps the queue and history safe on a sudden stop, so closing it is harmless
  nsExec::Exec 'taskkill /f /im KouTube.exe'
  Sleep 800
  SetOutPath "$INSTDIR"
  File "/oname=KouTube.exe" "${EXE}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\KouTube.lnk" "$INSTDIR\KouTube.exe"
  CreateShortcut "$DESKTOP\KouTube.lnk" "$INSTDIR\KouTube.exe"
  WriteRegStr HKCU "Software\KouTube" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINST}" "DisplayName" "KouTube"
  WriteRegStr HKCU "${UNINST}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST}" "Publisher" "KouTube"
  WriteRegStr HKCU "${UNINST}" "DisplayIcon" "$INSTDIR\KouTube.exe"
  WriteRegStr HKCU "${UNINST}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UNINST}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST}" "NoRepair" 1
SectionEnd

; the library, settings and YouTube sign-in live in %LOCALAPPDATA%\KouTube and on the sticks: uninstalling leaves them
Section "Uninstall"
  nsExec::Exec 'taskkill /f /im KouTube.exe'
  Sleep 800
  Delete "$INSTDIR\KouTube.exe"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR\tools"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\KouTube.lnk"
  Delete "$DESKTOP\KouTube.lnk"
  DeleteRegKey HKCU "${UNINST}"
  DeleteRegKey HKCU "Software\KouTube"
SectionEnd
