@echo off
setlocal

cd /d "%~dp0"

powershell.exe -NoProfile -Command "$p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent()); if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 1 }"
if not "%ERRORLEVEL%"=="0" (
    powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b 0
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start_monitoring.ps1"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="0" (
    echo Wizard Monitoring start check completed.
) else (
    echo Wizard Monitoring start check failed. Exit code: %EXIT_CODE%
)

echo.
echo Disk usage:
powershell.exe -NoProfile -Command "$names='C','D','E'; foreach($name in $names){ $drive=Get-PSDrive -Name $name -PSProvider FileSystem -ErrorAction SilentlyContinue; if($drive){ $used=$drive.Used/1GB; $total=($drive.Used+$drive.Free)/1GB; '{0}: {1:N1} GB used / {2:N1} GB total' -f $name,$used,$total } else { '{0}: unavailable' -f $name } }"

powershell.exe -NoProfile -Command "Start-Sleep -Seconds 3" >nul 2>nul
exit /b %EXIT_CODE%
