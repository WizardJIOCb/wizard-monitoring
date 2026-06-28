@echo off
setlocal

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "NVSMI=%WINDIR%\System32\nvidia-smi.exe"
if not exist "%NVSMI%" set "NVSMI=C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"

echo Restoring GPU power limit to default 285 W...
"%NVSMI%" -pl 285
echo.
"%NVSMI%" --query-gpu=name,power.draw,power.limit,power.default_limit,power.min_limit,power.max_limit,clocks.gr,clocks.mem,temperature.gpu --format=csv
echo.
pause
