# Incident: black screen / hard reboot on 2026-05-31

## Short conclusion

По текущим логам это не похоже на перегрев. Больше всего похоже на зависание/отвал NVIDIA GPU telemetry/driver/GPU под нагрузкой, после чего Windows перестала нормально отвечать и машина ушла в жесткое выключение/перезапуски без BSOD.

Это не 100% доказательство неисправности видеокарты: такой симптом могут давать драйвер NVIDIA, нестабильный разгон/undervolt, питание GPU/PCIe, БП, BIOS/chipset/PCIe, реже Windows power states. Но самый сильный след сейчас именно в сторону цепочки NVIDIA driver <-> GPU.

## What happened

Основной realtime лог:

- `C:\Projects\wizard-monitoring\publish\WizardMonitorAppSingle\log.log`

Watchdog лог:

- `C:\Projects\wizard-monitoring\black_screen_logs\telemetry_2026-05-31.csv`

Разрыв в мониторинге:

- последняя нормальная работа перед сбоем: `2026-05-31 23:10:10`
- последние строки перед обрывом: `2026-05-31 23:10:12` - `23:10:14`
- следующая запись после перезагрузки: примерно `2026-05-31 23:27:43` - `23:27:46`
- uptime сбросился: было около `116317 sec`, стало около `31-34 sec`

Это означает, что был не просто лаг приложения. Система реально ушла в выключение/перезапуск.

## Last good metrics before failure

Последние нормальные GPU-метрики около `2026-05-31 23:10:10`:

- GPU temp: `61 C`
- GPU load: `98%`
- GPU power: около `260 W`
- GPU fan: около `60%`
- GPU fan RPM: около `2197 RPM`
- GPU clock: около `2820 MHz`
- GPU memory clock: около `10701 MHz`
- GPU P-state: `P0`
- CPU load: около `12%`
- CPU temp: `28 C` по текущему датчику мониторинга
- RAM: около `48%`, примерно `31.5 GB / 65.3 GB`
- сеть была живая

За 5 минут перед сбоем максимумы были примерно:

- CPU max: `67%`
- CPU temp max: `28 C`
- RAM max: `48.7%`
- GPU temp max: `61-62 C`
- GPU load max: `98%`
- GPU power max: около `262 W`
- GPU fan max: около `2211 RPM`

Перегрева по этим данным не видно.

## Suspicious moment

В `23:10:12` CPU/RAM/сеть еще писались, но GPU-поля уже стали пустыми:

- `gpu_temp_c`: empty
- `gpu_util_pct`: empty
- `gpu_power_w`: empty
- `gpu_fan_rpm`: empty

В watchdog telemetry эта же точка отмечена как:

- `nv_status = nvidia-smi-no-output`

То есть Windows/процесс мониторинга еще успевал работать, но `nvidia-smi` уже перестал отдавать GPU telemetry. Это главный полезный след.

Важно: старый `WizardMonitorApp` в `log.log` тогда мог оставлять `nv_status=ok` при пустом output, потому что еще не было отдельной проверки пустого ответа. После инцидента это исправлено.

## Windows events

В системном журнале Windows после сбоя есть несколько событий:

- `2026-05-31 23:13:48` - `Kernel-Power 41`
- `2026-05-31 23:16:06` - `Kernel-Power 41`
- `2026-05-31 23:25:34` - `Kernel-Power 41`
- `2026-05-31 23:27:17` - `Kernel-Power 41`

Для этих событий:

- `BugcheckCode = 0`
- признаков обычного BSOD нет
- Windows видит это как: система перестала отвечать, произошел критический сбой или было неожиданно отключено питание

Также были события `EventLog 6008`:

- предыдущее завершение работы было неожиданным

Были попытки/события кнопки питания по timestamp примерно:

- `23:12:43`
- `23:14:10`
- `23:23:30`
- `23:25:56`

Это совпадает со сценарием "черный экран, жестко выключил, потом не сразу нормально загрузилось".

Не найдено в обычном System log за этот интервал:

- `nvlddmkm`
- `Display`
- `WHEA-Logger`
- `BugCheck`

`C:\Windows\MEMORY.DMP` не найден. К `C:\Windows\Minidump` не было доступа из текущего запуска PowerShell, поэтому наличие minidump отдельно не подтверждено.

## What was changed after the incident

В `WizardMonitorApp.exe` добавлено:

- realtime CSV `log.log` оставлен как основной поток всех метрик
- новый короткий event log:
  - `C:\Projects\wizard-monitoring\publish\WizardMonitorAppSingle\events.log`
- события:
  - `APP_STARTED`
  - `GPU_TELEMETRY_LOST`
  - `GPU_TELEMETRY_STILL_LOST`
  - `GPU_TELEMETRY_RESTORED`
  - `METRICS_READER_ERROR`
- `nvidia-smi` теперь имеет timeout `1500 ms`
- пустой ответ NVIDIA теперь фиксируется как `nvidia-smi-no-output`
- зависший `nvidia-smi` будет фиксироваться как `nvidia-smi-timeout`

При следующем сбое сначала смотреть последние строки:

- `events.log`
- `log.log`
- `C:\Projects\wizard-monitoring\black_screen_logs\telemetry_YYYY-MM-DD.csv`
- Windows Event Viewer -> System around failure time

## What to try fixing

### 1. Remove GPU instability variables

Сбросить все, что может менять поведение GPU:

- NVIDIA App auto tuning / разгон
- MSI Afterburner
- undervolt
- manual overclock GPU core
- manual overclock VRAM
- custom fan/power curve

Для проверки можно временно поставить:

- GPU power limit `90-95%`
- без разгона памяти
- без undervolt

Если черные экраны исчезнут, причина почти точно в нестабильности GPU/питания/частот под нагрузкой.

### 2. Clean reinstall NVIDIA driver

Лучший вариант:

- скачать свежий NVIDIA driver
- загрузиться в Safe Mode
- удалить старый драйвер через DDU
- поставить драйвер заново
- сначала без GeForce Experience/NVIDIA App tuning, только чистый драйвер

Если после чистого драйвера проблема уйдет, виноват был драйвер/профиль/настройка.

### 3. Check GPU power connection

Проверить физически:

- коннектор питания видеокарты вставлен до конца
- нет сильного изгиба кабеля прямо у видеокарты
- переходник/кабель не болтается
- если используются PCIe 8-pin линии, лучше отдельные линии от БП, а не один daisy-chain
- визуально нет подплавления/потемнения у коннектора

Симптом "под нагрузкой черный экран + потом не сразу стартует" часто бывает от питания/контакта/БП, даже если температуры нормальные.

### 4. Update platform drivers

Обновить:

- BIOS motherboard
- chipset driver
- Intel ME / AMD chipset components
- GPU driver

После обновления BIOS проверить, что настройки памяти/EXPO/XMP/PCIe не стали агрессивнее.

### 5. Disable problematic Windows power/GPU modes for test

Для диагностики можно временно выключить:

- Windows Fast Startup
- HAGS: Hardware-accelerated GPU scheduling
- PCI Express Link State Power Management

Также можно попробовать:

- NVIDIA Control Panel -> Power management mode -> Prefer maximum performance для проблемной игры
- либо наоборот ограничить FPS, чтобы снизить пики мощности

### 6. Stress tests carefully

Проверять отдельно:

- GPU load test 10-20 минут с логированием
- VRAM/memory test
- CPU test отдельно
- combined CPU+GPU load, если блок питания под подозрением

Следить в `log.log` за:

- GPU temp
- GPU power
- fan RPM
- моментом, когда `nvidia-smi` перестает отвечать
- появлением `GPU_TELEMETRY_LOST` в `events.log`

## Current working hypothesis

Самая вероятная цепочка:

1. Игра/3D нагрузка вывела GPU в `P0`, высокая частота/потребление около `260 W`.
2. Температуры были нормальные.
3. В `23:10:12` NVIDIA telemetry перестала отвечать (`nvidia-smi-no-output`).
4. Через некоторое время система ушла в hard reset/power loss без BSOD.
5. После этого были несколько неудачных/коротких стартов, что Windows зафиксировал как `Kernel-Power 41`.

Вероятные зоны ремонта по порядку:

1. драйвер NVIDIA / профиль разгона
2. нестабильность GPU core/VRAM/undervolt/power limit
3. питание видеокарты/кабель/БП
4. PCIe/chipset/BIOS/power management
5. аппаратная проблема GPU или БП, если повторяется на чистом драйвере и без разгона
