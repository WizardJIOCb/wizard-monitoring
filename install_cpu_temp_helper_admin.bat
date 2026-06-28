@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File ""C:\Projects\wizard-monitoring\install_cpu_temp_helper_task.ps1""' -Verb RunAs"
pause
