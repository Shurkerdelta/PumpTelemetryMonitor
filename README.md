# Pump Telemetry Monitor

A .NET 10 console app that simulates a small fleet of frac pumps, streams their telemetry through a processing
pipeline, and raises alerts. This document explains how the **simulated telemetry**, **concurrent processing**
and **alert detection** parts are implemented.

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

[Concurrent processing](#concurrent-processing) explains how the pipeline does this.

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

## Concurrent processing

### Requirement

A processing pipeline receives telemetry from all devices concurrently, processes readings without blocking the
devices that produce them, and shuts down gracefully. It is built from .NET concurrency primitives.

### Where the code lives

| Piece | File | Role |
|---|---|---|
| Pipeline | [TelemetryPipeline.cs](src/PumpMonitor.Core/Processing/TelemetryPipeline.cs) | Bounded channel, single consumer, drain on stop |
| Handler | [ITelemetryHandler.cs](src/PumpMonitor.Core/Processing/ITelemetryHandler.cs) | One processing step, run for every reading |
| Handlers in use | [ConsoleLogHandler.cs](src/PumpMonitor.App/ConsoleLogHandler.cs), [AlertMonitor.cs](src/PumpMonitor.Core/Alerts/AlertMonitor.cs) | Print the reading; check it for alerts |
| Console | [ConsoleUi.cs](src/PumpMonitor.App/ConsoleUi.cs) | Serialises output from every thread under one lock |
| Wiring and shutdown | [Program.cs](src/PumpMonitor.App/Program.cs) | Starts the producers and the consumer, and stops them in order |

### What runs at the same time

| Task | Started by | Does |
|---|---|---|
| One producer per device | `RunDeviceAsync` → `SimulatedDevice.RunAsync` | Takes readings and writes them to the pipeline |
| The consumer | `TelemetryPipeline.Start` (`Task.Run`) | Reads the queue and runs each handler in turn |
| The silence watch | `SilenceMonitor.RunAsync` | Checks for quiet devices every 500 ms |
| The command loop | `ReadCommandsAsync` | Reads operator input and applies commands to the devices |

All of them are async tasks on the thread pool, so none needs a thread of its own while it waits. The one
exception is the command loop's blocking `ReadLine`, which holds a pool thread until the operator presses Enter.

### .NET primitives used

| Primitive | Where | Why |
|---|---|---|
| `Channel<T>` (`Channel.CreateBounded`) | `TelemetryPipeline` | Thread-safe queue from many producers to one consumer, with a built-in policy for when it is full |
| `Task`, `Task.Run`, `async`/`await` | Producers, consumer, silence watch, command loop | Run concurrently without blocking a thread while waiting |
| `CancellationTokenSource` / `CancellationToken` | `Program.cs`, `TelemetryPipeline` | Stop the devices and the silence watch; put a time limit on the drain; abandon processing when it runs out |
| `Task.WhenAll` / `Task.WhenAny` | `Program.cs` | Wait for every producer before closing the queue; race blocking console input against shutdown |
| `Lock` (`System.Threading.Lock`) | `TelemetryPipeline` start/stop, `SilenceMonitor`, `SimulatedDevice` faults, `ConsoleUi` | Guard state that more than one thread reads and writes |
| `Interlocked` | `TelemetryPipeline.DroppedCount` | Count dropped readings without a lock |
| `volatile` | `SimulatedDevice` on/off state, `Toggle` | Flags set by the command loop and read by the devices or the consumer |
| `PeriodicTimer` | `SilenceMonitor.RunAsync` | Silence check every 500 ms. The next wait starts only after a check returns, so checks never overlap |

### Receiving concurrently

- **One writer for everyone:** every device writes to `TelemetryPipeline.Writer` at the same time, and the
  channel handles the concurrent writes with no locking in the devices. Readings from one device stay in the
  order that device wrote them.
- **Producers can't close it:** devices get a wrapper (`ProducerWriter`) that refuses to complete the queue:
  `TryComplete` returns false and `Complete` throws. Only `StopAsync` closes it, so one device can't cut off the
  others, or complete the queue with an error that would crash the consumer before the drain.
- **Exactly one consumer:** `Start` checks and starts under a lock, so even concurrent calls start a single
  consumer. Readings written before `Start` are queued, not lost.

### Never blocking producers

- **Bounded, drop oldest:** the queue holds 100 readings, 6–10 s of telemetry at normal rates. When it is full,
  a write still succeeds immediately and the oldest queued reading is dropped instead. Devices never wait on
  processing, and memory stays bounded. `DroppedCount` records how many were dropped, and the app prints it at
  shutdown.
- **Handlers never run on a producer's thread:** a write that wakes the idle consumer only schedules it on the
  thread pool (`AllowSynchronousContinuations = false`). Otherwise the device's own thread would run every
  handler, and would block on a slow console write.
- **Short locks on the write path:** the only lock a write takes is `SilenceMonitor`'s, held just long enough to
  record when the device was last heard from. Alerts and console output happen outside it.

### One consumer

- **Arrival order:** the consumer processes one reading at a time, in the order they were queued. So handlers
  need no locks: `AlertMonitor` keeps each device's last sequence number in a plain `Dictionary`.
- **Failures are contained:** a handler that throws is reported through the pipeline's error callback, and
  processing carries on with the next handler and reading. An exception from the error callback itself is
  swallowed, because a dead consumer would leave the queue unread until shutdown.
- **Cancellation:** handlers get a cancellation token, which fires only if the shutdown drain runs out of time.

### Graceful shutdown

`quit`, the end of input, and the first Ctrl+C all run the same sequence in `Program.cs`:

1. **Clear the prompt:** `ui.Close()` removes the input prompt, so final messages print as plain lines.
2. **Stop the producers:** cancelling the shutdown token ends every device loop and the silence watch. For them
   cancellation is a normal exit, not an error.
3. **Wait for them:** `Task.WhenAll` waits until no producer can write again. `RunDeviceAsync` reports a device
   that crashed and keeps its exception out of this wait, which would otherwise skip the drain.
4. **Drain:** `pipeline.StopAsync` closes the queue, so any further write fails, and waits while the consumer
   processes everything still queued.
5. **Time limit:** the drain gets 5 s. If it runs out, the in-flight handler's token is cancelled, no further
   handlers run, and the rest of the queue is discarded.
6. **Report:** the number of dropped readings, if any, then "All devices stopped."

Supporting details:

- **Input doesn't hold up shutdown:** `ReadLine` can't be cancelled, so the command loop runs it with `Task.Run`
  and races it against shutdown with `Task.WhenAny`. Ctrl+C doesn't wait for a line of input.
- **Ctrl+C:** the handler sets `e.Cancel = true` so the first press runs this sequence instead of killing the
  process. Once shutdown has started, Ctrl+C kills the process as usual, in case something hangs.
- **Safe to stop twice:** the pipeline is declared with `await using`, and `StopAsync` can be called more than
  once, so disposing it after the drain does nothing.

Known limit: `StopAsync` still waits for the in-flight handler to return. A handler that ignores its token (for
example a console write blocked by a paused terminal) can hold shutdown past the 5 s limit. A second Ctrl+C ends
the process.

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

The pipeline tests are in [tests/PumpMonitor.Tests/Processing](tests/PumpMonitor.Tests/Processing). They cover:

- four concurrent producers, with every reading delivered in per-device order
- writes that complete immediately while a handler is stalled, with the oldest readings dropped and counted
- a handler that blocks its thread never running on the producer's thread
- concurrent `Start` calls starting exactly one consumer
- producers that can't complete the pipeline
- `StopAsync` draining queued readings and rejecting new ones
- a drain that runs out of time abandoning the rest, even when a handler ignores cancellation
- a throwing handler or error callback that doesn't stop processing
- readings written before `Start` still being processed

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
