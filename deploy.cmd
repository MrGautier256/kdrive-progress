@echo off
rem Ferme kDrive Progress, le recompile (build.cmd), puis le relance depuis dist\.
rem Si la compilation echoue, l'ancienne version est relancee.
setlocal
set ROOT=%~dp0
set EXE=%ROOT%dist\KDriveProgress.exe

tasklist /FI "IMAGENAME eq KDriveProgress.exe" 2>nul | find /I "KDriveProgress.exe" >nul
if not errorlevel 1 (
    echo Fermeture de kDrive Progress...
    rem Fermeture propre : l'historique est enregistre
    if exist "%EXE%" start "" /wait "%EXE%" --quit
    rem Une version trop ancienne ne connait pas --quit : on force
    tasklist /FI "IMAGENAME eq KDriveProgress.exe" 2>nul | find /I "KDriveProgress.exe" >nul
    if not errorlevel 1 taskkill /F /IM KDriveProgress.exe >nul 2>&1
)

call "%ROOT%build.cmd"
set BUILD=%errorlevel%

if exist "%EXE%" (
    echo Relance de kDrive Progress...
    start "" "%EXE%"
)
if not "%BUILD%"=="0" (
    echo.
    echo ECHEC de la compilation : l'ancienne version a ete relancee.
    exit /b 1
)
