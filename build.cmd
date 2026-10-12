@echo off
rem Compile kDrive Progress et copie l'executable dans dist\
rem Prerequis : .NET SDK 6 ou plus recent (cible .NET Framework 4.8, fourni avec Windows 10 et 11).
setlocal
set ROOT=%~dp0
dotnet build "%ROOT%src\KDriveProgress" -c Release -nologo -v quiet || exit /b 1
if not exist "%ROOT%dist" mkdir "%ROOT%dist"
copy /y "%ROOT%src\KDriveProgress\bin\Release\net48\KDriveProgress.exe" "%ROOT%dist\" >nul || exit /b 1
echo.
echo Pret : %ROOT%dist\KDriveProgress.exe
