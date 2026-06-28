#Requires AutoHotkey v2.0
#SingleInstance Force
Persistent

; -----------------------------
; nvidia-smi: температура + fan %
; NVML: RPM, NVAPI fallback
; -----------------------------

nvsmi := A_WinDir "\System32\nvidia-smi.exe"
if !FileExist(nvsmi)
    nvsmi := "C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"

tmpFile := A_Temp "\gpu_temp_ahk.txt"
runtimeDir := "C:\Projects\wizard-monitoring\runtime"
pidFile := runtimeDir "\gpu_temp.pid"
clickThrough := false
netAdapterMatch := "Realtek 8812BU"
netPrev := Map()
cpuPrevIdle := 0
cpuPrevKernel := 0
cpuPrevUser := 0
cpuTempLast := ""
cpuTempLastTick := 0
cpuTempSource := ""
cpuTempNoSourceTick := 0
cpuTempFile := "C:\Projects\wizard-monitoring\cpu_temp_helper\cpu_temp.txt"

COLOR_TEXT := "FFFFFF"
COLOR_GPU := "EAF4FF"
COLOR_NET := "B8F7C7"
COLOR_CPU := "FFDDA6"
COLOR_WARN := "FFB347"
COLOR_DANGER := "FF4D4D"

try DirCreate(runtimeDir)
try {
    if FileExist(pidFile)
        FileDelete(pidFile)

    FileAppend(DllCall("GetCurrentProcessId"), pidFile, "UTF-8")
}
OnExit(CleanupPidFile)

; NVML globals
pNvmlGetNumFans := 0
pNvmlGetFanSpeedRPM := 0
nvmlDevice := 0
nvmlOk := InitNvml()

; NVAPI globals (fallback)
pNvQuery := 0
pNvGetTachReading := 0
nvGpuHandle := 0
nvapiOk := InitNvapi()

; -----------------------------
; GUI
; -----------------------------

g := Gui("+AlwaysOnTop -Caption +ToolWindow")
g.BackColor := "010101"

; Верхняя строка — температура
g.SetFont("s19 Bold", "Segoe UI")
tTemp := g.AddText("x0 y0 w118 h32 cWhite BackgroundTrans Center", "--°")
g.SetFont("s8", "Segoe UI")
tCpuTemp := g.AddText("x0 y32 w118 h15 cFFDDA6 BackgroundTrans Center", "cpu --°")

; Вторая строка — вентилятор в процентах
g.SetFont("s8 Bold", "Segoe UI")
tFanPct := g.AddText("x0 y47 w118 h15 cEAF4FF BackgroundTrans Center", "fan --%")

; Третья строка — реальные обороты
g.SetFont("s8 Bold", "Segoe UI")
tFanRpm := g.AddText("x0 y62 w118 h15 cEAF4FF BackgroundTrans Center", "rpm --")

g.SetFont("s8", "Segoe UI")
tNetUp := g.AddText("x0 y82 w118 h14 cB8F7C7 BackgroundTrans Center", "up --")
tNetDown := g.AddText("x0 y96 w118 h14 cB8F7C7 BackgroundTrans Center", "dn --")
tCpuLoad := g.AddText("x0 y112 w118 h14 cFFDDA6 BackgroundTrans Center", "cpu --%")
tGpuLoad := g.AddText("x0 y126 w118 h14 cFFDDA6 BackgroundTrans Center", "gpu --%")

g.Show("x100 y100 w118 h142 NoActivate")

; Прозрачный фон
WinSetTransColor("010101", "ahk_id " g.Hwnd)

OnMessage(0x0201, DragWindow)

SetTimer(UpdateGpuInfo, 1000)
UpdateGpuInfo()

; -----------------------------
; Обновление данных
; -----------------------------

UpdateGpuInfo() {
    global nvsmi, tmpFile, tTemp, tCpuTemp, tFanPct, tFanRpm, tNetUp, tNetDown, tCpuLoad, tGpuLoad
    global COLOR_TEXT, COLOR_GPU, COLOR_NET, COLOR_CPU, COLOR_DANGER

    temp := ""
    fanPct := ""
    gpuLoad := ""

    if FileExist(nvsmi) {
        try {
            cmd := A_ComSpec ' /c ""' nvsmi '" --query-gpu=temperature.gpu,fan.speed,utilization.gpu --format=csv,noheader,nounits >"' tmpFile '""'
            RunWait(cmd, , "Hide")

            out := FileExist(tmpFile) ? Trim(FileRead(tmpFile)) : ""

            ; Пример out: 40, 0
            parts := StrSplit(out, ",")

            temp := parts.Length >= 1 ? Trim(parts[1]) : ""
            fanPct := parts.Length >= 2 ? Trim(parts[2]) : ""
            gpuLoad := parts.Length >= 3 ? Trim(parts[3]) : ""
        }
    }

    if RegExMatch(temp, "\d+", &mTemp) {
        gpuTemp := mTemp[0] + 0
        SetMetric(tTemp, gpuTemp "°", TempColor(gpuTemp, 75, 85, COLOR_TEXT))
    } else {
        SetMetric(tTemp, "??°", COLOR_DANGER)
    }

    cpuTemp := GetCpuTemp()
    if (cpuTemp != "")
        SetMetric(tCpuTemp, "cpu " cpuTemp "°", TempColor(cpuTemp, 80, 90, COLOR_CPU))
    else
        SetMetric(tCpuTemp, "cpu --°", COLOR_CPU)

    if RegExMatch(fanPct, "\d+", &mFan) {
        fan := mFan[0] + 0
        SetMetric(tFanPct, "fan " fan "%", LoadColor(fan, 90, 98, COLOR_GPU))
    } else {
        SetMetric(tFanPct, "fan n/a", COLOR_GPU)
    }

    rpm := GetGpuRpm()

    if (rpm != "")
        SetMetric(tFanRpm, "rpm " rpm, COLOR_GPU)
    else
        SetMetric(tFanRpm, "rpm n/a", COLOR_GPU)

    net := GetNetworkRates()
    SetMetric(tNetUp, "up " net.up, COLOR_NET)
    SetMetric(tNetDown, "dn " net.down, COLOR_NET)

    cpuLoad := GetCpuUsage()
    if (cpuLoad != "")
        SetMetric(tCpuLoad, "cpu " cpuLoad "%", LoadColor(cpuLoad, 85, 95, COLOR_CPU))
    else
        SetMetric(tCpuLoad, "cpu --%", COLOR_CPU)

    if RegExMatch(gpuLoad, "\d+", &mGpuLoad) {
        gpu := mGpuLoad[0] + 0
        SetMetric(tGpuLoad, "gpu " gpu "%", LoadColor(gpu, 95, 99, COLOR_CPU))
    } else {
        SetMetric(tGpuLoad, "gpu --%", COLOR_CPU)
    }
}

SetMetric(ctrl, text, color) {
    ctrl.Text := text
    ctrl.SetFont("c" color)
}

TempColor(value, warn, danger, normalColor) {
    global COLOR_WARN, COLOR_DANGER

    if (value >= danger)
        return COLOR_DANGER

    if (value >= warn)
        return COLOR_WARN

    return normalColor
}

LoadColor(value, warn, danger, normalColor) {
    global COLOR_WARN, COLOR_DANGER

    if (value >= danger)
        return COLOR_DANGER

    if (value >= warn)
        return COLOR_WARN

    return normalColor
}

; -----------------------------
; CPU temperature
; -----------------------------

GetCpuTemp() {
    global cpuTempLast, cpuTempLastTick, cpuTempSource, cpuTempNoSourceTick

    if (cpuTempLastTick && A_TickCount - cpuTempLastTick < 2500)
        return cpuTempLast

    cpuTempLastTick := A_TickCount
    temp := GetCpuTempFromFile()
    if (temp != "") {
        cpuTempLast := temp
        return temp
    }

    if (cpuTempSource = "LibreHardwareMonitor") {
        temp := GetCpuTempFromHardwareMonitor("root\LibreHardwareMonitor")
    } else if (cpuTempSource = "OpenHardwareMonitor") {
        temp := GetCpuTempFromHardwareMonitor("root\OpenHardwareMonitor")
    } else if (!cpuTempNoSourceTick || A_TickCount - cpuTempNoSourceTick >= 15000) {
        temp := GetCpuTempFromHardwareMonitor("root\LibreHardwareMonitor")
        if (temp != "")
            cpuTempSource := "LibreHardwareMonitor"

        if (temp = "") {
            temp := GetCpuTempFromHardwareMonitor("root\OpenHardwareMonitor")
            if (temp != "")
                cpuTempSource := "OpenHardwareMonitor"
        }

        if (temp = "")
            cpuTempNoSourceTick := A_TickCount
    }

    if (temp = "" && cpuTempSource != "") {
        cpuTempSource := ""
        cpuTempNoSourceTick := A_TickCount
    }

    cpuTempLast := temp
    return temp
}

GetCpuTempFromFile() {
    global cpuTempFile

    return ReadCpuTempFile(cpuTempFile)
}

ReadCpuTempFile(path) {
    if !FileExist(path)
        return ""

    try {
        modified := FileGetTime(path, "M")
        if (DateDiff(A_Now, modified, "Seconds") > 10)
            return ""

        value := Trim(FileRead(path))
        if RegExMatch(value, "^\d{1,3}(?:[.,]\d+)?$", &m) {
            temp := Round(StrReplace(m[0], ",", ".") + 0)
            if (temp > 0 && temp < 120)
                return temp
        }
    }

    return ""
}

GetCpuTempFromHardwareMonitor(namespace) {
    try {
        service := ComObjGet("winmgmts:\\.\" namespace)
        sensors := service.ExecQuery("SELECT * FROM Sensor WHERE SensorType='Temperature'")
    } catch {
        return ""
    }

    bestTemp := ""
    bestScore := -1

    for sensor in sensors {
        try {
            name := sensor.Name
            identifier := sensor.Identifier
            value := sensor.Value
        } catch {
            continue
        }

        if (value = "")
            continue

        lowerName := StrLower(name)
        lowerId := StrLower(identifier)
        score := -1

        if InStr(lowerId, "/intelcpu/")
            score := 10
        else if InStr(lowerId, "/cpu/")
            score := 8
        else if InStr(lowerName, "cpu")
            score := 4

        if (score < 0)
            continue

        if InStr(lowerName, "package")
            score += 6
        else if InStr(lowerName, "core max")
            score += 5
        else if InStr(lowerName, "tctl") || InStr(lowerName, "tdie")
            score += 5

        if (score > bestScore) {
            bestScore := score
            bestTemp := Round(value)
        }
    }

    return bestTemp
}

; -----------------------------
; CPU usage
; -----------------------------

GetCpuUsage() {
    global cpuPrevIdle, cpuPrevKernel, cpuPrevUser

    idleBuf := Buffer(8, 0)
    kernelBuf := Buffer(8, 0)
    userBuf := Buffer(8, 0)

    if !DllCall("GetSystemTimes", "Ptr", idleBuf, "Ptr", kernelBuf, "Ptr", userBuf)
        return ""

    idle := NumGet(idleBuf, 0, "UInt64")
    kernel := NumGet(kernelBuf, 0, "UInt64")
    user := NumGet(userBuf, 0, "UInt64")

    if (!cpuPrevIdle && !cpuPrevKernel && !cpuPrevUser) {
        cpuPrevIdle := idle
        cpuPrevKernel := kernel
        cpuPrevUser := user
        return ""
    }

    idleDelta := idle - cpuPrevIdle
    kernelDelta := kernel - cpuPrevKernel
    userDelta := user - cpuPrevUser
    total := kernelDelta + userDelta

    cpuPrevIdle := idle
    cpuPrevKernel := kernel
    cpuPrevUser := user

    if (total <= 0)
        return ""

    usage := Round((total - idleDelta) * 100 / total)

    if (usage < 0)
        usage := 0
    else if (usage > 100)
        usage := 100

    return usage
}

; -----------------------------
; Network throughput
; -----------------------------

GetNetworkRates() {
    global netAdapterMatch, netPrev

    sizeBuf := Buffer(4, 0)
    DllCall("iphlpapi\GetIfTable", "Ptr", 0, "Ptr", sizeBuf, "Int", 0, "UInt")
    size := NumGet(sizeBuf, 0, "UInt")

    if (size <= 4)
        return {up: "--", down: "--"}

    table := Buffer(size, 0)
    status := DllCall("iphlpapi\GetIfTable", "Ptr", table, "Ptr", sizeBuf, "Int", 0, "UInt")

    if (status != 0)
        return {up: "--", down: "--"}

    rows := []
    count := NumGet(table, 0, "UInt")
    rowSize := 860

    Loop count {
        rowOffset := 4 + (A_Index - 1) * rowSize
        if (rowOffset + rowSize > table.Size)
            break

        descLen := NumGet(table, rowOffset + 600, "UInt")
        if (descLen > 255)
            descLen := 255

        rows.Push({
            idx: NumGet(table, rowOffset + 512, "UInt"),
            type: NumGet(table, rowOffset + 516, "UInt"),
            oper: NumGet(table, rowOffset + 544, "UInt"),
            rx: NumGet(table, rowOffset + 552, "UInt"),
            tx: NumGet(table, rowOffset + 576, "UInt"),
            desc: descLen ? StrGet(table.Ptr + rowOffset + 604, descLen, "CP0") : ""
        })
    }

    now := A_TickCount
    forcedRows := []
    autoRows := []

    for row in rows {
        if (row.oper != 5)
            continue

        if (netAdapterMatch != "" && InStr(StrLower(row.desc), StrLower(netAdapterMatch))) {
            forcedRows.Push(row)
            continue
        }

        if ((row.type = 6 || row.type = 71) && !IsIgnoredNetAdapter(row.desc))
            autoRows.Push(row)
    }

    candidates := forcedRows.Length ? forcedRows : autoRows
    if (!candidates.Length)
        candidates := rows

    haveRate := false
    bestScore := -1
    bestTx := 0
    bestRx := 0

    for row in candidates {
        if !netPrev.Has(row.idx)
            continue

        prev := netPrev[row.idx]
        elapsed := (now - prev.tick) / 1000

        if (elapsed <= 0)
            continue

        rxRate := CounterDelta32(row.rx, prev.rx) / elapsed
        txRate := CounterDelta32(row.tx, prev.tx) / elapsed
        score := rxRate + txRate

        if (score > bestScore) {
            bestScore := score
            bestRx := rxRate
            bestTx := txRate
            haveRate := true
        }
    }

    for row in rows
        netPrev[row.idx] := {rx: row.rx, tx: row.tx, tick: now}

    if !haveRate
        return {up: "--", down: "--"}

    return {up: FormatNetRate(bestTx), down: FormatNetRate(bestRx)}
}

CounterDelta32(current, previous) {
    return current >= previous ? current - previous : 0x100000000 - previous + current
}

IsIgnoredNetAdapter(desc) {
    lower := StrLower(desc)
    ignored := ["virtual", "vmware", "hyper-v", "vethernet", "tap", "tunnel", "loopback", "bluetooth", "openvpn", "wintun", "wireguard", "docker"]

    for word in ignored {
        if InStr(lower, word)
            return true
    }

    return false
}

FormatNetRate(bytesPerSec) {
    bits := bytesPerSec * 8

    if (bits >= 1000000000)
        return Format("{:.1f}G", bits / 1000000000)

    if (bits >= 1000000)
        return Format("{:.1f}M", bits / 1000000)

    if (bits >= 1000) {
        kbits := bits / 1000
        return kbits < 100 ? Format("{:.1f}K", kbits) : Format("{:.0f}K", kbits)
    }

    return Format("{:.0f}b", bits)
}

; -----------------------------
; NVML: initialization + RPM
; -----------------------------

InitNvml() {
    global pNvmlGetNumFans, pNvmlGetFanSpeedRPM, nvmlDevice

    hNvml := DllCall("LoadLibrary", "Str", "nvml.dll", "Ptr")
    if !hNvml {
        nvmlPath := A_WinDir "\System32\nvml.dll"
        if FileExist(nvmlPath)
            hNvml := DllCall("LoadLibrary", "Str", nvmlPath, "Ptr")
    }

    if !hNvml
        return false

    pNvmlInit := DllCall("GetProcAddress", "Ptr", hNvml, "AStr", "nvmlInit_v2", "Ptr")
    pNvmlGetHandle := DllCall("GetProcAddress", "Ptr", hNvml, "AStr", "nvmlDeviceGetHandleByIndex_v2", "Ptr")
    pNvmlGetNumFans := DllCall("GetProcAddress", "Ptr", hNvml, "AStr", "nvmlDeviceGetNumFans", "Ptr")
    pNvmlGetFanSpeedRPM := DllCall("GetProcAddress", "Ptr", hNvml, "AStr", "nvmlDeviceGetFanSpeedRPM", "Ptr")

    if (!pNvmlInit || !pNvmlGetHandle || !pNvmlGetFanSpeedRPM)
        return false

    if (DllCall(pNvmlInit, "Cdecl Int") != 0)
        return false

    deviceBuf := Buffer(A_PtrSize, 0)

    if (DllCall(pNvmlGetHandle, "UInt", 0, "Ptr", deviceBuf, "Cdecl Int") != 0)
        return false

    nvmlDevice := NumGet(deviceBuf, 0, "Ptr")
    return nvmlDevice != 0
}

GetGpuRpm() {
    rpm := GetGpuRpmNvml()
    if (rpm != "")
        return rpm

    return GetGpuRpmNvapi()
}

GetGpuRpmNvml() {
    global pNvmlGetNumFans, pNvmlGetFanSpeedRPM, nvmlDevice

    if (!pNvmlGetFanSpeedRPM || !nvmlDevice)
        return ""

    fanCount := 1

    if pNvmlGetNumFans {
        fanCountBuf := Buffer(4, 0)
        if (DllCall(pNvmlGetNumFans, "Ptr", nvmlDevice, "Ptr", fanCountBuf, "Cdecl Int") = 0) {
            fanCount := NumGet(fanCountBuf, 0, "UInt")
            if (fanCount < 1)
                fanCount := 1
        }
    }

    total := 0
    readings := 0

    Loop fanCount {
        fanInfo := Buffer(12, 0)
        NumPut("UInt", 0x0100000C, fanInfo, 0) ; nvmlFanSpeedInfo_v1
        NumPut("UInt", A_Index - 1, fanInfo, 4)

        status := DllCall(pNvmlGetFanSpeedRPM, "Ptr", nvmlDevice, "Ptr", fanInfo, "Cdecl Int")
        if (status = 0) {
            total += NumGet(fanInfo, 8, "UInt")
            readings += 1
        }
    }

    return readings ? Round(total / readings) : ""
}

; -----------------------------
; NVAPI: инициализация
; -----------------------------

InitNvapi() {
    global pNvQuery, pNvGetTachReading, nvGpuHandle

    dllName := A_PtrSize = 8 ? "nvapi64.dll" : "nvapi.dll"

    hNvapi := DllCall("LoadLibrary", "Str", dllName, "Ptr")
    if !hNvapi
        return false

    pNvQuery := DllCall("GetProcAddress", "Ptr", hNvapi, "AStr", "nvapi_QueryInterface", "Ptr")
    if !pNvQuery
        return false

    pNvInit := NvQuery(0x0150E828)
    pNvEnumPhysicalGPUs := NvQuery(0xE5AC921F)
    pNvGetTachReading := NvQuery(0x5F608315)

    if (!pNvInit || !pNvEnumPhysicalGPUs || !pNvGetTachReading)
        return false

    ; NvAPI_Initialize()
    if (DllCall(pNvInit, "Cdecl Int") != 0)
        return false

    ; NvAPI_EnumPhysicalGPUs()
    gpuHandles := Buffer(A_PtrSize * 64, 0)
    gpuCountBuf := Buffer(4, 0)

    if (DllCall(pNvEnumPhysicalGPUs, "Ptr", gpuHandles, "Ptr", gpuCountBuf, "Cdecl Int") != 0)
        return false

    gpuCount := NumGet(gpuCountBuf, 0, "UInt")
    if (gpuCount < 1)
        return false

    nvGpuHandle := NumGet(gpuHandles, 0, "Ptr")
    return true
}

NvQuery(id) {
    global pNvQuery

    if !pNvQuery
        return 0

    return DllCall(pNvQuery, "UInt", id, "Cdecl Ptr")
}

; -----------------------------
; NVAPI: чтение RPM
; -----------------------------

GetGpuRpmNvapi() {
    global pNvGetTachReading, nvGpuHandle

    if (!pNvGetTachReading || !nvGpuHandle)
        return ""

    rpmBuf := Buffer(4, 0)

    ; NvAPI_GPU_GetTachReading(hPhysicalGPU, &Value)
    status := DllCall(pNvGetTachReading, "Ptr", nvGpuHandle, "Ptr", rpmBuf, "Cdecl Int")

    if (status = 0)
        return NumGet(rpmBuf, 0, "UInt")

    return ""
}

; -----------------------------
; Перетаскивание мышкой
; -----------------------------

DragWindow(wParam, lParam, msg, hwnd) {
    global g, clickThrough

    if clickThrough
        return

    root := DllCall("GetAncestor", "Ptr", hwnd, "UInt", 2, "Ptr")

    if (hwnd = g.Hwnd || root = g.Hwnd) {
        DllCall("ReleaseCapture")
        PostMessage(0xA1, 2, 0, , "ahk_id " g.Hwnd)
    }
}

; Ctrl + Alt + T — включить/выключить клики сквозь виджет
^!t:: {
    global clickThrough, g

    clickThrough := !clickThrough
    WinSetExStyle((clickThrough ? "+" : "-") "0x20", "ahk_id " g.Hwnd)
}

; Ctrl + Alt + Q — закрыть
^!q::ExitApp

CleanupPidFile(*) {
    global pidFile

    try FileDelete(pidFile)
}
