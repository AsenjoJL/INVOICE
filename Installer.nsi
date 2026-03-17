; ======================================================================
; HazelInvoice Offline Installer (NSIS)
; ======================================================================

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"
!include "StrFunc.nsh"

; Enables ${StrStr}
${StrStr}

; -------------------- DEFINES (FILL THESE IN) -------------------------
!define APP_NAME        "HazelInvoice"
!define APP_EXE         "HazelInvoice.exe"
!define APP_PORT        "5050"
; Keep Postgres on the default port so installs on new PCs are predictable and
; compatible with existing Postgres installs.
!define PG_PORT         "5432"

; Build-time paths (relative to this .nsi file) for repeatable builds.
!define PUBLISH_DIR     "${__FILEDIR__}\\bin\\Release\\net8.0\\win-x64\\publish"
; Put the PostgreSQL Windows x64 installer here and name it exactly:
;   installer\postgres_installer.exe
; This keeps the NSIS script stable even when PostgreSQL versions change.
!define POSTGRES_FILE   "${__FILEDIR__}\\installer\\postgres_installer.exe"

!define DB_NAME         "hazel_invoice"
!define DB_USER         "hazel_user"
; Default passwords are only initial values shown in the installer UI.
; Users can (and should) change these during install.
!define DEFAULT_DB_PASS       "hazel_invoice_2026"
!define DEFAULT_PG_SUPER_PASS ""

; Optional fallback if registry not found
!define PG_BIN_FALLBACK "$PROGRAMFILES\\PostgreSQL\\14\\bin"

; -------------------- GENERAL SETTINGS -------------------------------
Name "${APP_NAME}"
; Allow overriding the output filename at build time:
;   makensis.exe /DOUTFILE=HazelInvoice_Installer_build.exe Installer.nsi
!ifndef OUTFILE
  !define OUTFILE "${APP_NAME}_Installer.exe"
!endif
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES\\${APP_NAME}"
RequestExecutionLevel admin
ShowInstDetails show
ShowUninstDetails show

; Large bundles (app + postgres installer) need strong compression to keep the
; resulting installer size manageable and avoid 32-bit makensis mmap limits.
SetCompress force
SetCompressor /SOLID lzma

Var AppPort
Var PortField
Var LogFile
Var AppSettingsFile
Var PgBin
Var PgSuperPass
Var PgPassField
Var PgPort
Var PgPortField
Var DbPass
Var DbPassField

; -------------------- UI PAGES ---------------------------------------
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page custom PortPageCreate PortPageLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

; -------------------- LANGUAGE ---------------------------------------
!insertmacro MUI_LANGUAGE "English"

; -------------------- LOGGING ----------------------------------------
Function LogLine
  Exch $0
  FileOpen $1 $LogFile a
  FileWrite $1 "$0$\r$\n"
  FileClose $1
FunctionEnd

!macro LOG MESSAGE
  Push "${MESSAGE}"
  Call LogLine
!macroend

; -------------------- PORT PAGE --------------------------------------
Function PortPageCreate
  nsDialogs::Create 1018
  Pop $0

  StrCpy $AppPort "${APP_PORT}"
  StrCpy $PgPort "${PG_PORT}"
  StrCpy $PgSuperPass "${DEFAULT_PG_SUPER_PASS}"
  StrCpy $DbPass "${DEFAULT_DB_PASS}"

  ${NSD_CreateLabel} 0 0 100% 20u "Choose the port for the local app URL:"
  Pop $1

  ${NSD_CreateText} 0 24u 100% 12u "$AppPort"
  Pop $PortField

  ${NSD_CreateLabel} 0 46u 100% 20u "PostgreSQL port (default: ${PG_PORT}):"
  Pop $1

  ${NSD_CreateText} 0 70u 100% 12u "$PgPort"
  Pop $PgPortField

  ${NSD_CreateLabel} 0 92u 100% 20u "PostgreSQL 'postgres' password (for DB setup):"
  Pop $1

  ${NSD_CreatePassword} 0 116u 100% 12u "$PgSuperPass"
  Pop $PgPassField

  ${NSD_CreateLabel} 0 138u 100% 20u "App database password (for user: ${DB_USER}):"
  Pop $1

  ${NSD_CreatePassword} 0 162u 100% 12u "$DbPass"
  Pop $DbPassField

  nsDialogs::Show
FunctionEnd

Function PortPageLeave
  ${NSD_GetText} $PortField $AppPort
  ${NSD_GetText} $PgPortField $PgPort
  ${NSD_GetText} $PgPassField $PgSuperPass
  ${NSD_GetText} $DbPassField $DbPass

  ${If} $AppPort == ""
    MessageBox MB_ICONEXCLAMATION "Port is required."
    Abort
  ${EndIf}

  ${If} $PgPort == ""
    MessageBox MB_ICONEXCLAMATION "PostgreSQL port is required."
    Abort
  ${EndIf}

  ${If} $PgSuperPass == ""
    MessageBox MB_ICONEXCLAMATION "PostgreSQL password is required."
    Abort
  ${EndIf}

  ${If} $DbPass == ""
    MessageBox MB_ICONEXCLAMATION "App DB password is required."
    Abort
  ${EndIf}

  ; Avoid characters that break our quoting in psql/cmd.
  ${StrStr} $0 $DbPass "'"
  ${If} $0 != ""
    MessageBox MB_ICONEXCLAMATION "App DB password cannot include: apostrophe, quote, ampersand, or pipe. Please use letters/numbers/underscore."
    Abort
  ${EndIf}
  ${StrStr} $0 $DbPass '"'
  ${If} $0 != ""
    MessageBox MB_ICONEXCLAMATION "App DB password cannot include: apostrophe, quote, ampersand, or pipe. Please use letters/numbers/underscore."
    Abort
  ${EndIf}
  ${StrStr} $0 $DbPass "&"
  ${If} $0 != ""
    MessageBox MB_ICONEXCLAMATION "App DB password cannot include: apostrophe, quote, ampersand, or pipe. Please use letters/numbers/underscore."
    Abort
  ${EndIf}
  ${StrStr} $0 $DbPass "|"
  ${If} $0 != ""
    MessageBox MB_ICONEXCLAMATION "App DB password cannot include: apostrophe, quote, ampersand, or pipe. Please use letters/numbers/underscore."
    Abort
  ${EndIf}

  ; Validate PostgreSQL port is numeric and within range (allow already-in-use).
  nsExec::ExecToStack 'powershell -NoProfile -Command "try { $$p=[int]''$PgPort''; if ($$p -lt 1 -or $$p -gt 65535) { exit 1 } else { exit 0 } } catch { exit 1 }"'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION "PostgreSQL port must be a number between 1 and 65535."
    Abort
  ${EndIf}

  nsExec::ExecToStack 'powershell -NoProfile -Command "if (Get-NetTCPConnection -LocalPort $AppPort -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }"'
  Pop $0

  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION "Port $AppPort is already in use. Choose another port."
    Abort
  ${EndIf}
FunctionEnd

; -------------------- POSTGRES HELPERS -------------------------------
Function GetPostgresBin
  StrCpy $PgBin ""
  EnumRegKey $0 HKLM "SOFTWARE\\PostgreSQL\\Installations" 0
  ${IfNot} ${Errors}
    ReadRegStr $1 HKLM "SOFTWARE\\PostgreSQL\\Installations\\$0" "Base Directory"
    ${If} $1 != ""
      StrCpy $PgBin "$1\\bin"
    ${EndIf}
  ${EndIf}

  ${If} $PgBin == ""
    StrCpy $PgBin "${PG_BIN_FALLBACK}"
  ${EndIf}
FunctionEnd

Function un.GetPostgresBin
  StrCpy $PgBin ""
  EnumRegKey $0 HKLM "SOFTWARE\\PostgreSQL\\Installations" 0
  ${IfNot} ${Errors}
    ReadRegStr $1 HKLM "SOFTWARE\\PostgreSQL\\Installations\\$0" "Base Directory"
    ${If} $1 != ""
      StrCpy $PgBin "$1\\bin"
    ${EndIf}
  ${EndIf}

  ${If} $PgBin == ""
    StrCpy $PgBin "${PG_BIN_FALLBACK}"
  ${EndIf}
FunctionEnd

; -------------------- MAIN INSTALL -----------------------------------
Section "Install"
  ; Ensure we can read 64-bit registry keys (PostgreSQL is typically 64-bit).
  SetRegView 64

  SetOutPath "$INSTDIR"
  SetOverwrite on

  StrCpy $LogFile "$INSTDIR\\install.log"
  !insertmacro LOG "== Installing ${APP_NAME} =="

  ; Clean stale framework-dependent files (single-file publish embeds runtimeconfig)
  !insertmacro LOG "Cleaning old app files..."
  Delete "$INSTDIR\\${APP_NAME}.runtimeconfig.json"
  Delete "$INSTDIR\\${APP_NAME}.deps.json"
  Delete "$INSTDIR\\${APP_NAME}.dll"
  Delete "$INSTDIR\\${APP_NAME}.pdb"
  Delete "$INSTDIR\\*.dll"
  Delete "$INSTDIR\\*.pdb"
  RMDir /r "$INSTDIR\\cs"
  RMDir /r "$INSTDIR\\de"
  RMDir /r "$INSTDIR\\es"
  RMDir /r "$INSTDIR\\fr"
  RMDir /r "$INSTDIR\\it"
  RMDir /r "$INSTDIR\\ja"
  RMDir /r "$INSTDIR\\ko"
  RMDir /r "$INSTDIR\\pl"
  RMDir /r "$INSTDIR\\pt-BR"
  RMDir /r "$INSTDIR\\ru"
  RMDir /r "$INSTDIR\\tr"
  RMDir /r "$INSTDIR\\zh-Hans"
  RMDir /r "$INSTDIR\\zh-Hant"

  ; 1) Copy publish output
  !insertmacro LOG "Copying publish output..."
  File /r "${PUBLISH_DIR}\\*.*"

  ; 2) Install PostgreSQL if not present
  !insertmacro LOG "Checking PostgreSQL installation..."
  EnumRegKey $0 HKLM "SOFTWARE\\PostgreSQL\\Installations" 0
  ${If} ${Errors}
    !insertmacro LOG "PostgreSQL not found. Installing..."
    ; Make the bundled PostgreSQL installer optional at build-time.
    ; If the file isn't present, the installer can still be built, but will
    ; require PostgreSQL to be installed manually on the target machine.
    File /nonfatal /oname=$TEMP\\postgres_installer.exe "${POSTGRES_FILE}"

    IfFileExists "$TEMP\\postgres_installer.exe" +2 0
      Goto postgres_missing_installer

    ; EXE silent install (adjust flags if your installer differs)
    ExecWait '"$TEMP\\postgres_installer.exe" --mode unattended --unattendedmodeui none --superpassword "$PgSuperPass" --serverport $PgPort'
    !insertmacro LOG "PostgreSQL installer finished."
    Goto postgres_install_done

postgres_missing_installer:
    MessageBox MB_ICONSTOP|MB_OK "PostgreSQL is not installed and HazelInvoice installer can't find the bundled PostgreSQL installer file.$\r$\n$\r$\nExpected file path:$\r$\n${POSTGRES_FILE}$\r$\n$\r$\nFix: Download the PostgreSQL Windows x64 installer, place it in the HazelInvoice 'installer' folder (same folder as Installer.nsi), then rebuild the installer.$\r$\n$\r$\nOr install PostgreSQL manually and run the HazelInvoice installer again."
    Abort

postgres_install_done:
  ${Else}
    !insertmacro LOG "PostgreSQL already installed. Skipping installer."
  ${EndIf}

  ; 3) Find Postgres bin
  Call GetPostgresBin
  !insertmacro LOG "Postgres bin: $PgBin"

  ; Set PGPASSWORD to avoid interactive prompts for psql
  System::Call 'Kernel32::SetEnvironmentVariable(t, t) i("PGPASSWORD", "$PgSuperPass")'

  ; 4) Create role (if not exists) and set password
  !insertmacro LOG "Ensuring DB user exists..."
  nsExec::ExecToLog '"$PgBin\\psql.exe" -h 127.0.0.1 -p $PgPort -U postgres -d postgres -v ON_ERROR_STOP=1 -q -X -c "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=''${DB_USER}'') THEN CREATE ROLE \"${DB_USER}\" LOGIN; END IF; ALTER ROLE \"${DB_USER}\" WITH PASSWORD ''$DbPass''; END $$;"'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "Database setup failed while creating user '${DB_USER}'.$\r$\n$\r$\nCommon cause: wrong PostgreSQL 'postgres' password or a different existing PostgreSQL installation.$\r$\n$\r$\nCheck: $INSTDIR\\install.log"
    Abort
  ${EndIf}

  ; 5) Create database if missing
  !insertmacro LOG "Ensuring database exists..."
  nsExec::ExecToLog '"$PgBin\\psql.exe" -h 127.0.0.1 -p $PgPort -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT ''CREATE DATABASE \"${DB_NAME}\" OWNER \"${DB_USER}\"'' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname=''${DB_NAME}'') \gexec"'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "Database setup failed while creating database '${DB_NAME}'.$\r$\n$\r$\nCheck: $INSTDIR\\install.log"
    Abort
  ${EndIf}

  ; Clear PGPASSWORD
  System::Call 'Kernel32::SetEnvironmentVariable(t, t) i("PGPASSWORD", "")'

  ; 6) Update appsettings connection string
  !insertmacro LOG "Updating connection string..."
  StrCpy $AppSettingsFile "$INSTDIR\\appsettings.json"
  IfFileExists "$INSTDIR\\appsettings.Production.json" 0 +2
  StrCpy $AppSettingsFile "$INSTDIR\\appsettings.Production.json"

  nsExec::ExecToLog 'powershell -NoProfile -ExecutionPolicy Bypass -Command "$$json = Get-Content -Raw ''$AppSettingsFile'' | ConvertFrom-Json; if (-not $$json.ConnectionStrings) { $$json | Add-Member -NotePropertyName ConnectionStrings -NotePropertyValue (@{}) }; $$json.ConnectionStrings.DefaultConnection = ''Host=localhost;Port=$PgPort;Database=${DB_NAME};Username=${DB_USER};Password=$DbPass;''; $$json | ConvertTo-Json -Depth 10 | Set-Content ''$AppSettingsFile''"'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "Failed to update the app connection string.$\r$\n$\r$\nCheck: $INSTDIR\\install.log"
    Abort
  ${EndIf}

  ; 7) Migrations run on first app launch (avoid blocking installer)
  !insertmacro LOG "Skipping explicit migrations; app will migrate on first launch."

  ; ---- Option B (commented):
  ; ExecWait '"$SYSDIR\\cmd.exe" /C "dotnet $INSTDIR\\${APP_NAME}.dll --migrate"'

  ; 8) Create launcher script
  !insertmacro LOG "Creating launcher script..."
  FileOpen $2 "$INSTDIR\\launch_app.cmd" w
  FileWrite $2 "@echo off$\r$\n"
  FileWrite $2 "set ASPNETCORE_URLS=http://localhost:$AppPort$\r$\n"
  FileWrite $2 "start $\"$\" $\"%~dp0${APP_EXE}$\"$\r$\n"
  FileWrite $2 "timeout /t 2 >nul$\r$\n"
  FileWrite $2 "start $\"$\" $\"http://localhost:$AppPort$\"$\r$\n"
  FileClose $2

  ; 8b) Create kiosk (silent print) launcher script
  !insertmacro LOG "Creating kiosk launcher script..."
  FileOpen $3 "$INSTDIR\\launch_kiosk.cmd" w
  FileWrite $3 "@echo off$\r$\n"
  FileWrite $3 "set ASPNETCORE_URLS=http://localhost:$AppPort$\r$\n"
  FileWrite $3 "set APP_URL=http://localhost:$AppPort$\r$\n"
  FileWrite $3 "set KIOSK_DIR=%LOCALAPPDATA%\\${APP_NAME}\\kiosk_profile$\r$\n"
  FileWrite $3 "if not exist $\"%KIOSK_DIR%$\" mkdir $\"%KIOSK_DIR%$\"$\r$\n"
  FileWrite $3 "start $\"$\" $\"%~dp0${APP_EXE}$\"$\r$\n"
  FileWrite $3 "timeout /t 2 >nul$\r$\n"
  FileWrite $3 "set EDGE=%ProgramFiles(x86)%\\Microsoft\\Edge\\Application\\msedge.exe$\r$\n"
  FileWrite $3 "if exist $\"%EDGE%$\" (start $\"$\" $\"%EDGE%$\" --user-data-dir=$\"%KIOSK_DIR%\\edge$\" --no-first-run --kiosk $\"%APP_URL%$\" --edge-kiosk-type=fullscreen --kiosk-printing & goto :eof)$\r$\n"
  FileWrite $3 "set EDGE=%ProgramFiles%\\Microsoft\\Edge\\Application\\msedge.exe$\r$\n"
  FileWrite $3 "if exist $\"%EDGE%$\" (start $\"$\" $\"%EDGE%$\" --user-data-dir=$\"%KIOSK_DIR%\\edge$\" --no-first-run --kiosk $\"%APP_URL%$\" --edge-kiosk-type=fullscreen --kiosk-printing & goto :eof)$\r$\n"
  FileWrite $3 "set CHROME=%ProgramFiles%\\Google\\Chrome\\Application\\chrome.exe$\r$\n"
  FileWrite $3 "if exist $\"%CHROME%$\" (start $\"$\" $\"%CHROME%$\" --user-data-dir=$\"%KIOSK_DIR%\\chrome$\" --no-first-run --kiosk --kiosk-printing --app=$\"%APP_URL%$\" & goto :eof)$\r$\n"
  FileWrite $3 "set CHROME=%ProgramFiles(x86)%\\Google\\Chrome\\Application\\chrome.exe$\r$\n"
  FileWrite $3 "if exist $\"%CHROME%$\" (start $\"$\" $\"%CHROME%$\" --user-data-dir=$\"%KIOSK_DIR%\\chrome$\" --no-first-run --kiosk --kiosk-printing --app=$\"%APP_URL%$\" & goto :eof)$\r$\n"
  FileWrite $3 "start $\"$\" $\"%APP_URL%$\"$\r$\n"
  FileClose $3

  ; 9) Shortcuts
  CreateDirectory "$SMPROGRAMS\\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\\${APP_NAME}\\Open ${APP_NAME}.lnk" "$INSTDIR\\launch_app.cmd"
  CreateShortcut "$SMPROGRAMS\\${APP_NAME}\\${APP_NAME} (Kiosk Print).lnk" "$INSTDIR\\launch_kiosk.cmd"
  CreateShortcut "$DESKTOP\\Open ${APP_NAME}.lnk" "$INSTDIR\\launch_app.cmd"
  CreateShortcut "$DESKTOP\\${APP_NAME} (Kiosk Print).lnk" "$INSTDIR\\launch_kiosk.cmd"

  ; 10) Uninstaller
  WriteUninstaller "$INSTDIR\\Uninstall.exe"
  WriteRegStr HKLM "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\${APP_NAME}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKLM "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\${APP_NAME}" "UninstallString" "$INSTDIR\\Uninstall.exe"
  WriteRegStr HKLM "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\${APP_NAME}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\${APP_NAME}" "DisplayIcon" "$INSTDIR\\${APP_EXE}"
  ; Store DB port for troubleshooting / uninstall helpers
  WriteRegStr HKLM "Software\\${APP_NAME}" "PgPort" "$PgPort"

  !insertmacro LOG "Install completed."
SectionEnd

; -------------------- UNINSTALL --------------------------------------
Section "Uninstall"
  SetRegView 64

  ; Locate Postgres bin for optional DB drop
  Call un.GetPostgresBin
  ; Best-effort: read the DB port that was selected at install time
  ReadRegStr $PgPort HKLM "Software\\${APP_NAME}" "PgPort"
  ${If} $PgPort == ""
    StrCpy $PgPort "${PG_PORT}"
  ${EndIf}

  ; Stop app if running
  nsExec::ExecToLog 'taskkill /F /IM "${APP_EXE}"'

  ; Remove shortcuts
  Delete "$DESKTOP\\Open ${APP_NAME}.lnk"
  Delete "$DESKTOP\\${APP_NAME} (Kiosk Print).lnk"
  Delete "$SMPROGRAMS\\${APP_NAME}\\Open ${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\\${APP_NAME}\\${APP_NAME} (Kiosk Print).lnk"
  RMDir  "$SMPROGRAMS\\${APP_NAME}"

  ; Optional: ask to drop DB
  MessageBox MB_ICONQUESTION|MB_YESNO "Remove database ${DB_NAME}? (Default: No)" IDYES +2 IDNO +3
  Goto +3
  nsExec::ExecToLog '"$PgBin\\psql.exe" -h 127.0.0.1 -p $PgPort -U postgres -d postgres -v ON_ERROR_STOP=1 -q -X -c "DROP DATABASE IF EXISTS \"${DB_NAME}\";"'

  ; Remove install folder
  RMDir /r "$INSTDIR"

  ; Remove uninstall registry
  DeleteRegKey HKLM "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\${APP_NAME}"
  DeleteRegKey HKLM "Software\\${APP_NAME}"
SectionEnd
