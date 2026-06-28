# Wizard Monitoring

Локальный набор утилит для мониторинга GPU/CPU/сети, применения GPU power limit и сбора телеметрии при диагностике черного экрана в Windows.

## Что внутри

- `WizardMonitorApp/` - WinForms tray-приложение и перетаскиваемый overlay с GPU/CPU, RAM, VRAM, CPU/GPU watts, load score и сетевым трафиком.
- `cpu_temp_helper/` - helper для чтения реальной температуры CPU через MSI Afterburner shared memory или LibreHardwareMonitorLib.
- `black_screen_watchdog.ps1` - фоновый логгер телеметрии.
- `start.bat` / `stop.bat` - запуск и остановка мониторинга с запросом прав администратора.
- `collect_black_screen_evidence.bat` - сбор отчета после аварии или жесткого выключения.
- `set_gpu_power_limit_*.bat` - сценарии применения NVIDIA GPU power limit.

## Требования

- Windows
- .NET 8 SDK
- PowerShell
- NVIDIA driver utilities (`nvidia-smi`) для GPU power limit и части диагностики
- MSI Afterburner как предпочтительный источник CPU temperature

## Сборка

```powershell
dotnet publish .\WizardMonitorApp\WizardMonitorApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish\WizardMonitorAppSingle_v4
dotnet publish .\cpu_temp_helper\cpu_temp_helper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\cpu_temp_helper\publish_pdh
```

Папки `publish/`, `bin/`, `obj/`, runtime-файлы, логи и CSV-телеметрия не хранятся в git.

## Запуск

```powershell
.\start.bat
```

`start.bat` поднимает права администратора, запускает мониторинг, применяет GPU power limit, стартует MSI Afterburner как источник CPU temperature, запускает виджет и watchdog.

Остановка:

```powershell
.\stop.bat
```

## CPU Temperature

Источники температуры CPU:

1. MSI Afterburner `MAHMSharedMemory`, если Afterburner запущен.
2. LibreHardwareMonitorLib как запасной источник, если он может прочитать CPU sensors.

Helper пишет текущее значение в `cpu_temp_helper\cpu_temp.txt`, а диагностические sensor values - в `cpu_temp_helper\cpu_temp.debug.txt`.

После изменений helper можно переустановить задачей администратора:

```powershell
.\install_cpu_temp_helper_admin.bat
```

## Диагностика

Watchdog пишет телеметрию в `black_screen_logs\telemetry_YYYY-MM-DD.csv`. После черного экрана и перезагрузки можно собрать отчет:

```powershell
.\collect_black_screen_evidence.bat
```

Скрипт создает `report_YYYYMMDD_HHMMSS` с событиями Windows, хвостом телеметрии и состоянием NVIDIA.
