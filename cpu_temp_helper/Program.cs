using LibreHardwareMonitor.Hardware;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;

const string DefaultOutFile = @"C:\Projects\wizard-monitoring\cpu_temp_helper\cpu_temp.txt";

var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "--once";
var outFile = GetArgValue(args, "--out") ?? DefaultOutFile;

Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

var computer = new Computer
{
    IsCpuEnabled = true,
    IsMotherboardEnabled = true
};

computer.Open();
UpdateAll(computer);
Thread.Sleep(500);
UpdateAll(computer);

if (mode == "--list")
{
    UpdateAll(computer);
    foreach (var sensor in ReadAfterburnerSensors())
    {
        Console.WriteLine($"Afterburner | {sensor.SourceId:X} | {sensor.Gpu} | {sensor.Name} | {sensor.Value}");
    }

    foreach (var sensor in GetComputerSensors(computer).Where(s => s.SensorType == SensorType.Temperature))
    {
        Console.WriteLine($"{sensor.Hardware.HardwareType} | {sensor.Hardware.Name} | {sensor.Name} | {sensor.Identifier} | {sensor.Value}");
    }

    return;
}

if (mode == "--watch")
{
    var intervalMs = int.TryParse(GetArgValue(args, "--interval-ms"), out var parsed) ? parsed : 2000;
    var mutexName = Path.GetFileName(outFile).Equals("cpu_temp_acpi.txt", StringComparison.OrdinalIgnoreCase)
        ? "RodionCpuTempAcpiHelper"
        : "RodionCpuTempHelper";
    using var mutex = new Mutex(true, mutexName, out var createdNew);
    if (!createdNew)
    {
        return;
    }

    while (true)
    {
        WriteCpuTemperature(computer, outFile);
        Thread.Sleep(Math.Max(intervalMs, 500));
    }
}

WriteCpuTemperature(computer, outFile);
var current = File.Exists(outFile) ? File.ReadAllText(outFile).Trim() : "";
Console.WriteLine(current);

static string? GetArgValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static void WriteCpuTemperature(Computer computer, string outFile)
{
    UpdateAll(computer);
    var reading = ReadAfterburnerCpuTemperature() ?? ReadBestCpuTemperature(computer);
    var text = reading.HasValue
        ? reading.Value.ToString("0", CultureInfo.InvariantCulture)
        : "";

    File.WriteAllText(outFile, text);
    WriteDebugSensors(computer, Path.ChangeExtension(outFile, ".debug.txt"));
}

static float? ReadBestCpuTemperature(Computer computer)
{
    var best = GetComputerSensors(computer)
        .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)
        .Select(s => new
        {
            Sensor = s,
            Value = s.Value!.Value,
            Score = ScoreCpuTemperatureSensor(s)
        })
        .Where(x => x.Score >= 0 && x.Value is > 0 and < 120)
        .OrderByDescending(x => x.Score)
        .ThenByDescending(x => x.Value)
        .FirstOrDefault();

    return best?.Value;
}

static float? ReadAfterburnerCpuTemperature()
{
    var best = ReadAfterburnerSensors()
        .Where(s => s.SourceId == 0x80 && s.Value is > 0 and < 120)
        .Select(s => new
        {
            Sensor = s,
            Score = s.Gpu == uint.MaxValue ? 20 : 10
        })
        .OrderByDescending(x => x.Score)
        .ThenByDescending(x => x.Sensor.Value)
        .FirstOrDefault();

    return best?.Sensor.Value;
}

static IReadOnlyList<AfterburnerSensor> ReadAfterburnerSensors()
{
    const uint validSignature = 0x4D41484D;
    const uint deadSignature = 0x0000DEAD;
    const int minHeaderSize = 32;
    const int minEntrySize = 1324;
    const int srcNameOffset = 0;
    const int dataOffset = 1300;
    const int gpuOffset = 1316;
    const int sourceIdOffset = 1320;

    try
    {
        using var memory = OpenAfterburnerMemory();
        if (memory is null)
        {
            return Array.Empty<AfterburnerSensor>();
        }

        using var stream = memory.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var signature = reader.ReadUInt32();
        if (signature == deadSignature || signature != validSignature)
        {
            return Array.Empty<AfterburnerSensor>();
        }

        var version = reader.ReadUInt32();
        var headerSize = reader.ReadUInt32();
        var numEntries = reader.ReadUInt32();
        var entrySize = reader.ReadUInt32();
        var timestamp = reader.ReadInt32();

        if (version < 0x00020000 || headerSize < minHeaderSize || entrySize < minEntrySize || numEntries == 0 || numEntries > 1024)
        {
            return Array.Empty<AfterburnerSensor>();
        }

        var lastPollUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (DateTimeOffset.UtcNow - lastPollUtc > TimeSpan.FromSeconds(30))
        {
            return Array.Empty<AfterburnerSensor>();
        }

        var sensors = new List<AfterburnerSensor>();
        var entry = new byte[entrySize];

        for (var index = 0; index < numEntries; index++)
        {
            stream.Position = headerSize + index * entrySize;
            var read = stream.Read(entry, 0, entry.Length);
            if (read < minEntrySize)
            {
                continue;
            }

            var value = BitConverter.ToSingle(entry, dataOffset);
            if (float.IsNaN(value) || float.IsInfinity(value) || value >= float.MaxValue / 2)
            {
                continue;
            }

            sensors.Add(new AfterburnerSensor(
                ReadAnsiString(entry, srcNameOffset, 260),
                value,
                BitConverter.ToUInt32(entry, gpuOffset),
                BitConverter.ToUInt32(entry, sourceIdOffset)));
        }

        return sensors;
    }
    catch
    {
        return Array.Empty<AfterburnerSensor>();
    }
}

static MemoryMappedFile? OpenAfterburnerMemory()
{
    foreach (var name in new[] { "MAHMSharedMemory", @"Global\MAHMSharedMemory" })
    {
        try
        {
            return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        }
        catch
        {
        }
    }

    return null;
}

static string ReadAnsiString(byte[] bytes, int offset, int length)
{
    var end = offset;
    var max = Math.Min(bytes.Length, offset + length);
    while (end < max && bytes[end] != 0)
    {
        end++;
    }

    return Encoding.Default.GetString(bytes, offset, end - offset);
}

static void WriteDebugSensors(Computer computer, string debugFile)
{
    var afterburnerLines = ReadAfterburnerSensors()
        .Select(s => string.Join(" | ", new[]
        {
            DateTimeOffset.Now.ToString("o"),
            "Afterburner",
            "MSI Afterburner",
            s.Name,
            $"source=0x{s.SourceId:X};gpu={s.Gpu}",
            s.Value.ToString("0.0", CultureInfo.InvariantCulture),
            (s.SourceId == 0x80 ? "100" : "-1")
        }));

    var lines = afterburnerLines.Concat(GetComputerSensors(computer)
        .Where(s => s.SensorType == SensorType.Temperature)
        .Select(s => string.Join(" | ", new[]
        {
            DateTimeOffset.Now.ToString("o"),
            s.Hardware.HardwareType.ToString(),
            s.Hardware.Name,
            s.Name,
            s.Identifier.ToString(),
            s.Value.HasValue ? s.Value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "<null>",
            ScoreCpuTemperatureSensor(s).ToString(CultureInfo.InvariantCulture)
        })))
        .ToArray();

    File.WriteAllLines(debugFile, lines);
}

static int ScoreCpuTemperatureSensor(ISensor sensor)
{
    var name = sensor.Name.ToLowerInvariant();
    var id = sensor.Identifier.ToString().ToLowerInvariant();
    var hardwareType = sensor.Hardware.HardwareType;
    var score = -1;

    if (hardwareType == HardwareType.Cpu || id.Contains("/intelcpu/") || id.Contains("/amdcpu/"))
    {
        score = 20;
    }
    else if (id.Contains("/cpu/") || name.Contains("cpu"))
    {
        score = 10;
    }

    if (score < 0)
    {
        return -1;
    }

    if (name.Contains("package"))
    {
        score += 20;
    }
    else if (name.Contains("core max"))
    {
        score += 18;
    }
    else if (name.Contains("tctl") || name.Contains("tdie"))
    {
        score += 18;
    }
    else if (name.Contains("core"))
    {
        score += 8;
    }

    return score;
}

static IEnumerable<ISensor> GetHardwareSensors(IHardware hardware)
{
    foreach (var sensor in hardware.Sensors)
    {
        yield return sensor;
    }

    foreach (var subHardware in hardware.SubHardware)
    {
        foreach (var sensor in GetHardwareSensors(subHardware))
        {
            yield return sensor;
        }
    }
}

static IEnumerable<ISensor> GetComputerSensors(Computer computer)
{
    foreach (var hardware in computer.Hardware)
    {
        foreach (var sensor in GetHardwareSensors(hardware))
        {
            yield return sensor;
        }
    }
}

static void UpdateAll(Computer computer)
{
    foreach (var hardware in computer.Hardware)
    {
        Update(hardware);
    }
}

static void Update(IHardware hardware)
{
    hardware.Update();
    foreach (var subHardware in hardware.SubHardware)
    {
        Update(subHardware);
    }
}

internal sealed record AfterburnerSensor(string Name, float Value, uint Gpu, uint SourceId);
