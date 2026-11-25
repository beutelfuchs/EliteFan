// Bluetooth LE client for the Elite Aria fan (Web Bluetooth).
// Protocol recovered from MyETraining; same as AriaFan.cs in the Windows app.

const uuid = id => `347b${id}-7635-408b-8918-8ff3949ce592`;

const SERVICE = uuid('0001');
const CONTROL_POINT = uuid('0040');
const STATUS = uuid('0042');
const EEPROM_WRITE = uuid('0012');
const EEPROM_READ = uuid('0013');
const EEPROM_RESULT = uuid('0014');

export const Mode = { Manual: 0, Speed: 1, Power: 2, HeartRate: 3, Temperature: 4 };

// EEPROM addresses
export const Addr = {
  heartAntId: 10,   // 2 bytes, paired ANT+ device number
  powerAntId: 16,   // 2 bytes
  powerMin: 26,     // 2 bytes, watts
  powerMax: 28,     // 2 bytes, watts
  heartMin: 31,     // 1 byte, bpm
  heartMax: 32,     // 1 byte, bpm
  bleSensors: 45,   // 1 byte, 0/1
};
const EEPROM_SIZE = 63;
const EE_READ = 2, EE_WRITE_OK = 3, EE_NACK = 0xfe;

export const isSupported = () => 'bluetooth' in navigator;

/** Shows the browser's device picker. Must be called from a click/tap. */
export function requestDevice() {
  return navigator.bluetooth.requestDevice({
    filters: [{ namePrefix: 'ARIA' }],
    optionalServices: [SERVICE],
  });
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

function withTimeout(promise, ms, message) {
  let timer;
  const timeout = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

export class AriaFan {
  /** @param {BluetoothDevice} device */
  constructor(device) {
    this.device = device;
    this.onStatus = () => {};
    this.onDisconnected = () => {};
    this._queue = Promise.resolve();   // Web Bluetooth allows one GATT operation at a time
    this._eepromResolve = null;
    this._chars = null;
    this._onGattDisconnected = () => { this._chars = null; this.onDisconnected(); };
    device.addEventListener('gattserverdisconnected', this._onGattDisconnected);
  }

  get name() { return this.device.name || 'ARIA'; }
  get connected() { return !!this._chars && this.device.gatt.connected; }

  async connect() {
    const server = await withTimeout(this.device.gatt.connect(), 20000, 'The fan did not answer.');
    const service = await server.getPrimaryService(SERVICE);
    const get = id => service.getCharacteristic(id);
    const chars = {
      control: await get(CONTROL_POINT),
      status: await get(STATUS),
      eepromWrite: await get(EEPROM_WRITE),
      eepromRead: await get(EEPROM_READ),
      eepromResult: await get(EEPROM_RESULT),
    };

    chars.status.addEventListener('characteristicvaluechanged', e => {
      const v = e.target.value;
      if (v.byteLength < 4) return;
      this.onStatus({
        mode: v.getUint8(0),
        output: v.getUint8(1),
        sensorConnected: v.getUint8(2) !== 0,
        standby: (v.getUint8(3) & 1) !== 0,
      });
    });
    chars.eepromResult.addEventListener('characteristicvaluechanged', e => {
      const v = e.target.value;
      if (v.byteLength === 0 || !this._eepromResolve) return;
      this._eepromResolve(v.byteLength >= 2 ? v.getUint16(0, true) : v.getUint8(0));
      this._eepromResolve = null;
    });
    await chars.status.startNotifications();
    await chars.eepromResult.startNotifications();

    this._queue = Promise.resolve();
    this._chars = chars;
  }

  /** Drops the link without forgetting the device. */
  disconnect() {
    this._chars = null;
    if (this.device.gatt.connected) this.device.gatt.disconnect();
  }

  /** Disconnects for good and stops listening to the device. */
  dispose() {
    this.device.removeEventListener('gattserverdisconnected', this._onGattDisconnected);
    this.disconnect();
  }

  // ---- Live control ---------------------------------------------------

  /** Airflow in percent (0-100). Only effective in manual mode. */
  setAirflow(percent) {
    return this._write('control', [3, 1, Math.max(0, Math.min(100, Math.round(percent)))]);
  }

  setMode(mode) { return this._write('control', [5, mode]); }

  setStandby(standby) { return this._write('control', [4, standby ? 1 : 0]); }

  /** Makes the fan search for and pair with the nearest sensor for its current mode. */
  startSensorPairing() { return this._write('control', [2, 1]); }

  // ---- Settings (EEPROM) ----------------------------------------------

  /** Reads the fan's whole settings memory. */
  readEeprom(address = 0, count = EEPROM_SIZE) {
    return this._enqueue(async chars => {
      const result = await this._eepromRequest(chars,
        [EE_READ, address & 0xff, address >> 8, count & 0xff, count >> 8]);
      if (result !== EE_READ) throw new Error(`Fan rejected the settings read (code ${result}).`);
      const v = await chars.eepromRead.readValue();
      if (v.byteLength < 5 + count) {
        throw new Error(`Settings read returned ${v.byteLength} bytes, expected ${5 + count}.`);
      }
      return new Uint8Array(v.buffer, v.byteOffset + 5, count).slice();
    });
  }

  writeEeprom(address, values) {
    return this._enqueue(async chars => {
      const result = await this._eepromRequest(chars,
        [3, address & 0xff, address >> 8, values.length & 0xff, values.length >> 8, ...values]);
      if (result === EE_NACK) throw new Error('Fan rejected the setting.');
      if (result !== EE_WRITE_OK) throw new Error(`Unexpected reply to settings write (code ${result}).`);
    });
  }

  writeByte(address, value) { return this.writeEeprom(address, [value & 0xff]); }
  writeUint16(address, value) { return this.writeEeprom(address, [value & 0xff, (value >> 8) & 0xff]); }

  async _eepromRequest(chars, bytes) {
    const reply = new Promise(resolve => { this._eepromResolve = resolve; });
    await writeChar(chars.eepromWrite, bytes);
    return withTimeout(reply, 5000, 'The fan did not answer in time.');
  }

  // ---- GATT helpers ---------------------------------------------------

  _write(name, bytes) {
    return this._enqueue(chars => writeChar(chars[name], bytes));
  }

  /** Runs GATT operations strictly one after another. */
  _enqueue(operation) {
    const run = () => {
      if (!this._chars) throw new Error('Not connected to the fan.');
      return operation(this._chars);
    };
    const result = this._queue.then(run, run);
    this._queue = result.catch(() => {});
    return result;
  }
}

function writeChar(characteristic, bytes) {
  const data = Uint8Array.from(bytes);
  return characteristic.properties.write
    ? characteristic.writeValueWithResponse(data)
    : characteristic.writeValueWithoutResponse(data);
}

export { sleep };
