using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WizardMonitorApp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "WizardMonitoringTrayApp", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        var openSettingsOnStart = args.Any(x => string.Equals(x, "--settings", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MonitorApplicationContext(openSettingsOnStart));
    }
}

internal sealed class MonitorApplicationContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly MetricsReader _reader;
    private readonly OverlayForm _overlay;
    private readonly NotifyIcon _trayIcon;
    private readonly MetricsLogger _logger;
    private readonly EventLogger _eventLogger;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _weatherTimer;
    private readonly System.Windows.Forms.Timer _zOrderTimer;
    private readonly WeatherReader _weatherReader = new();
    private CancellationTokenSource? _weatherCancellation;
    private int _weatherRequestVersion;
    private WeatherSnapshot _lastWeather = WeatherSnapshot.Empty;
    private bool? _gpuTelemetryAvailable;
    private int _gpuTelemetryFailureCount;
    private DateTimeOffset? _gpuTelemetryLostAt;

    public MonitorApplicationContext(bool openSettingsOnStart = false)
    {
        _settings = AppSettings.Load();
        _reader = new MetricsReader(_settings);
        _logger = new MetricsLogger(Path.Combine(AppContext.BaseDirectory, "log.log"));
        _eventLogger = new EventLogger(Path.Combine(AppContext.BaseDirectory, "events.log"));
        _eventLogger.Write("APP_STARTED", MetricsSnapshot.Empty, "ok", "WizardMonitorApp started");
        _overlay = new OverlayForm(_settings);
        _overlay.Show();

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Wizard Monitoring",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ToggleOverlay();

        _timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(500, _settings.UpdateIntervalMs)
        };
        _timer.Tick += (_, _) => UpdateMetrics();
        _timer.Start();

        _weatherTimer = new System.Windows.Forms.Timer();
        _weatherTimer.Tick += async (_, _) => await UpdateWeatherAsync();
        ConfigureWeatherTimer(refreshNow: true);

        _zOrderTimer = new System.Windows.Forms.Timer
        {
            Interval = 500
        };
        _zOrderTimer.Tick += (_, _) => _overlay.KeepAboveTaskbar();
        _zOrderTimer.Start();

        UpdateMetrics();

        if (openSettingsOnStart)
        {
            _overlay.BeginInvoke(new Action(OpenSettings));
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();

        var showHide = new ToolStripMenuItem("Show / Hide", null, (_, _) => ToggleOverlay());
        var clickThrough = new ToolStripMenuItem("Click through")
        {
            Checked = _settings.ClickThrough,
            CheckOnClick = true
        };
        clickThrough.CheckedChanged += (_, _) =>
        {
            _settings.ClickThrough = clickThrough.Checked;
            _overlay.SetClickThrough(_settings.ClickThrough);
            _settings.Save();
        };

        var settings = new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings());
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());

        menu.Items.Add(showHide);
        menu.Items.Add(clickThrough);
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    private void ToggleOverlay()
    {
        if (_overlay.Visible)
        {
            _overlay.Hide();
        }
        else
        {
            _overlay.Show();
            _overlay.KeepAboveTaskbar();
        }
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog(_overlay) != DialogResult.OK)
        {
            return;
        }

        _settings.Save();
        _timer.Interval = Math.Max(500, _settings.UpdateIntervalMs);
        _overlay.ApplySettings(_settings);
        _reader.ApplySettings(_settings);
        ConfigureWeatherTimer(refreshNow: true);
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.ContextMenuStrip = BuildTrayMenu();
        UpdateMetrics();
    }

    private void ConfigureWeatherTimer(bool refreshNow)
    {
        _weatherTimer.Stop();
        var intervalMinutes = Math.Clamp(_settings.WeatherUpdateIntervalMinutes, 1, 1440);
        _weatherTimer.Interval = checked(intervalMinutes * 60_000);

        if (!_settings.ShowWeather)
        {
            _weatherCancellation?.Cancel();
            _overlay.UpdateWeather(WeatherSnapshot.Empty);
            return;
        }

        _weatherTimer.Start();
        if (refreshNow)
        {
            _ = UpdateWeatherAsync();
        }
    }

    private async Task UpdateWeatherAsync()
    {
        if (!_settings.ShowWeather)
        {
            return;
        }

        var city = _settings.WeatherCity.Trim();
        if (city.Length == 0)
        {
            _overlay.UpdateWeather(new WeatherSnapshot { Error = "city-required" });
            return;
        }

        var requestVersion = Interlocked.Increment(ref _weatherRequestVersion);
        _weatherCancellation?.Cancel();
        _weatherCancellation?.Dispose();
        _weatherCancellation = new CancellationTokenSource();

        try
        {
            var snapshot = await _weatherReader.ReadAsync(city, _weatherCancellation.Token);
            if (requestVersion != _weatherRequestVersion || !_settings.ShowWeather)
            {
                return;
            }

            if (snapshot.TemperatureC.HasValue)
            {
                _lastWeather = snapshot;
                _overlay.UpdateWeather(snapshot);
                _eventLogger.Write(
                    "WEATHER_UPDATED",
                    MetricsSnapshot.Empty,
                    "ok",
                    $"city={snapshot.LocationName ?? city}; temp_c={snapshot.TemperatureC.Value.ToString("0.0", CultureInfo.InvariantCulture)}; weather_code={snapshot.WeatherCode?.ToString(CultureInfo.InvariantCulture) ?? ""}");
            }
            else if (_lastWeather.TemperatureC.HasValue &&
                     string.Equals(_lastWeather.Query, city, StringComparison.OrdinalIgnoreCase))
            {
                _overlay.UpdateWeather(_lastWeather with { IsStale = true });
                _eventLogger.Write("WEATHER_UPDATE_FAILED", MetricsSnapshot.Empty, snapshot.Error, $"city={city}; showing_stale=true");
            }
            else
            {
                _overlay.UpdateWeather(snapshot);
                _eventLogger.Write("WEATHER_UPDATE_FAILED", MetricsSnapshot.Empty, snapshot.Error, $"city={city}; showing_stale=false");
            }
        }
        catch (OperationCanceledException) when (_weatherCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (requestVersion != _weatherRequestVersion)
            {
                return;
            }

            if (_lastWeather.TemperatureC.HasValue &&
                string.Equals(_lastWeather.Query, city, StringComparison.OrdinalIgnoreCase))
            {
                _overlay.UpdateWeather(_lastWeather with { IsStale = true });
                _eventLogger.Write("WEATHER_UPDATE_FAILED", MetricsSnapshot.Empty, "request-failed", $"city={city}; showing_stale=true");
            }
            else
            {
                _overlay.UpdateWeather(new WeatherSnapshot { Query = city, Error = "request-failed" });
                _eventLogger.Write("WEATHER_UPDATE_FAILED", MetricsSnapshot.Empty, "request-failed", $"city={city}; showing_stale=false");
            }
        }
    }

    private void UpdateMetrics()
    {
        try
        {
            var snapshot = _reader.Read();
            _overlay.UpdateSnapshot(snapshot);
            _logger.Write(snapshot);
            TrackGpuTelemetry(snapshot);
            _overlay.KeepAboveTaskbar();
        }
        catch (Exception ex)
        {
            var snapshot = MetricsSnapshot.Empty with
            {
                NvStatus = "reader-error",
                NvStatusDetail = ex.GetType().Name + ": " + ex.Message
            };
            _overlay.UpdateSnapshot(snapshot);
            _logger.Write(snapshot);
            _eventLogger.Write("METRICS_READER_ERROR", snapshot, snapshot.NvStatus, snapshot.NvStatusDetail);
            TrackGpuTelemetry(snapshot);
            _overlay.KeepAboveTaskbar();
        }
    }

    private void TrackGpuTelemetry(MetricsSnapshot snapshot)
    {
        var hasCoreGpuValues = snapshot.GpuTempC.HasValue || snapshot.GpuPct.HasValue || snapshot.GpuPowerW.HasValue;
        var available = string.Equals(snapshot.NvStatus, "ok", StringComparison.OrdinalIgnoreCase) && hasCoreGpuValues;

        if (available)
        {
            if (_gpuTelemetryAvailable == false)
            {
                var duration = _gpuTelemetryLostAt.HasValue
                    ? Math.Round((DateTimeOffset.Now - _gpuTelemetryLostAt.Value).TotalSeconds, 1).ToString("0.0", CultureInfo.InvariantCulture)
                    : "unknown";
                _eventLogger.Write("GPU_TELEMETRY_RESTORED", snapshot, snapshot.NvStatus, $"lost_duration_sec={duration}; previous_failures={_gpuTelemetryFailureCount}");
            }

            _gpuTelemetryAvailable = true;
            _gpuTelemetryFailureCount = 0;
            _gpuTelemetryLostAt = null;
            return;
        }

        _gpuTelemetryFailureCount++;
        if (_gpuTelemetryAvailable != false)
        {
            _gpuTelemetryLostAt = DateTimeOffset.Now;
            _eventLogger.Write("GPU_TELEMETRY_LOST", snapshot, snapshot.NvStatus, BuildGpuTelemetryDetail(snapshot, hasCoreGpuValues));
        }
        else if (_gpuTelemetryFailureCount is 5 or 30 || _gpuTelemetryFailureCount % 60 == 0)
        {
            _eventLogger.Write("GPU_TELEMETRY_STILL_LOST", snapshot, snapshot.NvStatus, BuildGpuTelemetryDetail(snapshot, hasCoreGpuValues));
        }

        _gpuTelemetryAvailable = false;
    }

    private string BuildGpuTelemetryDetail(MetricsSnapshot snapshot, bool hasCoreGpuValues)
    {
        var parts = new[]
        {
            "has_core_gpu_values=" + (hasCoreGpuValues ? "true" : "false"),
            "consecutive_failures=" + _gpuTelemetryFailureCount.ToString(CultureInfo.InvariantCulture),
            "nv_detail=" + (snapshot.NvStatusDetail ?? "")
        };

        return string.Join("; ", parts);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _weatherTimer.Stop();
        _zOrderTimer.Stop();
        _weatherCancellation?.Cancel();
        _weatherCancellation?.Dispose();
        _settings.Location = _overlay.Location;
        _settings.Save();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _overlay.Close();
        _reader.Dispose();
        _weatherReader.Dispose();
        _logger.Dispose();
        _eventLogger.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class OverlayForm : Form
{
    private readonly Label _gpuTemp;
    private readonly Label _cpuTemp;
    private readonly Label _fanPct;
    private readonly Label _fanRpm;
    private readonly Label _netUp;
    private readonly Label _netDown;
    private readonly Label _netSentTotal;
    private readonly Label _netReceivedTotal;
    private readonly Label _cpuLoad;
    private readonly Label _gpuLoad;
    private readonly Label _ram;
    private readonly Label _vram;
    private readonly Label _diskC;
    private readonly Label _diskD;
    private readonly Label _diskE;
    private readonly Label _diskReadRate;
    private readonly Label _diskWriteRate;
    private readonly Label _power;
    private readonly Label _score;
    private readonly Label _weather;

    private AppSettings _settings;

    public OverlayForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Wizard Monitoring Overlay";
        StartPosition = FormStartPosition.Manual;
        Location = settings.Location;
        Size = new Size(118, 1);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = settings.TopMost;
        BackColor = Color.FromArgb(1, 1, 1);
        TransparencyKey = BackColor;
        DoubleBuffered = true;

        _gpuTemp = AddMetric(new Rectangle(0, 0, 118, 32), new Font("Segoe UI", 19, FontStyle.Bold), Color.White, "--°");
        _cpuTemp = AddMetric(new Rectangle(0, 32, 118, 15), new Font("Segoe UI", 8), Palette.Cpu, "cpu --°");
        _fanPct = AddMetric(new Rectangle(0, 47, 118, 15), new Font("Segoe UI", 8, FontStyle.Bold), Palette.Gpu, "fan --%");
        _fanRpm = AddMetric(new Rectangle(0, 62, 118, 15), new Font("Segoe UI", 8, FontStyle.Bold), Palette.Gpu, "rpm --");
        _cpuLoad = AddMetric(new Rectangle(0, 82, 118, 14), new Font("Segoe UI", 8), Palette.Cpu, "cpu --%");
        _gpuLoad = AddMetric(new Rectangle(0, 96, 118, 14), new Font("Segoe UI", 8), Palette.Cpu, "gpu --%");
        _ram = AddMetric(new Rectangle(0, 110, 118, 14), new Font("Segoe UI", 8), Palette.Ram, "ram --/--G");
        _vram = AddMetric(new Rectangle(0, 124, 118, 14), new Font("Segoe UI", 8), Palette.Vram, "vram --/--G");
        _diskC = AddMetric(new Rectangle(0, 138, 118, 14), new Font("Segoe UI", 8), Palette.Disk, "C --/--G");
        _diskD = AddMetric(new Rectangle(0, 152, 118, 14), new Font("Segoe UI", 8), Palette.Disk, "D --/--G");
        _diskE = AddMetric(new Rectangle(0, 166, 118, 14), new Font("Segoe UI", 8), Palette.Disk, "E --/--G");
        _diskReadRate = AddMetric(new Rectangle(0, 180, 118, 14), new Font("Segoe UI", 8), Palette.Disk, "rd --");
        _diskWriteRate = AddMetric(new Rectangle(0, 194, 118, 14), new Font("Segoe UI", 8), Palette.Disk, "wr --");
        _power = AddMetric(new Rectangle(0, 208, 118, 14), new Font("Segoe UI", 8), Palette.Power, "pwr --/--W");
        _netUp = AddMetric(new Rectangle(0, 222, 118, 14), new Font("Segoe UI", 8), Palette.Net, "up --");
        _netDown = AddMetric(new Rectangle(0, 236, 118, 14), new Font("Segoe UI", 8), Palette.Net, "dn --");
        _netSentTotal = AddMetric(new Rectangle(0, 250, 118, 14), new Font("Segoe UI", 8), Palette.NetTotal, "tx --");
        _netReceivedTotal = AddMetric(new Rectangle(0, 264, 118, 14), new Font("Segoe UI", 8), Palette.NetTotal, "rx --");
        _score = AddMetric(new Rectangle(0, 278, 118, 14), new Font("Segoe UI", 8, FontStyle.Bold), Palette.Score, "cap --");
        _weather = AddMetric(new Rectangle(0, 292, 118, 30), new Font("Segoe UI", 7.5f), Palette.Weather, "weather --");

        SetClickThrough(settings.ClickThrough);
        ApplyMetricVisibility();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExNoActivate = 0x08000000;

            var cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow | wsExNoActivate;
            return cp;
        }
    }

    private Label AddMetric(Rectangle bounds, Font font, Color color, string text)
    {
        var label = new Label
        {
            Bounds = bounds,
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false
        };
        label.MouseDown += BeginDrag;
        Controls.Add(label);
        return label;
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        TopMost = settings.TopMost;
        SetClickThrough(settings.ClickThrough);
        ApplyMetricVisibility();
        KeepAboveTaskbar();
    }

    public void UpdateSnapshot(MetricsSnapshot snapshot)
    {
        SetMetric(_gpuTemp, FormatTemp(snapshot.GpuTempC), TempColor(snapshot.GpuTempC, 75, 85, Color.White));
        SetMetric(_cpuTemp, "cpu " + FormatTemp(snapshot.CpuTempC), TempColor(snapshot.CpuTempC, 80, 90, Palette.Cpu));
        SetMetric(_fanPct, snapshot.GpuFanPct.HasValue ? $"fan {snapshot.GpuFanPct:0}%" : "fan n/a", LoadColor(snapshot.GpuFanPct, 90, 98, Palette.Gpu));
        SetMetric(_fanRpm, snapshot.GpuFanRpm.HasValue ? $"rpm {snapshot.GpuFanRpm:0}" : "rpm n/a", Palette.Gpu);
        SetMetric(_cpuLoad, snapshot.CpuPct.HasValue ? $"cpu {snapshot.CpuPct:0}%" : "cpu --%", LoadColor(snapshot.CpuPct, 85, 95, Palette.Cpu));
        SetMetric(_gpuLoad, snapshot.GpuPct.HasValue ? $"gpu {snapshot.GpuPct:0}%" : "gpu --%", LoadColor(snapshot.GpuPct, 95, 99, Palette.Cpu));
        SetMetric(_ram, FormatRam(snapshot.RamUsedMb, snapshot.RamTotalMb), LoadColor(snapshot.RamUsedPct, 85, 95, Palette.Ram));
        SetMetric(_vram, FormatVram(snapshot.GpuMemUsedMb, snapshot.GpuMemTotalMb), LoadColor(snapshot.GpuMemPct, 85, 95, Palette.Vram));
        SetMetric(_diskC, FormatDisk("C", snapshot.DiskC), DiskColor(snapshot.DiskC, _settings.PlainDiskColor));
        SetMetric(_diskD, FormatDisk("D", snapshot.DiskD), DiskColor(snapshot.DiskD, _settings.PlainDiskColor));
        SetMetric(_diskE, FormatDisk("E", snapshot.DiskE), DiskColor(snapshot.DiskE, _settings.PlainDiskColor));
        SetMetric(_diskReadRate, "rd " + FormatDiskRate(snapshot.DiskReadBytesPerSec), Palette.Disk);
        SetMetric(_diskWriteRate, "wr " + FormatDiskRate(snapshot.DiskWriteBytesPerSec), Palette.Disk);
        SetMetric(_power, FormatPower(snapshot.CpuPowerW, snapshot.GpuPowerW), PowerColor(snapshot.CpuPowerW, snapshot.GpuPowerW));
        SetMetric(_netUp, "up " + (snapshot.NetUpText ?? "--"), Palette.Net);
        SetMetric(_netDown, "dn " + (snapshot.NetDownText ?? "--"), Palette.Net);
        SetMetric(_netSentTotal, "tx " + (snapshot.NetSentTotalText ?? "--"), Palette.NetTotal);
        SetMetric(_netReceivedTotal, "rx " + (snapshot.NetReceivedTotalText ?? "--"), Palette.NetTotal);
        SetMetric(_score, FormatScore(snapshot.LoadScore), LoadColor(snapshot.LoadScore, 70, 88, Palette.Score));
    }

    public void UpdateWeather(WeatherSnapshot snapshot)
    {
        if (!_settings.ShowWeather)
        {
            return;
        }

        if (!snapshot.TemperatureC.HasValue)
        {
            var errorText = snapshot.Error switch
            {
                "city-required" => "задай город\nв настройках",
                "city-not-found" => "город не найден",
                _ => "погода n/a"
            };
            SetMetric(_weather, errorText, Palette.Danger);
            return;
        }

        var city = string.IsNullOrWhiteSpace(snapshot.LocationName)
            ? snapshot.Query
            : snapshot.LocationName;
        var staleMarker = snapshot.IsStale ? "~" : "";
        var temperature = snapshot.TemperatureC.Value > 0
            ? $"+{snapshot.TemperatureC.Value:0}°"
            : $"{snapshot.TemperatureC.Value:0}°";
        var condition = WeatherCodeText(snapshot.WeatherCode);
        SetMetric(_weather, $"{city}\n{staleMarker}{temperature} {condition}", snapshot.IsStale ? Palette.Warn : Palette.Weather);
    }

    private void ApplyMetricVisibility()
    {
        var y = 0;
        LayoutMetric(_gpuTemp, _settings.ShowGpuTemp, 32, ref y);
        LayoutMetric(_cpuTemp, _settings.ShowCpuTemp, 15, ref y);
        LayoutMetric(_fanPct, _settings.ShowFanPct, 15, ref y);
        LayoutMetric(_fanRpm, _settings.ShowFanRpm, 15, ref y);

        if (y > 0)
        {
            y += 5;
        }

        LayoutMetric(_cpuLoad, _settings.ShowCpuLoad, 14, ref y);
        LayoutMetric(_gpuLoad, _settings.ShowGpuLoad, 14, ref y);
        LayoutMetric(_ram, _settings.ShowRam, 14, ref y);
        LayoutMetric(_vram, _settings.ShowVram, 14, ref y);
        LayoutMetric(_diskC, _settings.ShowDiskC, 14, ref y);
        LayoutMetric(_diskD, _settings.ShowDiskD, 14, ref y);
        LayoutMetric(_diskE, _settings.ShowDiskE, 14, ref y);
        LayoutMetric(_diskReadRate, _settings.ShowDiskRates, 14, ref y);
        LayoutMetric(_diskWriteRate, _settings.ShowDiskRates, 14, ref y);
        LayoutMetric(_power, _settings.ShowPower, 14, ref y);
        LayoutMetric(_netUp, _settings.ShowNetworkRates, 14, ref y);
        LayoutMetric(_netDown, _settings.ShowNetworkRates, 14, ref y);
        LayoutMetric(_netSentTotal, _settings.ShowNetworkTotals, 14, ref y);
        LayoutMetric(_netReceivedTotal, _settings.ShowNetworkTotals, 14, ref y);
        LayoutMetric(_score, _settings.ShowLoadScore, 14, ref y);
        LayoutMetric(_weather, _settings.ShowWeather, 30, ref y);

        Size = new Size(118, Math.Max(1, y + 4));
        Invalidate();
    }

    private static void LayoutMetric(Label label, bool visible, int height, ref int y)
    {
        label.Visible = visible;
        if (!visible)
        {
            return;
        }

        label.Bounds = new Rectangle(0, y, 118, height);
        y += height;
    }

    public void KeepAboveTaskbar()
    {
        if (!Visible || IsDisposed || !_settings.TopMost)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HwndTopMost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    public void SetClickThrough(bool enabled)
    {
        const int gwlExStyle = -20;
        const int wsExTransparent = 0x00000020;
        var style = NativeMethods.GetWindowLong(Handle, gwlExStyle);
        style = enabled ? style | wsExTransparent : style & ~wsExTransparent;
        NativeMethods.SetWindowLong(Handle, gwlExStyle, style);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_settings.ShowBackground)
        {
            using var brush = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            using var path = CreateRoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 6);
            e.Graphics.FillPath(brush, path);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        KeepAboveTaskbar();
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (Created)
        {
            _settings.Location = Location;
        }
    }

    private static string FormatTemp(double? value) => value.HasValue ? $"{value:0}°" : "--°";

    private static string FormatRam(double? usedMb, double? totalMb)
    {
        return FormatMemory("ram", usedMb, totalMb);
    }

    private static string FormatVram(double? usedMb, double? totalMb)
    {
        return FormatMemory("vram", usedMb, totalMb);
    }

    private static string FormatMemory(string prefix, double? usedMb, double? totalMb)
    {
        if (!usedMb.HasValue || !totalMb.HasValue || totalMb.Value <= 0)
        {
            return prefix + " --/--G";
        }

        var usedGb = usedMb.Value / 1024.0;
        var totalGb = totalMb.Value / 1024.0;
        return string.Format(CultureInfo.InvariantCulture, "{0} {1:0.0}/{2:0}G", prefix, usedGb, totalGb);
    }

    private static string FormatDisk(string label, DiskSnapshot? disk)
    {
        if (disk?.UsedGb is not { } usedGb || disk.TotalGb is not { } totalGb || totalGb <= 0)
        {
            return label + " --/--G";
        }

        return string.Format(CultureInfo.InvariantCulture, "{0} {1:0}/{2:0}G", label, usedGb, totalGb);
    }

    private static string FormatDiskRate(double? bytesPerSecond)
    {
        if (!bytesPerSecond.HasValue)
        {
            return "--";
        }

        var bytes = Math.Max(0, bytesPerSecond.Value);
        if (bytes >= 1024.0 * 1024.0 * 1024.0)
        {
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0}G/s";
        }

        if (bytes >= 1024.0 * 1024.0)
        {
            return $"{bytes / (1024.0 * 1024.0):0.0}M/s";
        }

        if (bytes >= 1024.0)
        {
            var kibibytes = bytes / 1024.0;
            return kibibytes < 100 ? $"{kibibytes:0.0}K/s" : $"{kibibytes:0}K/s";
        }

        return $"{bytes:0}B/s";
    }

    private static string FormatPower(double? cpuPowerW, double? gpuPowerW)
    {
        if (!cpuPowerW.HasValue && !gpuPowerW.HasValue)
        {
            return "pwr --/--W";
        }

        var cpu = cpuPowerW.HasValue ? cpuPowerW.Value.ToString("0", CultureInfo.InvariantCulture) : "--";
        var gpu = gpuPowerW.HasValue ? gpuPowerW.Value.ToString("0", CultureInfo.InvariantCulture) : "--";
        return $"pwr {cpu}/{gpu}W";
    }

    private static string FormatScore(double? score)
    {
        return score.HasValue
            ? $"cap {score.Value:0}"
            : "cap --";
    }

    private static string WeatherCodeText(int? code)
    {
        return code switch
        {
            0 => "ясно",
            1 => "малооблачно",
            2 => "облачно",
            3 => "пасмурно",
            45 or 48 => "туман",
            51 or 53 or 55 => "морось",
            56 or 57 => "ледяная морось",
            61 or 63 or 65 => "дождь",
            66 or 67 => "ледяной дождь",
            71 or 73 or 75 => "снег",
            77 => "снежная крупа",
            80 or 81 or 82 => "ливень",
            85 or 86 => "снегопад",
            95 => "гроза",
            96 or 99 => "гроза с градом",
            _ => "погода"
        };
    }

    private static void SetMetric(Label label, string text, Color color)
    {
        label.Text = text;
        label.ForeColor = color;
    }

    private static Color TempColor(double? value, double warn, double danger, Color normal)
    {
        if (!value.HasValue)
        {
            return normal;
        }

        if (value.Value >= danger)
        {
            return Palette.Danger;
        }

        return value.Value >= warn ? Palette.Warn : normal;
    }

    private static Color LoadColor(double? value, double warn, double danger, Color normal)
    {
        if (!value.HasValue)
        {
            return normal;
        }

        if (value.Value >= danger)
        {
            return Palette.Danger;
        }

        return value.Value >= warn ? Palette.Warn : normal;
    }

    private static Color PowerColor(double? cpuPowerW, double? gpuPowerW)
    {
        var total = (cpuPowerW ?? 0) + (gpuPowerW ?? 0);
        if (total >= 500)
        {
            return Palette.Danger;
        }

        return total >= 380 ? Palette.Warn : Palette.Power;
    }

    private static Color DiskColor(DiskSnapshot? disk, bool plainDiskColor)
    {
        if (plainDiskColor)
        {
            return Palette.Disk;
        }

        if (disk?.UsedPct is not { } usedPct)
        {
            return Palette.Disk;
        }

        return LoadColor(usedPct, 85, 95, Palette.Disk);
    }

    private void BeginDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _settings.ClickThrough)
        {
            return;
        }

        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, 0xA1, 2, 0);
    }
}

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly NumericUpDown _interval;
    private readonly NumericUpDown _weatherInterval;
    private readonly TextBox _adapter;
    private readonly TextBox _weatherCity;
    private readonly CheckBox _topMost;
    private readonly CheckBox _background;
    private readonly CheckBox _clickThrough;
    private readonly CheckBox _showGpuTemp;
    private readonly CheckBox _showCpuTemp;
    private readonly CheckBox _showFanPct;
    private readonly CheckBox _showFanRpm;
    private readonly CheckBox _showCpuLoad;
    private readonly CheckBox _showGpuLoad;
    private readonly CheckBox _showRam;
    private readonly CheckBox _showVram;
    private readonly CheckBox _showDiskC;
    private readonly CheckBox _showDiskD;
    private readonly CheckBox _showDiskE;
    private readonly CheckBox _showDiskRates;
    private readonly CheckBox _showPower;
    private readonly CheckBox _showNetworkRates;
    private readonly CheckBox _showNetworkTotals;
    private readonly CheckBox _showLoadScore;
    private readonly CheckBox _showWeather;
    private readonly CheckBox _plainDiskColor;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Wizard Monitoring Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 620);

        _interval = new NumericUpDown
        {
            Minimum = 500,
            Maximum = 10000,
            Increment = 500,
            Value = Math.Clamp(settings.UpdateIntervalMs, 500, 10000),
            Location = new Point(160, 18),
            Size = new Size(165, 23)
        };
        _adapter = new TextBox
        {
            Text = settings.NetAdapterMatch,
            Location = new Point(160, 52),
            Size = new Size(325, 23)
        };
        _weatherCity = new TextBox
        {
            Text = settings.WeatherCity,
            Location = new Point(160, 86),
            Size = new Size(325, 23)
        };
        _weatherInterval = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 1440,
            Increment = 1,
            Value = Math.Clamp(settings.WeatherUpdateIntervalMinutes, 1, 1440),
            Location = new Point(160, 120),
            Size = new Size(165, 23)
        };
        _topMost = new CheckBox
        {
            Text = "Always on top",
            Checked = settings.TopMost,
            Location = new Point(25, 154),
            Size = new Size(160, 24)
        };
        _background = new CheckBox
        {
            Text = "Subtle background",
            Checked = settings.ShowBackground,
            Location = new Point(25, 180),
            Size = new Size(160, 24)
        };
        _clickThrough = new CheckBox
        {
            Text = "Click through",
            Checked = settings.ClickThrough,
            Location = new Point(25, 206),
            Size = new Size(160, 24)
        };

        Controls.Add(new Label { Text = "Update interval, ms", Location = new Point(25, 20), AutoSize = true });
        Controls.Add(_interval);
        Controls.Add(new Label { Text = "Network adapter", Location = new Point(25, 55), AutoSize = true });
        Controls.Add(_adapter);
        Controls.Add(new Label { Text = "Weather city", Location = new Point(25, 89), AutoSize = true });
        Controls.Add(_weatherCity);
        Controls.Add(new Label { Text = "Weather update, min", Location = new Point(25, 123), AutoSize = true });
        Controls.Add(_weatherInterval);
        Controls.Add(_topMost);
        Controls.Add(_background);
        Controls.Add(_clickThrough);

        var rows = new GroupBox
        {
            Text = "Overlay rows",
            Location = new Point(25, 240),
            Size = new Size(460, 272)
        };

        _showGpuTemp = AddRowCheckBox(rows, "GPU temperature", settings.ShowGpuTemp, 15, 24);
        _showCpuTemp = AddRowCheckBox(rows, "CPU temperature", settings.ShowCpuTemp, 15, 50);
        _showFanPct = AddRowCheckBox(rows, "Fan percent", settings.ShowFanPct, 15, 76);
        _showFanRpm = AddRowCheckBox(rows, "Fan RPM", settings.ShowFanRpm, 15, 102);
        _showCpuLoad = AddRowCheckBox(rows, "CPU load", settings.ShowCpuLoad, 15, 128);
        _showGpuLoad = AddRowCheckBox(rows, "GPU load", settings.ShowGpuLoad, 15, 154);
        _showRam = AddRowCheckBox(rows, "RAM", settings.ShowRam, 15, 180);

        _showVram = AddRowCheckBox(rows, "VRAM", settings.ShowVram, 240, 24);
        _showDiskC = AddRowCheckBox(rows, "Disk C", settings.ShowDiskC, 240, 50);
        _showDiskD = AddRowCheckBox(rows, "Disk D", settings.ShowDiskD, 240, 76);
        _showDiskE = AddRowCheckBox(rows, "Disk E", settings.ShowDiskE, 240, 102);
        _showDiskRates = AddRowCheckBox(rows, "Disk read/write", settings.ShowDiskRates, 240, 128);
        _showPower = AddRowCheckBox(rows, "Power", settings.ShowPower, 240, 154);
        _showNetworkRates = AddRowCheckBox(rows, "Network up/down", settings.ShowNetworkRates, 240, 180);
        _showNetworkTotals = AddRowCheckBox(rows, "Network tx/rx", settings.ShowNetworkTotals, 240, 206);
        _showLoadScore = AddRowCheckBox(rows, "Cap score", settings.ShowLoadScore, 15, 206);
        _showWeather = AddRowCheckBox(rows, "Weather", settings.ShowWeather, 15, 232);
        Controls.Add(rows);

        _plainDiskColor = new CheckBox
        {
            Text = "Plain disk color",
            Checked = settings.PlainDiskColor,
            Location = new Point(25, 524),
            Size = new Size(200, 24)
        };
        Controls.Add(_plainDiskColor);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(330, 579), Size = new Size(75, 26) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(410, 579), Size = new Size(75, 26) };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static CheckBox AddRowCheckBox(Control parent, string text, bool isChecked, int x, int y)
    {
        var checkBox = new CheckBox
        {
            Text = text,
            Checked = isChecked,
            Location = new Point(x, y),
            Size = new Size(180, 24)
        };

        parent.Controls.Add(checkBox);
        return checkBox;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            _settings.UpdateIntervalMs = (int)_interval.Value;
            _settings.NetAdapterMatch = _adapter.Text.Trim();
            _settings.WeatherCity = _weatherCity.Text.Trim();
            _settings.WeatherUpdateIntervalMinutes = (int)_weatherInterval.Value;
            _settings.TopMost = _topMost.Checked;
            _settings.ShowBackground = _background.Checked;
            _settings.ClickThrough = _clickThrough.Checked;
            _settings.ShowGpuTemp = _showGpuTemp.Checked;
            _settings.ShowCpuTemp = _showCpuTemp.Checked;
            _settings.ShowFanPct = _showFanPct.Checked;
            _settings.ShowFanRpm = _showFanRpm.Checked;
            _settings.ShowCpuLoad = _showCpuLoad.Checked;
            _settings.ShowGpuLoad = _showGpuLoad.Checked;
            _settings.ShowRam = _showRam.Checked;
            _settings.ShowVram = _showVram.Checked;
            _settings.ShowDiskC = _showDiskC.Checked;
            _settings.ShowDiskD = _showDiskD.Checked;
            _settings.ShowDiskE = _showDiskE.Checked;
            _settings.ShowDiskRates = _showDiskRates.Checked;
            _settings.ShowPower = _showPower.Checked;
            _settings.ShowNetworkRates = _showNetworkRates.Checked;
            _settings.ShowNetworkTotals = _showNetworkTotals.Checked;
            _settings.ShowLoadScore = _showLoadScore.Checked;
            _settings.ShowWeather = _showWeather.Checked;
            _settings.PlainDiskColor = _plainDiskColor.Checked;
        }

        base.OnFormClosing(e);
    }
}

internal sealed class MetricsReader : IDisposable
{
    private readonly GpuReader _gpu = new();
    private readonly CpuUsageReader _cpuUsage = new();
    private readonly CpuTemperatureReader _cpuTemp = new();
    private readonly AfterburnerReader _afterburner = new();
    private readonly MemoryReader _memory = new();
    private readonly DiskReader _disk = new();
    private readonly DiskRateReader _diskRate = new();
    private readonly NetworkRateReader _network;

    public MetricsReader(AppSettings settings)
    {
        _network = new NetworkRateReader(settings.NetAdapterMatch);
    }

    public void ApplySettings(AppSettings settings)
    {
        _network.AdapterMatch = settings.NetAdapterMatch;
    }

    public MetricsSnapshot Read()
    {
        var gpu = _gpu.Read();
        var afterburner = _afterburner.Read();
        var memory = _memory.Read();
        var diskC = _disk.Read("C");
        var diskD = _disk.Read("D");
        var diskE = _disk.Read("E");
        var diskRate = _diskRate.Read();
        var network = _network.Read();
        var cpuPct = _cpuUsage.Read();
        var cpuTemp = _cpuTemp.Read() ?? afterburner.CpuTempC;
        var loadScore = CalculateLoadScore(
            cpuPct,
            gpu.UtilPct,
            memory.UsedPct,
            CalculateUsedPercent(gpu.MemUsedMb, gpu.MemTotalMb),
            afterburner.CpuPowerW,
            gpu.PowerW,
            network.UpBps,
            network.DownBps);

        return new MetricsSnapshot
        {
            UptimeSec = Math.Round(Environment.TickCount64 / 1000.0, 0),
            CpuPct = cpuPct,
            CpuTempC = cpuTemp,
            CpuPowerW = afterburner.CpuPowerW,
            LoadScore = loadScore,
            RamUsedPct = memory.UsedPct,
            RamUsedMb = memory.UsedMb,
            RamTotalMb = memory.TotalMb,
            DiskC = diskC,
            DiskD = diskD,
            DiskE = diskE,
            DiskReadBytesPerSec = diskRate.ReadBytesPerSec,
            DiskWriteBytesPerSec = diskRate.WriteBytesPerSec,
            GpuTempC = gpu.TempC,
            GpuPct = gpu.UtilPct,
            GpuMemPct = gpu.MemPct,
            GpuMemUsedMb = gpu.MemUsedMb,
            GpuMemTotalMb = gpu.MemTotalMb,
            GpuPowerW = gpu.PowerW,
            GpuFanPct = gpu.FanPct,
            GpuFanRpm = gpu.FanRpm,
            GpuClockMhz = gpu.ClockMhz,
            GpuMemClockMhz = gpu.MemClockMhz,
            GpuPState = gpu.PState,
            NvStatus = gpu.Status,
            NvStatusDetail = gpu.StatusDetail,
            NetAdapter = network.Adapter,
            NetUpBps = network.UpBps,
            NetDownBps = network.DownBps,
            NetUpText = network.UpText,
            NetDownText = network.DownText,
            NetSentTotalBytes = network.SentTotalBytes,
            NetReceivedTotalBytes = network.ReceivedTotalBytes,
            NetSentTotalText = network.SentTotalText,
            NetReceivedTotalText = network.ReceivedTotalText,
            NetStatus = network.Status
        };
    }

    private static double? CalculateLoadScore(double? cpuPct, double? gpuPct, double? ramPct, double? vramPct, double? cpuPowerW, double? gpuPowerW, double? netUpBps, double? netDownBps)
    {
        var weighted = 0.0;
        var weight = 0.0;

        Add(cpuPct, 0.20);
        Add(gpuPct, 0.30);
        Add(ramPct, 0.15);
        Add(vramPct, 0.15);

        var totalPower = (cpuPowerW ?? 0) + (gpuPowerW ?? 0);
        if (totalPower > 0)
        {
            Add(Math.Clamp(totalPower / 500.0 * 100.0, 0, 100), 0.15);
        }

        var totalNetwork = (netUpBps ?? 0) + (netDownBps ?? 0);
        if (totalNetwork > 0)
        {
            Add(Math.Clamp(totalNetwork / 100_000_000.0 * 100.0, 0, 100), 0.05);
        }

        return weight <= 0 ? null : Math.Round(weighted / weight, 0);

        void Add(double? value, double valueWeight)
        {
            if (!value.HasValue)
            {
                return;
            }

            weighted += Math.Clamp(value.Value, 0, 100) * valueWeight;
            weight += valueWeight;
        }
    }

    private static double? CalculateUsedPercent(double? usedMb, double? totalMb)
    {
        if (!usedMb.HasValue || !totalMb.HasValue || totalMb.Value <= 0)
        {
            return null;
        }

        return Math.Clamp(usedMb.Value / totalMb.Value * 100.0, 0, 100);
    }

    public void Dispose()
    {
        _gpu.Dispose();
        _diskRate.Dispose();
    }
}

internal sealed class GpuReader : IDisposable
{
    private readonly string? _nvidiaSmi;
    private readonly NvmlReader _nvml = new();

    public GpuReader()
    {
        _nvidiaSmi = FindNvidiaSmi();
    }

    public GpuSnapshot Read()
    {
        var snapshot = new GpuSnapshot
        {
            Status = _nvidiaSmi is null ? "no-nvidia-smi" : "ok",
            StatusDetail = _nvidiaSmi is null ? "nvidia-smi.exe was not found" : null
        };

        if (_nvidiaSmi is not null)
        {
            try
            {
                var query = "temperature.gpu,utilization.gpu,utilization.memory,memory.used,memory.total,power.draw,fan.speed,clocks.gr,clocks.mem,pstate";
                var output = RunHidden(_nvidiaSmi, $"--query-gpu={query} --format=csv,noheader,nounits");
                if (string.IsNullOrWhiteSpace(output))
                {
                    snapshot.Status = "nvidia-smi-no-output";
                    snapshot.StatusDetail = "nvidia-smi returned empty stdout";
                    snapshot.FanRpm = _nvml.ReadFanRpm();
                    return snapshot;
                }

                var parts = output.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 1)
                {
                    snapshot.TempC = ParseDouble(parts[0]);
                }

                if (parts.Length >= 2)
                {
                    snapshot.UtilPct = ParseDouble(parts[1]);
                }

                if (parts.Length >= 3)
                {
                    snapshot.MemPct = ParseDouble(parts[2]);
                }

                if (parts.Length >= 4)
                {
                    snapshot.MemUsedMb = ParseDouble(parts[3]);
                }

                if (parts.Length >= 5)
                {
                    snapshot.MemTotalMb = ParseDouble(parts[4]);
                }

                if (parts.Length >= 6)
                {
                    snapshot.PowerW = ParseDouble(parts[5]);
                }

                if (parts.Length >= 7)
                {
                    snapshot.FanPct = ParseDouble(parts[6]);
                }

                if (parts.Length >= 8)
                {
                    snapshot.ClockMhz = ParseDouble(parts[7]);
                }

                if (parts.Length >= 9)
                {
                    snapshot.MemClockMhz = ParseDouble(parts[8]);
                }

                if (parts.Length >= 10)
                {
                    snapshot.PState = string.IsNullOrWhiteSpace(parts[9]) ? null : parts[9];
                }

                if (!snapshot.TempC.HasValue && !snapshot.UtilPct.HasValue && !snapshot.PowerW.HasValue)
                {
                    snapshot.Status = "nvidia-smi-unparsed-output";
                    snapshot.StatusDetail = Truncate(output, 240);
                }
            }
            catch (TimeoutException ex)
            {
                snapshot.Status = "nvidia-smi-timeout";
                snapshot.StatusDetail = ex.Message;
            }
            catch (Exception ex)
            {
                snapshot.Status = "nvidia-smi-error";
                snapshot.StatusDetail = ex.GetType().Name + ": " + ex.Message;
            }
        }

        snapshot.FanRpm = _nvml.ReadFanRpm();
        return snapshot;
    }

    public void Dispose()
    {
        _nvml.Dispose();
    }

    private static string? FindNvidiaSmi()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "nvidia-smi.exe"),
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
        };

        return paths.FirstOrDefault(File.Exists);
    }

    private static string RunHidden(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            return string.Empty;
        }

        if (!process.WaitForExit(1500))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException("nvidia-smi did not finish within 1500 ms");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException($"nvidia-smi exited {process.ExitCode}: {Truncate(error, 240)}");
        }

        return output.Trim();
    }

    private static string Truncate(string value, int maxLength)
    {
        value = value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static double? ParseDouble(string value)
    {
        if (double.TryParse(value.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}

internal sealed class NvmlReader : IDisposable
{
    private bool _initialized;
    private bool _ok;
    private IntPtr _device;
    private uint _fanCount = 1;

    public double? ReadFanRpm()
    {
        EnsureInitialized();
        if (!_ok)
        {
            return null;
        }

        var total = 0.0;
        var readings = 0;

        for (uint fan = 0; fan < _fanCount; fan++)
        {
            var info = new NvmlFanSpeedInfo
            {
                Version = 0x0100000C,
                Fan = fan
            };

            try
            {
                var status = NativeMethods.NvmlDeviceGetFanSpeedRPM(_device, ref info);
                if (status == 0)
                {
                    total += info.Speed;
                    readings++;
                }
            }
            catch
            {
                _ok = false;
                return null;
            }
        }

        return readings == 0 ? null : Math.Round(total / readings, 0);
    }

    public void Dispose()
    {
        if (_ok)
        {
            try
            {
                NativeMethods.NvmlShutdown();
            }
            catch
            {
            }
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            if (NativeMethods.NvmlInit() != 0)
            {
                return;
            }

            if (NativeMethods.NvmlDeviceGetHandleByIndex(0, out _device) != 0 || _device == IntPtr.Zero)
            {
                return;
            }

            if (NativeMethods.NvmlDeviceGetNumFans(_device, out var fanCount) == 0 && fanCount > 0)
            {
                _fanCount = fanCount;
            }

            _ok = true;
        }
        catch
        {
            _ok = false;
        }
    }
}

internal sealed class CpuUsageReader
{
    private ulong? _prevIdle;
    private ulong? _prevKernel;
    private ulong? _prevUser;

    public double? Read()
    {
        if (!NativeMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return null;
        }

        var idle = idleTime.ToUInt64();
        var kernel = kernelTime.ToUInt64();
        var user = userTime.ToUInt64();

        if (!_prevIdle.HasValue || !_prevKernel.HasValue || !_prevUser.HasValue)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            return null;
        }

        var idleDelta = idle - _prevIdle.Value;
        var kernelDelta = kernel - _prevKernel.Value;
        var userDelta = user - _prevUser.Value;
        var total = kernelDelta + userDelta;

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        if (total == 0)
        {
            return null;
        }

        var usage = ((total - idleDelta) * 100.0) / total;
        return Math.Clamp(Math.Round(usage, 0), 0, 100);
    }
}

internal sealed class AfterburnerReader
{
    public AfterburnerSnapshot Read()
    {
        const uint validSignature = 0x4D41484D;
        const uint cpuTemperatureSourceId = 0x80;
        const uint cpuPowerSourceId = 0x100;
        const int minHeaderSize = 32;
        const int minEntrySize = 1324;
        const int dataOffset = 1300;
        const int gpuOffset = 1316;
        const int sourceIdOffset = 1320;

        try
        {
            using var memory = OpenMemory();
            if (memory is null)
            {
                return AfterburnerSnapshot.Empty;
            }

            using var stream = memory.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            if (reader.ReadUInt32() != validSignature)
            {
                return AfterburnerSnapshot.Empty;
            }

            var version = reader.ReadUInt32();
            var headerSize = reader.ReadUInt32();
            var entryCount = reader.ReadUInt32();
            var entrySize = reader.ReadUInt32();
            var timestamp = reader.ReadInt32();

            if (version < 0x00020000 || headerSize < minHeaderSize || entrySize < minEntrySize || entryCount == 0 || entryCount > 1024)
            {
                return AfterburnerSnapshot.Empty;
            }

            var lastPollUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            if (DateTimeOffset.UtcNow - lastPollUtc > TimeSpan.FromSeconds(30))
            {
                return AfterburnerSnapshot.Empty;
            }

            double? globalCpuTemp = null;
            double? maxCoreCpuTemp = null;
            double? cpuPower = null;
            var entry = new byte[entrySize];

            for (var index = 0; index < entryCount; index++)
            {
                stream.Position = headerSize + index * entrySize;
                if (stream.Read(entry, 0, entry.Length) < minEntrySize)
                {
                    continue;
                }

                var sourceId = BitConverter.ToUInt32(entry, sourceIdOffset);
                if (sourceId is not (cpuTemperatureSourceId or cpuPowerSourceId))
                {
                    continue;
                }

                var value = BitConverter.ToSingle(entry, dataOffset);
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0 || value >= 1000)
                {
                    continue;
                }

                if (sourceId == cpuPowerSourceId)
                {
                    if (value is > 0 and < 400)
                    {
                        cpuPower = Math.Round(value, 1);
                    }

                    continue;
                }

                if (value is <= 0 or >= 120)
                {
                    continue;
                }

                var gpu = BitConverter.ToUInt32(entry, gpuOffset);
                if (gpu == uint.MaxValue)
                {
                    globalCpuTemp = value;
                }
                else if (!maxCoreCpuTemp.HasValue || value > maxCoreCpuTemp.Value)
                {
                    maxCoreCpuTemp = value;
                }
            }

            var cpuTemp = globalCpuTemp ?? maxCoreCpuTemp;
            return new AfterburnerSnapshot
            {
                CpuTempC = cpuTemp.HasValue ? Math.Round(cpuTemp.Value, 0) : null,
                CpuPowerW = cpuPower
            };
        }
        catch
        {
            return AfterburnerSnapshot.Empty;
        }
    }

    private static MemoryMappedFile? OpenMemory()
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
}

internal sealed class CpuTemperatureReader
{
    private readonly string _mainFile = @"C:\Projects\wizard-monitoring\cpu_temp_helper\cpu_temp.txt";

    public double? Read()
    {
        return ReadFile(_mainFile);
    }

    private static double? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            if (info.LastWriteTime < DateTime.Now.AddSeconds(-15))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (double.TryParse(text.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 120)
            {
                return Math.Round(value, 0);
            }
        }
        catch
        {
        }

        return null;
    }

}

internal sealed class MemoryReader
{
    public MemorySnapshot Read()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
        {
            return MemorySnapshot.Empty;
        }

        var totalMb = Math.Round(status.TotalPhys / 1024.0 / 1024.0, 0);
        var freeMb = Math.Round(status.AvailPhys / 1024.0 / 1024.0, 0);
        var usedMb = totalMb - freeMb;

        return new MemorySnapshot
        {
            UsedPct = Math.Round((usedMb * 100.0) / Math.Max(totalMb, 1), 1),
            UsedMb = usedMb,
            TotalMb = totalMb
        };
    }
}

internal sealed class DiskReader
{
    public DiskSnapshot Read(string name)
    {
        try
        {
            var drive = new DriveInfo(name + @":\");
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return DiskSnapshot.Unavailable(name);
            }

            var totalGb = drive.TotalSize / 1024.0 / 1024.0 / 1024.0;
            var freeGb = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
            var usedGb = Math.Max(0, totalGb - freeGb);

            return new DiskSnapshot
            {
                Name = name,
                UsedGb = Math.Round(usedGb, 0),
                TotalGb = Math.Round(totalGb, 0),
                UsedPct = Math.Round((usedGb * 100.0) / Math.Max(totalGb, 1), 1)
            };
        }
        catch
        {
            return DiskSnapshot.Unavailable(name);
        }
    }
}

internal sealed class DiskRateReader : IDisposable
{
    private readonly List<DiskDevice> _devices = new();
    private long _previousTimestamp;

    public DiskRateReader()
    {
        for (var index = 0; index < 32; index++)
        {
            var handle = NativeMethods.CreateFile(
                $@"\\.\PhysicalDrive{index}",
                0,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                continue;
            }

            _devices.Add(new DiskDevice(handle));
        }
    }

    public DiskRateSnapshot Read()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var elapsedSeconds = _previousTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(_previousTimestamp, timestamp).TotalSeconds;
        var readBytes = 0L;
        var writeBytes = 0L;
        var hasDelta = false;

        foreach (var device in _devices)
        {
            if (!TryReadCounters(device.Handle, out var counters))
            {
                device.HasPreviousSample = false;
                continue;
            }

            if (device.HasPreviousSample)
            {
                readBytes += Math.Max(0, counters.BytesRead - device.BytesRead);
                writeBytes += Math.Max(0, counters.BytesWritten - device.BytesWritten);
                hasDelta = true;
            }

            device.BytesRead = counters.BytesRead;
            device.BytesWritten = counters.BytesWritten;
            device.HasPreviousSample = true;
        }

        _previousTimestamp = timestamp;
        if (!hasDelta || elapsedSeconds <= 0)
        {
            return DiskRateSnapshot.Empty;
        }

        return new DiskRateSnapshot
        {
            ReadBytesPerSec = Math.Round(readBytes / elapsedSeconds, 0),
            WriteBytesPerSec = Math.Round(writeBytes / elapsedSeconds, 0)
        };
    }

    private static bool TryReadCounters(SafeFileHandle handle, out DiskPerformance counters)
    {
        return NativeMethods.DeviceIoControl(
            handle,
            NativeMethods.IoctlDiskPerformance,
            IntPtr.Zero,
            0,
            out counters,
            (uint)Marshal.SizeOf<DiskPerformance>(),
            out _,
            IntPtr.Zero);
    }

    public void Dispose()
    {
        foreach (var device in _devices)
        {
            device.Handle.Dispose();
        }

        _devices.Clear();
    }

    private sealed class DiskDevice
    {
        public DiskDevice(SafeFileHandle handle)
        {
            Handle = handle;
        }

        public SafeFileHandle Handle { get; }
        public long BytesRead { get; set; }
        public long BytesWritten { get; set; }
        public bool HasPreviousSample { get; set; }
    }
}

internal sealed class NetworkRateReader
{
    private readonly Dictionary<string, NetCounters> _previous = new(StringComparer.OrdinalIgnoreCase);

    public NetworkRateReader(string adapterMatch)
    {
        AdapterMatch = adapterMatch;
    }

    public string AdapterMatch { get; set; }

    public NetworkSnapshot Read()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up)
            .ToArray();

        if (adapters.Length == 0)
        {
            return NetworkSnapshot.Empty;
        }

        var candidates = adapters.Where(IsForcedAdapter).ToArray();
        if (candidates.Length == 0)
        {
            candidates = adapters.Where(x => !IsIgnoredAdapter(x)).ToArray();
        }

        if (candidates.Length == 0)
        {
            candidates = adapters;
        }

        var now = DateTime.UtcNow;
        NetworkSnapshot? best = null;
        double bestScore = -1;

        foreach (var adapter in candidates)
        {
            IPv4InterfaceStatistics stats;
            try
            {
                stats = adapter.GetIPv4Statistics();
            }
            catch
            {
                continue;
            }

            var current = new NetCounters(stats.BytesSent, stats.BytesReceived, now);
            double? upBps = null;
            double? downBps = null;

            if (_previous.TryGetValue(adapter.Id, out var previous))
            {
                var elapsed = Math.Max((now - previous.Time).TotalSeconds, 0.001);
                upBps = Math.Max(0, current.Sent - previous.Sent) * 8.0 / elapsed;
                downBps = Math.Max(0, current.Received - previous.Received) * 8.0 / elapsed;
            }

            _previous[adapter.Id] = current;

            var score = (upBps ?? 0) + (downBps ?? 0);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            best = new NetworkSnapshot
            {
                Adapter = adapter.Name,
                UpBps = upBps.HasValue ? Math.Round(upBps.Value, 0) : null,
                DownBps = downBps.HasValue ? Math.Round(downBps.Value, 0) : null,
                UpText = FormatNetRate(upBps),
                DownText = FormatNetRate(downBps),
                SentTotalBytes = stats.BytesSent,
                ReceivedTotalBytes = stats.BytesReceived,
                SentTotalText = FormatByteTotal(stats.BytesSent),
                ReceivedTotalText = FormatByteTotal(stats.BytesReceived),
                Status = "ok"
            };
        }

        return best ?? NetworkSnapshot.Empty;
    }

    private bool IsForcedAdapter(NetworkInterface adapter)
    {
        if (string.IsNullOrWhiteSpace(AdapterMatch))
        {
            return false;
        }

        return adapter.Name.Contains(AdapterMatch, StringComparison.OrdinalIgnoreCase) ||
               adapter.Description.Contains(AdapterMatch, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredAdapter(NetworkInterface adapter)
    {
        var text = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
        var ignored = new[] { "virtual", "vmware", "hyper-v", "vethernet", "tap", "tunnel", "loopback", "bluetooth", "openvpn", "wintun", "wireguard", "docker", "tailscale" };
        return ignored.Any(text.Contains);
    }

    private static string? FormatNetRate(double? bitsPerSecond)
    {
        if (!bitsPerSecond.HasValue)
        {
            return "--";
        }

        var bits = bitsPerSecond.Value;
        if (bits >= 1_000_000_000)
        {
            return $"{bits / 1_000_000_000:0.0}G";
        }

        if (bits >= 1_000_000)
        {
            return $"{bits / 1_000_000:0.0}M";
        }

        if (bits >= 1000)
        {
            var kbits = bits / 1000;
            return kbits < 100 ? $"{kbits:0.0}K" : $"{kbits:0}K";
        }

        return $"{bits:0}b";
    }

    private static string FormatByteTotal(long bytes)
    {
        var value = Math.Max(0, bytes);
        if (value >= 1_000_000_000_000L)
        {
            return $"{value / 1_000_000_000_000.0:0.00}T";
        }

        if (value >= 1_000_000_000L)
        {
            return $"{value / 1_000_000_000.0:0.00}G";
        }

        if (value >= 1_000_000L)
        {
            return $"{value / 1_000_000.0:0.0}M";
        }

        if (value >= 1_000L)
        {
            return $"{value / 1_000.0:0.0}K";
        }

        return $"{value}B";
    }
}

internal sealed class WeatherReader : IDisposable
{
    private readonly HttpClient _httpClient;
    private string? _cachedQuery;
    private WeatherLocation? _cachedLocation;

    public WeatherReader()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WizardMonitoring/1.0");
    }

    public async Task<WeatherSnapshot> ReadAsync(string city, CancellationToken cancellationToken)
    {
        var query = city.Trim();
        if (query.Length == 0)
        {
            return new WeatherSnapshot { Error = "city-required" };
        }

        var location = await ResolveLocationAsync(query, cancellationToken);
        if (location is null)
        {
            return new WeatherSnapshot { Query = query, Error = "city-not-found" };
        }

        var forecastUrl = "https://api.open-meteo.com/v1/forecast" +
                          "?latitude=" + location.Latitude.ToString(CultureInfo.InvariantCulture) +
                          "&longitude=" + location.Longitude.ToString(CultureInfo.InvariantCulture) +
                          "&current=temperature_2m,weather_code" +
                          "&temperature_unit=celsius&timezone=auto&forecast_days=1";

        using var response = await _httpClient.GetAsync(forecastUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("current", out var current) ||
            !current.TryGetProperty("temperature_2m", out var temperature) ||
            !temperature.TryGetDouble(out var temperatureC))
        {
            return new WeatherSnapshot { Query = query, LocationName = location.Name, Error = "invalid-response" };
        }

        int? weatherCode = null;
        if (current.TryGetProperty("weather_code", out var code) && code.TryGetInt32(out var parsedCode))
        {
            weatherCode = parsedCode;
        }

        return new WeatherSnapshot
        {
            Query = query,
            LocationName = location.Name,
            TemperatureC = temperatureC,
            WeatherCode = weatherCode,
            FetchedAt = DateTimeOffset.Now
        };
    }

    private async Task<WeatherLocation?> ResolveLocationAsync(string query, CancellationToken cancellationToken)
    {
        if (_cachedLocation is not null && string.Equals(_cachedQuery, query, StringComparison.OrdinalIgnoreCase))
        {
            return _cachedLocation;
        }

        var geocodingUrl = "https://geocoding-api.open-meteo.com/v1/search?name=" +
                           Uri.EscapeDataString(query) +
                           "&count=1&language=ru&format=json";

        using var response = await _httpClient.GetAsync(geocodingUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() == 0)
        {
            return null;
        }

        var first = results[0];
        if (!first.TryGetProperty("latitude", out var latitudeElement) ||
            !latitudeElement.TryGetDouble(out var latitude) ||
            !first.TryGetProperty("longitude", out var longitudeElement) ||
            !longitudeElement.TryGetDouble(out var longitude))
        {
            return null;
        }

        var name = first.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;

        _cachedQuery = query;
        _cachedLocation = new WeatherLocation(name ?? query, latitude, longitude);
        return _cachedLocation;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WizardMonitoring",
        "settings.json");

    public Point Location { get; set; } = new(100, 100);
    public bool TopMost { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool ShowBackground { get; set; }
    public int UpdateIntervalMs { get; set; } = 1000;
    public string NetAdapterMatch { get; set; } = "Realtek 8812BU";
    public string WeatherCity { get; set; } = "";
    public int WeatherUpdateIntervalMinutes { get; set; } = 10;
    public bool ShowGpuTemp { get; set; } = true;
    public bool ShowCpuTemp { get; set; } = true;
    public bool ShowFanPct { get; set; } = true;
    public bool ShowFanRpm { get; set; } = true;
    public bool ShowCpuLoad { get; set; } = true;
    public bool ShowGpuLoad { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowVram { get; set; } = true;
    public bool ShowDiskC { get; set; } = true;
    public bool ShowDiskD { get; set; } = true;
    public bool ShowDiskE { get; set; } = true;
    public bool ShowDiskRates { get; set; } = true;
    public bool ShowPower { get; set; } = true;
    public bool ShowNetworkRates { get; set; } = true;
    public bool ShowNetworkTotals { get; set; } = true;
    public bool ShowLoadScore { get; set; } = true;
    public bool ShowWeather { get; set; }
    public bool PlainDiskColor { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
        }
    }
}

internal sealed class MetricsLogger : IDisposable
{
    private const string Header =
        "time,uptime_sec,cpu_pct,cpu_temp_c,cpu_power_w,load_score,ram_used_pct,ram_used_mb,ram_total_mb,gpu_temp_c,gpu_util_pct,gpu_mem_util_pct,gpu_mem_used_mb,gpu_mem_total_mb,gpu_power_w,gpu_fan_pct,gpu_fan_rpm,gpu_clock_mhz,gpu_mem_clock_mhz,gpu_pstate,nv_status,net_adapter,net_up_bps,net_down_bps,net_up,net_down,net_status";

    private readonly StreamWriter _writer;
    private readonly object _sync = new();

    public MetricsLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var needsHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        if (needsHeader)
        {
            _writer.WriteLine(Header);
        }
    }

    public void Write(MetricsSnapshot snapshot)
    {
        lock (_sync)
        {
            var values = new object?[]
            {
                DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                snapshot.UptimeSec,
                snapshot.CpuPct,
                snapshot.CpuTempC,
                snapshot.CpuPowerW,
                snapshot.LoadScore,
                snapshot.RamUsedPct,
                snapshot.RamUsedMb,
                snapshot.RamTotalMb,
                snapshot.GpuTempC,
                snapshot.GpuPct,
                snapshot.GpuMemPct,
                snapshot.GpuMemUsedMb,
                snapshot.GpuMemTotalMb,
                snapshot.GpuPowerW,
                snapshot.GpuFanPct,
                snapshot.GpuFanRpm,
                snapshot.GpuClockMhz,
                snapshot.GpuMemClockMhz,
                snapshot.GpuPState,
                snapshot.NvStatus,
                snapshot.NetAdapter,
                snapshot.NetUpBps,
                snapshot.NetDownBps,
                snapshot.NetUpText,
                snapshot.NetDownText,
                snapshot.NetStatus
            };

            _writer.WriteLine(string.Join(",", values.Select(Csv)));
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private static string Csv(object? value)
    {
        if (value is null)
        {
            return "\"\"";
        }

        var text = value switch
        {
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}

internal sealed class EventLogger : IDisposable
{
    private const string Header =
        "time,event,status,detail,uptime_sec,cpu_pct,cpu_temp_c,cpu_power_w,load_score,ram_used_pct,gpu_temp_c,gpu_util_pct,gpu_power_w,gpu_fan_pct,gpu_fan_rpm,gpu_pstate,net_up,net_down";

    private readonly StreamWriter _writer;
    private readonly object _sync = new();

    public EventLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var needsHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        if (needsHeader)
        {
            _writer.WriteLine(Header);
        }
    }

    public void Write(string eventName, MetricsSnapshot snapshot, string? status, string? detail)
    {
        lock (_sync)
        {
            var values = new object?[]
            {
                DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                eventName,
                status,
                detail,
                snapshot.UptimeSec,
                snapshot.CpuPct,
                snapshot.CpuTempC,
                snapshot.CpuPowerW,
                snapshot.LoadScore,
                snapshot.RamUsedPct,
                snapshot.GpuTempC,
                snapshot.GpuPct,
                snapshot.GpuPowerW,
                snapshot.GpuFanPct,
                snapshot.GpuFanRpm,
                snapshot.GpuPState,
                snapshot.NetUpText,
                snapshot.NetDownText
            };

            _writer.WriteLine(string.Join(",", values.Select(Csv)));
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private static string Csv(object? value)
    {
        if (value is null)
        {
            return "\"\"";
        }

        var text = value switch
        {
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}

internal record MetricsSnapshot
{
    public static MetricsSnapshot Empty { get; } = new();

    public double? UptimeSec { get; init; }
    public double? CpuPct { get; init; }
    public double? CpuTempC { get; init; }
    public double? CpuPowerW { get; init; }
    public double? LoadScore { get; init; }
    public double? RamUsedPct { get; init; }
    public double? RamUsedMb { get; init; }
    public double? RamTotalMb { get; init; }
    public DiskSnapshot? DiskC { get; init; }
    public DiskSnapshot? DiskD { get; init; }
    public DiskSnapshot? DiskE { get; init; }
    public double? DiskReadBytesPerSec { get; init; }
    public double? DiskWriteBytesPerSec { get; init; }
    public double? GpuTempC { get; init; }
    public double? GpuPct { get; init; }
    public double? GpuMemPct { get; init; }
    public double? GpuMemUsedMb { get; init; }
    public double? GpuMemTotalMb { get; init; }
    public double? GpuPowerW { get; init; }
    public double? GpuFanPct { get; init; }
    public double? GpuFanRpm { get; init; }
    public double? GpuClockMhz { get; init; }
    public double? GpuMemClockMhz { get; init; }
    public string? GpuPState { get; init; }
    public string? NvStatus { get; init; }
    public string? NvStatusDetail { get; init; }
    public string? NetAdapter { get; init; }
    public double? NetUpBps { get; init; }
    public double? NetDownBps { get; init; }
    public string? NetUpText { get; init; }
    public string? NetDownText { get; init; }
    public long? NetSentTotalBytes { get; init; }
    public long? NetReceivedTotalBytes { get; init; }
    public string? NetSentTotalText { get; init; }
    public string? NetReceivedTotalText { get; init; }
    public string? NetStatus { get; init; }
}

internal sealed record GpuSnapshot
{
    public double? TempC { get; set; }
    public double? UtilPct { get; set; }
    public double? MemPct { get; set; }
    public double? MemUsedMb { get; set; }
    public double? MemTotalMb { get; set; }
    public double? PowerW { get; set; }
    public double? FanPct { get; set; }
    public double? FanRpm { get; set; }
    public double? ClockMhz { get; set; }
    public double? MemClockMhz { get; set; }
    public string? PState { get; set; }
    public string Status { get; set; } = "";
    public string? StatusDetail { get; set; }
}

internal sealed record AfterburnerSnapshot
{
    public static AfterburnerSnapshot Empty { get; } = new();

    public double? CpuTempC { get; init; }
    public double? CpuPowerW { get; init; }
}

internal sealed record NetworkSnapshot
{
    public static NetworkSnapshot Empty { get; } = new() { UpText = "--", DownText = "--", Status = "no-adapter" };

    public string? Adapter { get; init; }
    public double? UpBps { get; init; }
    public double? DownBps { get; init; }
    public string? UpText { get; init; }
    public string? DownText { get; init; }
    public long? SentTotalBytes { get; init; }
    public long? ReceivedTotalBytes { get; init; }
    public string? SentTotalText { get; init; }
    public string? ReceivedTotalText { get; init; }
    public string? Status { get; init; }
}

internal sealed record MemorySnapshot
{
    public static MemorySnapshot Empty { get; } = new();

    public double? UsedPct { get; init; }
    public double? UsedMb { get; init; }
    public double? TotalMb { get; init; }
}

internal sealed record DiskSnapshot
{
    public static DiskSnapshot Unavailable(string name) => new() { Name = name };

    public string Name { get; init; } = "";
    public double? UsedGb { get; init; }
    public double? TotalGb { get; init; }
    public double? UsedPct { get; init; }
}

internal sealed record DiskRateSnapshot
{
    public static DiskRateSnapshot Empty { get; } = new();

    public double? ReadBytesPerSec { get; init; }
    public double? WriteBytesPerSec { get; init; }
}

internal sealed record WeatherSnapshot
{
    public static WeatherSnapshot Empty { get; } = new();

    public string? Query { get; init; }
    public string? LocationName { get; init; }
    public double? TemperatureC { get; init; }
    public int? WeatherCode { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public string? Error { get; init; }
    public bool IsStale { get; init; }
}

internal sealed record WeatherLocation(string Name, double Latitude, double Longitude);

internal readonly record struct NetCounters(long Sent, long Received, DateTime Time);

internal static class Palette
{
    public static readonly Color Gpu = Color.FromArgb(234, 244, 255);
    public static readonly Color Net = Color.FromArgb(184, 247, 199);
    public static readonly Color NetTotal = Color.FromArgb(170, 225, 255);
    public static readonly Color Cpu = Color.FromArgb(255, 221, 166);
    public static readonly Color Ram = Color.FromArgb(211, 224, 255);
    public static readonly Color Vram = Color.FromArgb(234, 244, 255);
    public static readonly Color Disk = Color.FromArgb(214, 232, 255);
    public static readonly Color Power = Color.FromArgb(255, 230, 190);
    public static readonly Color Score = Color.FromArgb(220, 255, 220);
    public static readonly Color Weather = Color.FromArgb(178, 226, 255);
    public static readonly Color Warn = Color.FromArgb(255, 179, 71);
    public static readonly Color Danger = Color.FromArgb(255, 77, 77);
}

internal static class NativeMethods
{
    public const uint PdhFmtDouble = 0x00000200;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;
    public const uint IoctlDiskPerformance = 0x00070020;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public static readonly IntPtr HwndTopMost = new(-1);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        out DiskPerformance outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int NvmlInit();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown", CallingConvention = CallingConvention.Cdecl)]
    public static extern int NvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetNumFans", CallingConvention = CallingConvention.Cdecl)]
    public static extern int NvmlDeviceGetNumFans(IntPtr device, out uint fanCount);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeedRPM", CallingConvention = CallingConvention.Cdecl)]
    public static extern int NvmlDeviceGetFanSpeedRPM(IntPtr device, ref NvmlFanSpeedInfo fanInfo);

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", EntryPoint = "PdhAddCounterW", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddCounter(IntPtr query, string fullCounterPath, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterValue")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint counterType, out PdhFmtCounterValue value);

    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    public static extern uint PdhCloseQuery(IntPtr query);
}

[StructLayout(LayoutKind.Sequential)]
internal struct FileTime
{
    public uint LowDateTime;
    public uint HighDateTime;

    public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhys;
    public ulong AvailPhys;
    public ulong TotalPageFile;
    public ulong AvailPageFile;
    public ulong TotalVirtual;
    public ulong AvailVirtual;
    public ulong AvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DiskPerformance
{
    public long BytesRead;
    public long BytesWritten;
    public long ReadTime;
    public long WriteTime;
    public long IdleTime;
    public uint ReadCount;
    public uint WriteCount;
    public uint QueueDepth;
    public uint SplitCount;
    public long QueryTime;
    public uint StorageDeviceNumber;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
    public string StorageManagerName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlFanSpeedInfo
{
    public uint Version;
    public uint Fan;
    public uint Speed;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PdhFmtCounterValue
{
    public readonly uint CStatus;
    public readonly double DoubleValue;
}
