@echo off
setlocal

cd /d "%~dp0"

powershell.exe -NoProfile -Command "$p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent()); if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 1 }"
if not "%ERRORLEVEL%"=="0" (
    powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b 0
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0stop_monitoring.ps1"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="0" (
    echo Wizard Monitoring stop completed.
) else (
    echo Wizard Monitoring stop failed. Exit code: %EXIT_CODE%
)

powershell.exe -NoProfile -Command "Start-Sleep -Seconds 3" >nul 2>nul
exit /b %EXIT_CODE%
