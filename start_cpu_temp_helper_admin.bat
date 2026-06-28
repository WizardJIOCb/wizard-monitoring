@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath 'C:\Projects\wizard-monitoring\cpu_temp_helper\publish_pdh\cpu_temp_helper.exe' -ArgumentList '--watch --interval-ms 2000 --out C:\Projects\wizard-monitoring\cpu_temp_helper\cpu_temp.txt' -Verb RunAs -WindowStyle Hidden"
