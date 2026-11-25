using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;

namespace EliteFan;

public enum AriaMode : byte { Manual = 0, Speed = 1, Power = 2, HeartRate = 3, Temperature = 4 }

public readonly record struct AriaStatus(AriaMode Mode, int Output, bool SensorConnected, bool Standby);

/// <summary>
/// Bluetooth LE client for the Elite Aria fan. Protocol recovered from MyETraining
/// (Elite.Real.Devices.BLE.BleAriaSensor / AriaManager).
/// </summary>
public sealed class AriaFan : IDisposable
{
    private static Guid EliteUuid(string id) => new($"347b{id}-7635-408b-8918-8ff3949ce592");

    private static readonly Guid ServiceUuid = EliteUuid("0001");
    private static readonly Guid ControlPointUuid = EliteUuid("0040");
    private static readonly Guid StatusUuid = EliteUuid("0042");
    private static readonly Guid EepromWriteUuid = EliteUuid("0012");
    private static readonly Guid EepromReadUuid = EliteUuid("0013");
    private static readonly Guid EepromResultUuid = EliteUuid("0014");

    // Control point commands
    private const byte CmdPairing = 2, CmdWorkingOutput = 3, CmdStandby = 4, CmdMode = 5;

    // EEPROM protocol
    private const int EepromRead = 2, EepromWriteOk = 3, EepromNack = 0xFE;
    public const int EepromSize = 63;

    // EEPROM addresses
    public const int AddrPowerMin = 26, AddrPowerMax = 28;   // 2 bytes each, watts
    public const int AddrHeartMin = 31, AddrHeartMax = 32;   // 1 byte each, bpm
    public const int AddrBleSensors = 45;                    // 1 byte, 0/1
    public const int AddrHeartAntId = 10, AddrPowerAntId = 16; // 2 bytes each, paired ANT+ device number

    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _control, _status, _eepromWrite, _eepromRead, _eepromResult;
    private readonly SemaphoreSlim _eepromLock = new(1, 1);
    private TaskCompletionSource<int>? _eepromResultTcs;

    public string Name { get; }
    public event Action<AriaStatus>? StatusReceived;
    public event Action? Disconnected;

    private AriaFan(string name, BluetoothLEDevice device, GattDeviceService service,
        GattCharacteristic control, GattCharacteristic status,
        GattCharacteristic eepromWrite, GattCharacteristic eepromRead, GattCharacteristic eepromResult)
    {
        Name = name;
        _device = device;
        _service = service;
        _control = control;
        _status = status;
        _eepromWrite = eepromWrite;
        _eepromRead = eepromRead;
        _eepromResult = eepromResult;
    }

    /// <summary>
    /// Scans for the first advertising device whose name starts with "ARIA",
    /// or, when an address is given, for any advertisement from that device.
    /// </summary>
    public static async Task<(ulong Address, string Name)?> ScanAsync(TimeSpan timeout, ulong? address = null, CancellationToken ct = default)
    {
        var found = new TaskCompletionSource<(ulong, string)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var name = e.Advertisement.LocalName ?? "";
            if (address is ulong wanted
                    ? e.BluetoothAddress == wanted
                    : name.StartsWith("ARIA", StringComparison.OrdinalIgnoreCase))
                found.TrySetResult((e.BluetoothAddress, name));
        };
        watcher.Stopped += (_, e) =>
        {
            if (e.Error != BluetoothError.Success)
                found.TrySetException(new IOException($"Bluetooth scan failed: {e.Error}. Is Bluetooth turned on?"));
        };
        watcher.Start();
        try
        {
            var done = await Task.WhenAny(found.Task, Task.Delay(timeout, ct));
            return done == found.Task ? await found.Task : null;
        }
        finally
        {
            watcher.Stop();
        }
    }

    public static async Task<AriaFan> ConnectAsync(ulong address, string name)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                     ?? throw new IOException("Could not open the Bluetooth device.");
        try
        {
            var services = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new IOException($"Elite service not found ({services.Status}). Close MyETraining or other apps connected to the fan.");
            var service = services.Services[0];

            var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (chars.Status != GattCommunicationStatus.Success)
                throw new IOException($"Could not read characteristics ({chars.Status}).");

            GattCharacteristic Find(Guid uuid) =>
                chars.Characteristics.FirstOrDefault(c => c.Uuid == uuid)
                ?? throw new IOException($"Characteristic {uuid} not found on the fan.");

            var fan = new AriaFan(name, device, service,
                Find(ControlPointUuid), Find(StatusUuid),
                Find(EepromWriteUuid), Find(EepromReadUuid), Find(EepromResultUuid));

            fan._status.ValueChanged += fan.OnStatus;
            fan._eepromResult.ValueChanged += fan.OnEepromResult;
            await Subscribe(fan._status);
            await Subscribe(fan._eepromResult);
            device.ConnectionStatusChanged += fan.OnConnectionStatusChanged;
            return fan;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static async Task Subscribe(GattCharacteristic c)
    {
        var value = c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        var status = await c.WriteClientCharacteristicConfigurationDescriptorAsync(value);
        if (status != GattCommunicationStatus.Success)
            throw new IOException($"Could not subscribe to {c.Uuid} ({status}).");
    }

    // ---- Live control -------------------------------------------------

    /// <summary>Sets the airflow in percent (0-100). Only effective in manual mode.</summary>
    public Task SetAirflowAsync(int percent) =>
        WriteAsync(_control, CmdWorkingOutput, 1, (byte)Math.Clamp(percent, 0, 100));

    public Task SetModeAsync(AriaMode mode) => WriteAsync(_control, CmdMode, (byte)mode);

    public Task SetStandbyAsync(bool standby) => WriteAsync(_control, CmdStandby, (byte)(standby ? 1 : 0));

    /// <summary>Makes the fan search for and pair with the nearest sensor for its current mode.</summary>
    public Task StartSensorPairingAsync() => WriteAsync(_control, CmdPairing, 1);

    // ---- Settings (EEPROM) --------------------------------------------

    public async Task<byte[]> ReadEepromAsync(int address = 0, int count = EepromSize)
    {
        await _eepromLock.WaitAsync();
        try
        {
            var result = await EepromRequestAsync(
                [EepromRead, (byte)(address % 256), (byte)(address / 256), (byte)(count % 256), (byte)(count / 256)]);
            if (result != EepromRead)
                throw new IOException($"Fan rejected the settings read (code {result}).");

            var data = await ReadAsync(_eepromRead);
            if (data.Length < 5 + count)
                throw new IOException($"Settings read returned {data.Length} bytes, expected {5 + count}.");
            return data[5..(5 + count)];
        }
        finally
        {
            _eepromLock.Release();
        }
    }

    public async Task WriteEepromAsync(int address, params byte[] values)
    {
        await _eepromLock.WaitAsync();
        try
        {
            byte[] request =
            [
                3, (byte)(address % 256), (byte)(address / 256),
                (byte)(values.Length % 256), (byte)(values.Length / 256), .. values
            ];
            var result = await EepromRequestAsync(request);
            if (result != EepromWriteOk)
                throw new IOException(result == EepromNack
                    ? "Fan rejected the setting (NACK)."
                    : $"Unexpected reply to settings write (code {result}).");
        }
        finally
        {
            _eepromLock.Release();
        }
    }

    public Task WriteUInt16Async(int address, int value) =>
        WriteEepromAsync(address, (byte)(value % 256), (byte)(value / 256));

    private async Task<int> EepromRequestAsync(byte[] request)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _eepromResultTcs = tcs;
        await WriteAsync(_eepromWrite, request);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- Notifications ------------------------------------------------

    private void OnStatus(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        CryptographicBuffer.CopyToByteArray(e.CharacteristicValue, out byte[] d);
        if (d is null || d.Length < 4) return;
        StatusReceived?.Invoke(new AriaStatus((AriaMode)d[0], d[1], d[2] != 0, (d[3] & 1) != 0));
    }

    private void OnEepromResult(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        CryptographicBuffer.CopyToByteArray(e.CharacteristicValue, out byte[] d);
        if (d is null || d.Length == 0) return;
        int code = d.Length >= 2 ? d[0] | (d[1] << 8) : d[0];
        _eepromResultTcs?.TrySetResult(code);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            Disconnected?.Invoke();
    }

    // ---- GATT helpers -------------------------------------------------

    private static async Task WriteAsync(GattCharacteristic c, params byte[] data)
    {
        var option = c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;
        var status = await c.WriteValueAsync(CryptographicBuffer.CreateFromByteArray(data), option);
        if (status != GattCommunicationStatus.Success)
            throw new IOException($"Write to the fan failed ({status}).");
    }

    private static async Task<byte[]> ReadAsync(GattCharacteristic c)
    {
        var result = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
            throw new IOException($"Read from the fan failed ({result.Status}).");
        CryptographicBuffer.CopyToByteArray(result.Value, out byte[] data);
        return data ?? [];
    }

    public void Dispose()
    {
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _status.ValueChanged -= OnStatus;
        _eepromResult.ValueChanged -= OnEepromResult;
        _service.Dispose();
        _device.Dispose();
    }
}
