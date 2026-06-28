@echo off
setlocal

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "PROJECT=%~dp0"
set "TASK=WizardGpuPowerLimit90"
set "PS=%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe"
set "SCRIPT=%PROJECT%set_gpu_power_limit_90_silent.ps1"

echo Installing startup task %TASK%...
schtasks /Create /TN "%TASK%" /SC ONLOGON /RL HIGHEST /F /TR "\"%PS%\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%SCRIPT%\""
if errorlevel 1 (
    echo Failed to create startup task.
    pause
    exit /b 1
)

echo Running task now...
schtasks /Run /TN "%TASK%"
echo.
echo Done. Check power_limit.log and nvidia-smi after a few seconds.
pause
