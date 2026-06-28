using OpenHardwareMonitor.Hardware;

var computer = new Computer { IsCpuEnabled = true, IsMotherboardEnabled = true, IsRing0Enabled = true };
computer.Open(true);

for (var i = 0; i < 3; i++)
{
    foreach (var hardware in computer.Hardware)
    {
        Update(hardware);
    }
    Thread.Sleep(500);
}

foreach (var sensor in ComputerSensors(computer).Where(s => s.SensorType == SensorType.Temperature))
{
    Console.WriteLine($"{sensor.Hardware.HardwareType} | {sensor.Hardware.Name} | {sensor.Name} | {sensor.Identifier} | {sensor.Value}");
}

static void Update(IHardware hardware)
{
    hardware.Update();
    foreach (var sub in hardware.SubHardware)
    {
        Update(sub);
    }
}

static IEnumerable<ISensor> ComputerSensors(Computer computer)
{
    foreach (var hardware in computer.Hardware)
    {
        foreach (var sensor in HardwareSensors(hardware))
        {
            yield return sensor;
        }
    }
}

static IEnumerable<ISensor> HardwareSensors(IHardware hardware)
{
    foreach (var sensor in hardware.Sensors)
    {
        yield return sensor;
    }
    foreach (var sub in hardware.SubHardware)
    {
        foreach (var sensor in HardwareSensors(sub))
        {
            yield return sensor;
        }
    }
}
