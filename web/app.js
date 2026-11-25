import { AriaFan, Addr, Mode, isSupported, requestDevice, sleep } from './aria.js';

const $ = id => document.getElementById(id);
const TAB_MODES = [Mode.Manual, Mode.Power, Mode.HeartRate];
const MODE_NAMES = ['Manual', 'Speed', 'Power', 'Heart rate', 'Temperature'];
const IDLE_HINT = 'Turn the fan on, then press Connect.';
const POWER_GAP = 10, HEART_GAP = 5;   // the upper threshold stays this far above the lower one

/** @type {AriaFan | null} */
let fan = null;
let status = null;
let session = 0;            // bumped on every disconnect; cancels a pending reconnect
let reconnecting = false;
let manualTouched = 0;      // last time the user moved the manual slider
let settings = { powerSensorId: 0, heartSensorId: 0, bleSensors: false };

// ---- Small helpers ----------------------------------------------------

function debounce(ms, action) {
  let timer;
  return () => {
    clearTimeout(timer);
    timer = setTimeout(() => { if (fan?.connected) run(action); }, ms);
  };
}

/** Runs a fan operation and shows any failure on the page. */
async function run(action) {
  try {
    await action();
    $('error').textContent = '';
    return true;
  } catch (e) {
    $('error').textContent = e.message || String(e);
    return false;
  }
}

function setConnectedUi(connected) {
  $('controls').disabled = !connected;
  $('standby').disabled = !connected;
  $('connect').textContent = connected ? 'Disconnect' : reconnecting ? 'Cancel' : 'Connect';
}

// ---- Connection -------------------------------------------------------

$('connect').addEventListener('click', async () => {
  if (fan || reconnecting) {
    dropFan('Not connected', IDLE_HINT);
    return;
  }

  let device;
  try {
    device = await requestDevice();
  } catch (e) {
    if (e.name !== 'NotFoundError') $('error').textContent = e.message;   // NotFoundError = picker cancelled
    return;
  }

  $('connect').disabled = true;
  $('connection').textContent = `Connecting to ${device.name || 'ARIA'}…`;
  const mySession = ++session;
  const candidate = new AriaFan(device);
  const ok = await run(() => attach(candidate));
  $('connect').disabled = false;

  if (ok && session === mySession) {
    showConnected();
  } else {
    const error = $('error').textContent;
    candidate.dispose();
    if (session === mySession) dropFan('Not connected', IDLE_HINT);
    $('error').textContent = error;
  }
});

async function attach(candidate) {
  candidate.onStatus = s => { if (fan === candidate) onStatus(s); };
  candidate.onDisconnected = () => {
    if (fan === candidate && !reconnecting) reconnect('Connection lost – reconnecting…', IDLE_HINT, 120);
  };
  await candidate.connect();
  fan = candidate;
  await loadSettings();
}

function showConnected() {
  $('connection').textContent = `Connected to ${fan.name}`;
  setConnectedUi(true);
}

/** Forgets the fan completely (user pressed Disconnect/Cancel, or reconnecting gave up). */
function dropFan(text, hint) {
  session++;
  reconnecting = false;
  fan?.dispose();
  fan = null;
  status = null;
  $('connection').textContent = text;
  $('status').textContent = hint;
  setConnectedUi(false);
}

/**
 * Lets go of the link (if any) and keeps trying until the fan is reachable again.
 * The fan is unreachable while it searches for a sensor, so this simply retries.
 */
async function reconnect(text, hint, patienceSeconds) {
  const current = fan;
  if (!current) return;
  const mySession = ++session;
  reconnecting = true;
  status = null;
  current.disconnect();
  $('connection').textContent = text;
  $('status').textContent = hint;
  setConnectedUi(false);

  const deadline = Date.now() + patienceSeconds * 1000;
  let lastError = 'The fan did not come back in time.';
  while (Date.now() < deadline && session === mySession) {
    try {
      await current.connect();
      if (session !== mySession) return;
      await loadSettings();
      if (session !== mySession) return;
      reconnecting = false;
      $('error').textContent = '';
      showConnected();
      return;
    } catch (e) {
      lastError = e.message || String(e);
      current.disconnect();
      await sleep(2000);
    }
  }

  if (session === mySession) {
    dropFan('Connection lost', IDLE_HINT);
    $('error').textContent = `Could not reconnect to the fan. ${lastError}`;
  }
}

// ---- Settings stored on the fan ---------------------------------------

async function loadSettings() {
  const eeprom = await fan.readEeprom();
  const u16 = a => eeprom[a] | (eeprom[a + 1] << 8);

  settings = {
    powerSensorId: u16(Addr.powerAntId),
    heartSensorId: u16(Addr.heartAntId),
    bleSensors: eeprom[Addr.bleSensors] !== 0,
  };
  setSlider('powerMax', u16(Addr.powerMax));
  setSlider('powerMin', u16(Addr.powerMin));
  setSlider('heartMax', eeprom[Addr.heartMax]);
  setSlider('heartMin', eeprom[Addr.heartMin]);
  $('bleSensors').checked = settings.bleSensors;
  updateScaleTexts();
  if (status) showSensor(status);
}

/** Sets a slider from a stored value, widening its range if the fan holds something outside it. */
function setSlider(id, value) {
  const slider = $(id);
  if (value > Number(slider.max)) slider.max = value;
  if (value < Number(slider.min)) slider.min = value;
  slider.value = value;
}

function updateScaleTexts() {
  const v = id => Number($(id).value);
  $('powerMaxValue').textContent = `${v('powerMax')} W`;
  $('powerMinValue').textContent = `${v('powerMin')} W`;
  $('heartMaxValue').textContent = `${v('heartMax')} bpm`;
  $('heartMinValue').textContent = `${v('heartMin')} bpm`;
  $('powerHint').textContent =
    `The fan is off below ${v('powerMin')} W, then ramps up and reaches 100 % at ${v('powerMax')} W.`;
  $('heartHint').textContent =
    `The fan is off below ${v('heartMin')} bpm, then ramps up and reaches 100 % at ${v('heartMax')} bpm.`;
}

// ---- Status from the fan ----------------------------------------------

function onStatus(s) {
  const sensorJustConnected = s.sensorConnected && status && !status.sensorConnected;
  status = s;
  if (sensorJustConnected) {
    // Re-read the fan's memory so the newly paired sensor shows up.
    const current = fan;
    setTimeout(() => { if (fan === current && fan.connected) run(loadSettings); }, 300);
  }

  const tab = TAB_MODES.indexOf(s.mode);
  if (tab >= 0) selectTab(tab);

  // Follow the fan's own value unless the user is moving the slider right now.
  if (s.mode === Mode.Manual && Date.now() - manualTouched > 1500) {
    $('manual').value = s.output;
    $('manualValue').textContent = `${s.output} %`;
  }

  showSensor(s);
  $('standby').textContent = s.standby ? 'Wake up' : 'Standby';
  $('status').textContent =
    `Mode: ${MODE_NAMES[s.mode] ?? `Unknown (${s.mode})`}  ·  Fan speed: ${s.output} %` +
    (s.standby ? '  ·  Standby' : '');
}

function showSensor(s) {
  const ant = id => (id === 0 || id === 0xffff ? null : `ANT+ ID ${id}`);
  const text = (connected, paired) =>
    connected ? `Sensor: connected (${paired ?? 'unknown'})`
      : paired ? `Sensor: searching for ${paired}…`
        : 'Sensor: none paired – press Pair';

  $('powerSensor').textContent = s.mode === Mode.Power
    ? text(s.sensorConnected, ant(settings.powerSensorId)) : 'Sensor: –';
  $('heartSensor').textContent = s.mode === Mode.HeartRate
    ? text(s.sensorConnected, settings.bleSensors ? 'Bluetooth sensor' : ant(settings.heartSensorId)) : 'Sensor: –';
}

// ---- Tabs -------------------------------------------------------------

const tabs = [...document.querySelectorAll('[role=tab]')];
const panels = [...document.querySelectorAll('[role=tabpanel]')];

function selectTab(index) {
  tabs.forEach((t, i) => t.setAttribute('aria-selected', String(i === index)));
  panels.forEach((p, i) => { p.hidden = i !== index; });
}

tabs.forEach((tab, index) => tab.addEventListener('click', () => {
  selectTab(index);
  if (fan?.connected) run(() => fan.setMode(TAB_MODES[index]));
}));

// ---- User actions -----------------------------------------------------

const sendManual = debounce(120, () => fan.setAirflow(Number($('manual').value)));
$('manual').addEventListener('input', () => {
  $('manualValue').textContent = `${$('manual').value} %`;
  manualTouched = Date.now();
  sendManual();
});

/** Wires a min/max slider pair: keeps them apart and saves each to the fan after the user stops moving it. */
function wireScale(minId, maxId, gap, saveMin, saveMax) {
  const min = $(minId), max = $(maxId);
  const sendMin = debounce(500, () => saveMin(Number(min.value)));
  const sendMax = debounce(500, () => saveMax(Number(max.value)));
  min.addEventListener('input', () => {
    if (Number(max.value) < Number(min.value) + gap) { max.value = Number(min.value) + gap; sendMax(); }
    updateScaleTexts();
    sendMin();
  });
  max.addEventListener('input', () => {
    if (Number(min.value) > Number(max.value) - gap) { min.value = Number(max.value) - gap; sendMin(); }
    updateScaleTexts();
    sendMax();
  });
}

wireScale('powerMin', 'powerMax', POWER_GAP,
  v => fan.writeUint16(Addr.powerMin, v), v => fan.writeUint16(Addr.powerMax, v));
wireScale('heartMin', 'heartMax', HEART_GAP,
  v => fan.writeByte(Addr.heartMin, v), v => fan.writeByte(Addr.heartMax, v));

async function pair() {
  if (!fan?.connected) return;
  if (!await run(() => fan.startSensorPairing())) return;

  // Like MyETraining: give the fan a second, then let go of the Bluetooth link.
  // The fan searches for the sensor on its own and only becomes reachable again afterwards.
  await sleep(1000);
  if (!fan || reconnecting) return;
  reconnect('Fan is searching for the sensor…',
    'Make sure the sensor is transmitting and within about 1 m of the fan. This can take up to 2 minutes.',
    180);
}
$('pairPower').addEventListener('click', pair);
$('pairHeart').addEventListener('click', pair);

$('bleSensors').addEventListener('change', async () => {
  const wanted = $('bleSensors').checked;
  if (await run(() => fan.writeByte(Addr.bleSensors, wanted ? 1 : 0))) {
    settings.bleSensors = wanted;
    if (status) showSensor(status);
  } else {
    $('bleSensors').checked = !wanted;
  }
});

$('standby').addEventListener('click', () => {
  if (fan?.connected) run(() => fan.setStandby(!(status?.standby ?? false)));
});

// ---- Start-up ---------------------------------------------------------

selectTab(0);
updateScaleTexts();
$('status').textContent = IDLE_HINT;

if (!isSupported()) {
  $('connect').disabled = true;
  $('error').textContent =
    'This browser cannot talk to Bluetooth devices. Use Chrome or Edge on Windows, macOS or Android. ' +
    'iPhone and iPad browsers do not support this.';
}

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('./sw.js').catch(() => {});
}
