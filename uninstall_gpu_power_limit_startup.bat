@echo off
setlocal

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

schtasks /Delete /TN "WizardGpuPowerLimit90" /F
pause
