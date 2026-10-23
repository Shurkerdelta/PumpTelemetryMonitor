# Pump Telemetry Monitor

A .NET 10 console app that simulates a small fleet of frac pumps, streams their telemetry through a processing
pipeline, and raises alerts. This document explains how the **simulated telemetry** and **alert detection**
parts are implemented.

## Requirement

The program creates 3–5 simulated devices. Each one emits readings asynchronously every 100–500 ms, and every
reading contains `DeviceId`, `Timestamp`, `Pressure`, `Temperature` and `SequenceNumber`.

## Running it

```bash
dotnet run --project src/PumpMonitor.App -- --show-readings
```

`--show-readings` (or `-v`) prints every reading. Without it, only alerts are shown, and `readings on` turns
readings on while the app is running. Type `help` for the commands, and `quit` or Ctrl+C to stop.

## Where the code lives

| Piece | File | Role |
|---|---|---|
| Reading | [TelemetryReading.cs](src/PumpMonitor.Core/Telemetry/TelemetryReading.cs) | Immutable record holding the five fields |
| Fleet | [DeviceFleet.cs](src/PumpMonitor.Core/Devices/DeviceFleet.cs) | Creates 3–5 devices named `pump-1`…`pump-n` |
| Device | [SimulatedDevice.cs](src/PumpMonitor.Core/Devices/SimulatedDevice.cs) | Owns timing, sequence numbers, on/off state and fault injection |
| Device settings | [SimulatedDeviceOptions.cs](src/PumpMonitor.Core/Devices/SimulatedDeviceOptions.cs) | Reading interval, timer slack, fault sizes |
| Physics | [PumpProcessModel.cs](src/PumpMonitor.Core/Devices/PumpProcessModel.cs) | Produces realistic pressure and temperature values |
| Physics settings | [PumpProcessOptions.cs](src/PumpMonitor.Core/Devices/PumpProcessOptions.cs) | Setpoints, warm-up speed, noise |
| Wiring | [Program.cs](src/PumpMonitor.App/Program.cs) | Starts one producer task per device, all writing into one pipeline |

## The reading

```csharp
public sealed record TelemetryReading(
    string DeviceId,           // "pump-1", "pump-2", ...
    DateTimeOffset Timestamp,  // UTC, when the sample was taken
    double Pressure,           // discharge pressure, MPa
    double Temperature,        // power-end lube oil temperature, °C
    long SequenceNumber);      // per device, starts at 1, +1 per reading
```

It is a record, so readings are immutable and safe to pass between threads, and tests can compare them by value.

## Creating 3–5 devices

`DeviceFleet.Create()` picks a count with `Random.Next(3, 6)` (the upper bound is exclusive, so the result is
3, 4 or 5) and names the devices `pump-1` to `pump-n`. Each device gets its own `Random`, because `Random` is
not thread-safe and the devices run at the same time. The method accepts an optional seeded `Random` so tests
can check the count deterministically.

## Emitting asynchronously

`Program.cs` starts one task per device with `SimulatedDevice.RunAsync`. All of them write into a single
`System.Threading.Channels` channel owned by `TelemetryPipeline`:

```
pump-1 ─┐
pump-2 ─┼─► bounded channel (100, drop oldest) ─► single consumer ─► console log, alert monitor
pump-n ─┘
```

- Devices never wait on each other or on processing. The channel is bounded and drops the oldest reading when
  full, so a slow consumer costs stale data, not stalled devices.
- A single consumer runs the handlers, so readings are processed in arrival order and handlers need no locks.
- On shutdown, the producers are cancelled first and then the pipeline drains what is already queued.

Each device loop is:

1. Read the clock once (`now`).
2. Take a reading stamped with `now`, give it the next sequence number, and write it to the channel.
3. Wait until `now + NextInterval()`, then repeat.

Cancellation ends the loop normally, without throwing.

## Keeping gaps within 100–500 ms

Each device waits a random amount of time between readings. Two details keep the actual gaps inside the
range, measured on the real clock:

- **Timed from the reading, not from after delivery.** The wait counts from when the previous reading was taken,
  so time spent writing it comes out of the wait instead of being added to it.
- **Allowance for OS timer imprecision.** On Windows, `Task.Delay` can wake up to about 16 ms late (the timer
  ticks every 15.6 ms) and occasionally about 1 ms early. To handle this:
  - **Never early:** `WaitUntilAsync` checks the clock after waking and waits out any remainder, so a reading is
    never taken before it is due.
  - **Room for late:** `TimerSlack` (default 20 ms) is reserved below `MaxInterval`, so the target gap is drawn
    from 100–480 ms and a late wake-up still lands under 500 ms.
  - **Whole milliseconds:** `Task.Delay` drops fractions of a millisecond, so each wait is rounded up. Otherwise
    a sub-millisecond remainder would become `Delay(0)` and spin.

The defaults live in `SimulatedDeviceOptions`: `MinInterval` 100 ms, `MaxInterval` 500 ms, `TimerSlack` 20 ms.
In a measurement of 891 real gaps across six runs, every gap was between 104 and 493 ms. No timer gives a hard
guarantee on a heavily loaded machine, but the slack covers normal Windows timer behaviour.

## Pressure and temperature values

The values are not plain random numbers. `PumpProcessModel` simulates a frac pump:

- Each value moves toward a target: running (55 MPa, 70 °C) when the pump is on, idle (0.3 MPa, 20 °C) when it
  is off. Every pump starts at idle and warms up over time.
- The move is exponential, with time constants of 5 s for pressure and 30 s for temperature, so temperature
  visibly lags behind pressure.
- Gaussian noise is added on top of the curve. Pressure noise is a percentage of the current pressure, so it
  shrinks when the pump is off. Pressure is never allowed below zero.
- Noise is scaled by the square root of the time since the last reading (an Ornstein–Uhlenbeck process), so the
  curve looks the same whatever the reading interval.

`SimulatedDevice` passes the model the on/off state and the elapsed time, and takes the values it returns. Tests
replace the model with `StubPumpProcess` through the `IPumpProcess` interface.

## Operator commands that affect telemetry

| Command | Effect |
|---|---|
| `on [id...]` / `off [id...]` | Start or stop pumps. An off pump keeps reporting while it cools down to idle. |
| `fault spike [id...]` | Pressure reads above the alert threshold for 3 s |
| `fault overheat [id...]` | Temperature reads above the alert threshold for 3 s |
| `fault silence [id...]` | The device sends nothing for the silence timeout plus 2 s. No sequence numbers are skipped. |
| `fault reorder [id...]` | One reading is held back and sent after the next one |

Faults are sized from the current alert thresholds, so each one is guaranteed to trigger its alert. Silence and
reorder deliberately break the normal cadence and order. Without them, every device reports in order every
100–500 ms.

## Alert detection

### Requirement

Alerts are generated and printed to the console when pressure is above a threshold, when a sequence number arrives
out of order, and when a device is silent for more than 5 seconds.

### Where the code lives

| Piece | File | Role |
|---|---|---|
| Alert | [Alert.cs](src/PumpMonitor.Core/Alerts/Alert.cs) | Record of device, type, message and detection time |
| Thresholds | [AlertOptions.cs](src/PumpMonitor.Core/Alerts/AlertOptions.cs) | Pressure 69 MPa, temperature 90 °C, silence 5 s; validated at startup |
| Reading checks | [AlertMonitor.cs](src/PumpMonitor.Core/Alerts/AlertMonitor.cs) | Pipeline handler: pressure, temperature and sequence checks |
| Silence | [SilenceMonitor.cs](src/PumpMonitor.Core/Alerts/SilenceMonitor.cs) | Timer-driven check for devices that stopped reporting |
| Delivery | [AlertDelivery.cs](src/PumpMonitor.Core/Alerts/AlertDelivery.cs) | Delivers every alert even if one callback call fails |
| Printing | [Program.cs](src/PumpMonitor.App/Program.cs) | One callback prints every alert to the console |

### Two monitors

Alerts that depend on a reading's contents can be checked when the reading arrives. Silence can't: a silent
device sends nothing to react to. So detection is split in two:

```
devices ─► SilenceMonitor.Track ─► pipeline ─► AlertMonitor ─┐
                │                                            ├─► printAlert ─► console
                └─ last heard from ◄── timer (every 0.5 s) ──┘
```

- **`AlertMonitor`** is an `ITelemetryHandler` in the pipeline. It runs on the single consumer, so it keeps its
  per-device state without locks.
- **`SilenceMonitor`** wraps the pipeline's writer. A device counts as heard from when its reading is *queued*,
  not when it is processed, so a backed-up consumer (for example a console paused by a text selection) isn't
  mistaken for silent devices. `RunAsync` checks every device on a 500 ms timer. Device threads and the timer
  share the state under a lock.

### High pressure

Every reading with `Pressure > PressureThreshold` (69 MPa, about 10,000 psi) raises `HighPressure`, so a
sustained spike alerts on each reading, not just once. A reading exactly at the threshold doesn't alert.
Temperature works the same way against 90 °C. `AlertOptions.Validate` rejects NaN and infinite thresholds,
because no reading is ever above NaN and that alert would silently switch off.

### Out-of-order sequence numbers

`AlertMonitor` remembers the highest sequence number seen from each device. A reading whose number is at or
below it raises `OutOfOrderSequence` ("Sequence #10 arrived after #11"), which also catches duplicates. A late
reading doesn't lower the high-water mark, so the readings after it aren't flagged as well. Gaps are not alerts,
because the pipeline may drop readings when it falls behind. Each device is tracked separately.

### Device silent for more than 5 seconds

Each check raises `DeviceSilent` for any device quiet for longer than `SilenceTimeout` (5 s). Because checks run
every 500 ms, the alert fires 5–5.5 s after the last reading.

- **Once per silence:** a device is reported once, then re-armed when it is heard from again.
- **Never reported at all:** every device is registered at startup, so one that never sends a reading is still
  flagged.
- **Steady clock:** quiet time is measured with `TimeProvider.GetTimestamp()`, not the wall clock. A system clock
  change (an NTP step or a manual adjustment) would otherwise report every device silent at once when it jumps
  forward, or hide a real silence when it jumps back.
- **Shutdown:** the silence watch stops with the devices, so stopped devices aren't reported as silent.

### Printing and failures

Both monitors call the same `printAlert` callback, which writes a line through `ConsoleUi` above the input
prompt:

```
!! ALERT 20:10:42.274  pump-1  HighPressure: Pressure 74.00 MPa exceeds 69.00 MPa
!! ALERT 20:10:43.623  pump-2  OutOfOrderSequence: Sequence #10 arrived after #11
!! ALERT 20:10:49.311  pump-3  DeviceSilent: No readings for 5.1s (limit 5.0s)
```

Alerts are printed even when readings are hidden. A single reading can raise several alerts (for example out of
order *and* over pressure). `AlertDelivery` hands over every one of them even if the callback throws for one,
then reports the failures together, so a failed console write can't lose the alerts behind it. A failed silence
check is reported and the timer keeps running.

Known trade-off: the pipeline drops its oldest queued readings when it falls far behind, and a dropped reading is
never checked. With a 100-reading queue this needs a stall of 6–10 s.

### Trying it

Inject one fault per alert type while the app runs:

```
fault spike pump-1
fault reorder pump-2
fault silence pump-3
```

`status` shows the thresholds in use.

## Testability

- **Fake clock:** every time-dependent class accepts a `TimeProvider`, so tests use `FakeTimeProvider` and run
  in milliseconds instead of real time.
- **Seeded randomness:** devices and the fleet accept a `Random`, so tests can pin the exact draw (for example
  the lowest, middle and highest gap).
- **Step-by-step readings:** `TakeReading()` is public, so tests can take readings one at a time without running
  the loop.

The relevant tests are in [tests/PumpMonitor.Tests/Devices](tests/PumpMonitor.Tests/Devices). They cover:

- the 100/500 ms defaults and the exact wait at each end of the range
- that delivery time comes out of the wait
- contiguous, ordered sequence numbers from concurrent devices sharing a channel
- the 3–5 device count and the `pump-n` naming

The alert tests are in [tests/PumpMonitor.Tests/Alerts](tests/PumpMonitor.Tests/Alerts). They cover:

- alerts on every reading above a threshold, and none exactly at it
- out-of-order and duplicate sequence numbers, per device, with gaps allowed
- silence after more than 5 s, once per silence, for devices that never report, and with a stalled queue
- system clock steps forward and back that don't change measured silence
- a failing alert callback that doesn't lose the other alerts
- every injected fault raising its matching alert

```bash
dotnet test
```
