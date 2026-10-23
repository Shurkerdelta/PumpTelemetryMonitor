# Pump Telemetry Monitor

A .NET 10 console app that simulates a fleet of 3–5 frac pumps. It streams their telemetry through a concurrent
processing pipeline and prints alerts for high pressure, out-of-order readings, invalid values and silent devices.
Tests run in GitHub Actions on Ubuntu and Windows.

```bash
dotnet run --project src/PumpMonitor.App
dotnet test
```

Type `help` for the commands while the app is running, for example `fault spike pump-1` to trigger an alert.
Use `quit` or Ctrl+C to stop it.

For a section-by-section explanation of the code, see [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md).

## Architecture

```
src/PumpMonitor.Core   Devices/     simulated pumps and their physics model
                       Processing/  the telemetry pipeline
                       Alerts/      alert checks and delivery
src/PumpMonitor.App    console UI, operator commands and wiring
tests/                 xUnit tests for Core
```

- **The core is a library and the console is a thin shell.** All behaviour lives in `PumpMonitor.Core`, which
  has no console dependency, so the tests exercise it directly. The app only does the wiring, input and output.
- **Devices are separate from the physics.** `SimulatedDevice` owns timing, sequence numbers and fault injection.
  `PumpProcessModel` produces realistic pressure and temperature curves, and sits behind `IPumpProcess` so tests
  can substitute a stub.
- **Processing steps are pluggable handlers.** The pipeline runs a list of `ITelemetryHandler`s for each reading.
  Logging and alert checks are two such handlers, so adding a step doesn't change the pipeline.
- **Silence is detected separately.** A silent device sends nothing to react to, so `SilenceMonitor` records when
  each device was last heard from and checks the times on a timer. All other alerts are checked as readings
  arrive.
- **Time and randomness are injected.** Every time-dependent class takes a `TimeProvider`, and devices take a
  `Random`. Tests use a fake clock and seeded values, and run in milliseconds.
- **Settings are records.** Intervals, physics constants and alert thresholds are options records with defaults.
  Alert thresholds are validated at startup.

## Concurrency approach

```
pump-1 ─┐
pump-2 ─┼─► bounded channel (100, drop oldest) ─► single consumer ─► handlers (log, alerts)
pump-n ─┘
```

- **Many producers, one consumer.** Each device runs as its own async task and writes to a shared
  `System.Threading.Channels` channel. A single consumer task processes readings in arrival order, so the
  handlers keep their per-device state without locks.
- **Producers never block.** The channel is bounded and drops the oldest reading when full, so a slow consumer
  costs stale data, not stalled devices, and memory stays bounded. Dropped readings are counted and reported at
  shutdown. Handlers never run on a producer's thread.
- **Graceful shutdown.** `quit`, end of input and Ctrl+C all run the same sequence: cancel the producers, wait
  for them all to finish, then close the channel and drain what is already queued. The drain has a 5 s limit,
  after which the rest of the queue is discarded.
- **Failures are contained.** A handler that throws is reported and processing continues. One failed alert
  delivery doesn't lose the alerts after it.
- **Little shared state.** The few values that several threads touch are guarded by `Lock`, `Interlocked` or
  `volatile`.

## How AI tools were used

The project was built with [Claude Code](https://claude.com/claude-code) (Claude Opus 5.5) as a pair
programmer, one small, reviewable change at a time. Commits it helped write carry a `Co-Authored-By: Claude`
trailer in the git history.

- **Design and implementation.** I adapted the original requirements into the specifications I gave Claude, and
  refined them as the work progressed. Claude wrote the code and tests to those specifications, and I reviewed
  the changes and asked for revisions before committing.
- **Review and hardening.** I used Claude to review the code for concurrency bugs and edge cases, and as an
  additional QA tester. This led to several fixes, for example measuring silence on a steady clock instead of the
  wall clock, starting exactly one consumer when `Start` is called concurrently, and keeping reading gaps inside
  100–500 ms despite Windows timer imprecision.
- **Documentation.** Claude drafted this README and the implementation notes from the code, and I edited them.

All AI-generated code is covered by the test suite. Before it was committed, it was reviewed and tested manually
by me and checked by separate, dedicated QA agents.
