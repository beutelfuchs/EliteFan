using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EliteFan;

public partial class MainWindow : Window
{
    private static readonly AriaMode[] TabModes = [AriaMode.Manual, AriaMode.Power, AriaMode.HeartRate];

    private AriaFan? _fan;
    private AriaStatus? _status;
    private bool _updatingUi;          // true while code (not the user) changes controls
    private DateTime _manualTouched;   // last time the user moved the manual slider
    private int _session;              // bumped on every disconnect; cancels a pending reconnect
    private ulong _address;            // last connected fan, for reconnecting
    private string _name = "";
    private int _powerSensorId, _heartSensorId;   // ANT+ device numbers the fan has paired (0 = none)
    private bool _bleSensors;

    private const string IdleHint = "Turn the fan on, close MyETraining, then press Connect.";

    private readonly DispatcherTimer _manualTimer, _powerTimer, _heartTimer, _powerMinTimer, _heartMinTimer;

    public MainWindow()
    {
        InitializeComponent();
        _manualTimer = Debounce(120, () => _fan!.SetAirflowAsync((int)ManualSlider.Value));
        _powerTimer = Debounce(500, () => _fan!.WriteUInt16Async(AriaFan.AddrPowerMax, (int)PowerSlider.Value));
        _heartTimer = Debounce(500, () => _fan!.WriteEepromAsync(AriaFan.AddrHeartMax, (byte)HeartSlider.Value));
        _powerMinTimer = Debounce(500, () => _fan!.WriteUInt16Async(AriaFan.AddrPowerMin, (int)PowerMinSlider.Value));
        _heartMinTimer = Debounce(500, () => _fan!.WriteEepromAsync(AriaFan.AddrHeartMin, (byte)HeartMinSlider.Value));
    }

    private DispatcherTimer Debounce(int ms, Func<Task> action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (_fan != null) await Run(action);
        };
        return timer;
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    /// <summary>Runs a fan operation and shows any failure in the window.</summary>
    private async Task<bool> Run(Func<Task> action)
    {
        try
        {
            await action();
            ErrorText.Text = "";
            return true;
        }
        catch (TimeoutException)
        {
            ErrorText.Text = "The fan did not answer in time.";
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
        return false;
    }

    // ---- Connection ---------------------------------------------------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_fan != null || (string)ConnectButton.Content == "Cancel")
        {
            await StandbyAndDisconnect();
            return;
        }
        await ConnectAsync();
    }

    /// <summary>Finds the fan, connects and wakes it from standby.</summary>
    private async Task ConnectAsync()
    {

        ConnectButton.IsEnabled = false;
        ConnectionText.Text = "Searching for ARIA…";
        var ok = await Run(async () =>
        {
            var found = await AriaFan.ScanAsync(TimeSpan.FromSeconds(20))
                        ?? throw new InvalidOperationException(
                            "No ARIA fan found. Make sure it is powered on, in range, and not connected to another app.");
            ConnectionText.Text = $"Connecting to {found.Name}…";
            await ConnectTo(found.Address, found.Name);
            await _fan!.SetStandbyAsync(false);
        });

        ConnectButton.IsEnabled = true;
        if (ok && _fan != null)
        {
            ShowConnected();
        }
        else
        {
            var error = ErrorText.Text;
            Disconnect("Not connected");
            ErrorText.Text = error;
        }
    }

    private async Task ConnectTo(ulong address, string name)
    {
        var fan = await AriaFan.ConnectAsync(address, name);
        fan.StatusReceived += s => Dispatcher.BeginInvoke(() => OnStatus(fan, s));
        fan.Disconnected += () => Dispatcher.BeginInvoke(() =>
        {
            if (_fan == fan)
                _ = Reconnect("Connection lost – reconnecting…", IdleHint, TimeSpan.FromMinutes(2));
        });
        _fan = fan;
        _address = address;
        _name = name;
        try
        {
            await LoadSettings();
        }
        catch
        {
            _fan = null;
            fan.Dispose();
            throw;
        }
    }

    private void ShowConnected()
    {
        ConnectionText.Text = $"Connected to {_fan!.Name}";
        ConnectButton.Content = "Disconnect";
        Tabs.IsEnabled = true;
        StandbyButton.IsEnabled = true;
    }

    /// <summary>
    /// Drops the link (if any), then waits for the fan to advertise again and reconnects.
    /// The fan is unreachable while it searches for a sensor, so this only listens until it is back.
    /// </summary>
    private async Task Reconnect(string message, string hint, TimeSpan patience)
    {
        Disconnect(message);
        int session = _session;
        ConnectButton.Content = "Cancel";
        StatusText.Text = hint;

        var deadline = DateTime.UtcNow + patience;
        string lastError = "The fan did not come back in time.";
        while (DateTime.UtcNow < deadline && _session == session)
        {
            try
            {
                var found = await AriaFan.ScanAsync(TimeSpan.FromSeconds(10), _address);
                if (_session != session) return;
                if (found == null) continue;

                await ConnectTo(_address, _name);
                if (_session != session)   // user cancelled meanwhile
                {
                    _fan?.Dispose();
                    _fan = null;
                    return;
                }
                ErrorText.Text = "";
                ShowConnected();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                await Task.Delay(2000);
            }
        }

        if (_session == session)
        {
            Disconnect("Connection lost");
            ErrorText.Text = "Could not reconnect to the fan. " + lastError;
        }
    }

    /// <summary>Puts the fan in standby (if connected), then disconnects.</summary>
    private async Task StandbyAndDisconnect()
    {
        var fan = _fan;
        if (fan != null)
        {
            try
            {
                await fan.SetStandbyAsync(true).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Disconnecting anyway.
            }
        }
        Disconnect("Not connected");
    }

    private void Disconnect(string text)
    {
        _session++;
        _manualTimer.Stop();
        _powerTimer.Stop();
        _heartTimer.Stop();
        _powerMinTimer.Stop();
        _heartMinTimer.Stop();
        _fan?.Dispose();
        _fan = null;
        _status = null;
        Tabs.IsEnabled = false;
        StandbyButton.IsEnabled = false;
        ConnectButton.Content = "Connect";
        ConnectionText.Text = text;
        StatusText.Text = IdleHint;
    }

    private async Task LoadSettings()
    {
        var eeprom = await _fan!.ReadEepromAsync();
        int U16(int a) => eeprom[a] | (eeprom[a + 1] << 8);

        int powerMin = U16(AriaFan.AddrPowerMin);
        int heartMin = eeprom[AriaFan.AddrHeartMin];
        int powerMax = U16(AriaFan.AddrPowerMax);
        int heartMax = eeprom[AriaFan.AddrHeartMax];
        _powerSensorId = U16(AriaFan.AddrPowerAntId);
        _heartSensorId = U16(AriaFan.AddrHeartAntId);
        _bleSensors = eeprom[AriaFan.AddrBleSensors] != 0;

        _updatingUi = true;
        try
        {
            PowerSlider.Maximum = Math.Max(1000, RoundUp(powerMax, 10));
            PowerSlider.Value = powerMax;
            PowerMinSlider.Maximum = Math.Max(500, RoundUp(powerMin, 10));
            PowerMinSlider.Value = powerMin;
            HeartSlider.Maximum = Math.Max(220, heartMax);
            HeartSlider.Value = heartMax;
            HeartMinSlider.Maximum = Math.Max(200, heartMin);
            HeartMinSlider.Value = heartMin;
            BleSensorsBox.IsChecked = eeprom[AriaFan.AddrBleSensors] != 0;
        }
        finally
        {
            _updatingUi = false;
        }
        UpdateScaleTexts();
    }

    private static int RoundUp(int value, int step) => (value + step - 1) / step * step;

    private void UpdateScaleTexts()
    {
        int power = (int)PowerSlider.Value, heart = (int)HeartSlider.Value;
        int powerMin = (int)PowerMinSlider.Value, heartMin = (int)HeartMinSlider.Value;
        PowerValue.Text = $"{power} W";
        HeartValue.Text = $"{heart} bpm";
        PowerMinValue.Text = $"{powerMin} W";
        HeartMinValue.Text = $"{heartMin} bpm";
        PowerHint.Text = $"The fan is off below {powerMin} W, then ramps up and reaches 100 % at {power} W.";
        HeartHint.Text = $"The fan is off below {heartMin} bpm, then ramps up and reaches 100 % at {heart} bpm.";
    }

    private const int PowerGap = 10, HeartGap = 5;   // the upper threshold stays this far above the lower one

    private void PowerMinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || _fan == null) return;
        if (PowerSlider.Value < PowerMinSlider.Value + PowerGap) PowerSlider.Value = PowerMinSlider.Value + PowerGap;
        UpdateScaleTexts();
        Restart(_powerMinTimer);
    }

    private void HeartMinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || _fan == null) return;
        if (HeartSlider.Value < HeartMinSlider.Value + HeartGap) HeartSlider.Value = HeartMinSlider.Value + HeartGap;
        UpdateScaleTexts();
        Restart(_heartMinTimer);
    }

    // ---- Status from the fan ------------------------------------------

    private void OnStatus(AriaFan fan, AriaStatus s)
    {
        if (fan != _fan) return;
        bool sensorJustConnected = s.SensorConnected && _status is { SensorConnected: false };
        _status = s;
        if (sensorJustConnected) _ = RefreshAfterPairing(fan);

        _updatingUi = true;
        try
        {
            int tab = Array.IndexOf(TabModes, s.Mode);
            if (tab >= 0 && Tabs.SelectedIndex != tab) Tabs.SelectedIndex = tab;

            // Follow the fan's own value unless the user is moving the slider right now.
            if (s.Mode == AriaMode.Manual && !ManualSlider.IsMouseCaptureWithin
                && DateTime.UtcNow - _manualTouched > TimeSpan.FromSeconds(1.5))
            {
                ManualSlider.Value = s.Output;
                ManualValue.Text = $"{s.Output} %";
            }
        }
        finally
        {
            _updatingUi = false;
        }

        PowerSensorText.Text = s.Mode == AriaMode.Power
            ? SensorText(s.SensorConnected, AntName(_powerSensorId)) : "Sensor: –";
        HeartSensorText.Text = s.Mode == AriaMode.HeartRate
            ? SensorText(s.SensorConnected, _bleSensors ? "Bluetooth sensor" : AntName(_heartSensorId)) : "Sensor: –";
        StandbyButton.Content = s.Standby ? "Wake up" : "Standby";
        StatusText.Text = $"Mode: {ModeName(s.Mode)}   ·   Fan speed: {s.Output} %" + (s.Standby ? "   ·   Standby" : "");
    }

    private static string? AntName(int id) => id is 0 or 0xFFFF ? null : $"ANT+ ID {id}";

    private static string SensorText(bool connected, string? paired) =>
        connected ? $"Sensor: connected ({paired ?? "unknown"})"
        : paired != null ? $"Sensor: searching for {paired}…"
        : "Sensor: none paired – press Pair";

    /// <summary>Re-reads the fan's memory so the newly paired sensor ID shows up.</summary>
    private async Task RefreshAfterPairing(AriaFan fan)
    {
        await Task.Delay(300);
        if (_fan == fan) await Run(LoadSettings);
    }

    private static string ModeName(AriaMode mode) => mode switch
    {
        AriaMode.Manual => "Manual",
        AriaMode.Speed => "Speed",
        AriaMode.Power => "Power",
        AriaMode.HeartRate => "Heart rate",
        AriaMode.Temperature => "Temperature",
        _ => $"Unknown ({(int)mode})",
    };

    // ---- User actions -------------------------------------------------

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs || _updatingUi || _fan == null || Tabs.SelectedIndex < 0) return;
        await Run(() => _fan.SetModeAsync(TabModes[Tabs.SelectedIndex]));
    }

    private void ManualSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || _fan == null) return;
        ManualValue.Text = $"{(int)ManualSlider.Value} %";
        _manualTouched = DateTime.UtcNow;
        Restart(_manualTimer);
    }

    private void PowerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || _fan == null) return;
        if (PowerMinSlider.Value > PowerSlider.Value - PowerGap) PowerMinSlider.Value = PowerSlider.Value - PowerGap;
        UpdateScaleTexts();
        Restart(_powerTimer);
    }

    private void HeartSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || _fan == null) return;
        if (HeartMinSlider.Value > HeartSlider.Value - HeartGap) HeartMinSlider.Value = HeartSlider.Value - HeartGap;
        UpdateScaleTexts();
        Restart(_heartTimer);
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (_fan == null) return;
        if (!await Run(() => _fan.StartSensorPairingAsync())) return;

        // Like MyETraining: give the fan a second, then let go of the Bluetooth link.
        // The fan searches for the sensor on its own and only becomes reachable again afterwards.
        await Task.Delay(1000);
        if (_fan == null) return;   // the fan already dropped the link; reconnect is under way
        await Reconnect("Fan is searching for the sensor…",
            "Wear the sensor so it transmits and keep it within about 1 m of the fan. This can take up to 2 minutes.",
            TimeSpan.FromMinutes(3));
    }

    private async void BleSensors_Click(object sender, RoutedEventArgs e)
    {
        if (_fan == null) return;
        bool wanted = BleSensorsBox.IsChecked == true;
        if (await Run(() => _fan.WriteEepromAsync(AriaFan.AddrBleSensors, (byte)(wanted ? 1 : 0))))
            _bleSensors = wanted;
        else
            BleSensorsBox.IsChecked = !wanted;
    }

    private async void Standby_Click(object sender, RoutedEventArgs e)
    {
        if (_fan == null) return;
        bool standby = !(_status?.Standby ?? false);
        await Run(() => _fan.SetStandbyAsync(standby));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ConnectAsync();

    private bool _closing;

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing || _fan == null)
        {
            _session++;          // stops a reconnect that may be under way
            _fan?.Dispose();
            return;
        }

        // Hold the window open just long enough to send the fan to standby.
        e.Cancel = true;
        _closing = true;
        IsEnabled = false;
        await StandbyAndDisconnect();
        Close();
    }
}
