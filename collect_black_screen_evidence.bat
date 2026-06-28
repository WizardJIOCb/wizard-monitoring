@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Projects\wizard-monitoring\collect_black_screen_evidence.ps1" -Hours 72
pause
