namespace PumpMonitor.Core;

/// <summary>
/// Tunable values for the simulation, processing pipeline and console app, gathered in one place.
/// </summary>
/// <remarks>
/// The options records (<see cref="Devices.SimulatedDeviceOptions"/>, <see cref="Devices.PumpProcessOptions"/>)
/// use these as their defaults, so individual instances (e.g. in tests) can still override them.
/// </remarks>
public static class Constants
{
    /// <summary>How many devices the app runs and how they emit readings.</summary>
    public static class Simulation
    {
        public const int MinDeviceCount = 3;
        public const int MaxDeviceCount = 5;

        /// <summary>Device ids are this prefix followed by 1, 2, 3...</summary>
        public const string DeviceIdPrefix = "pump-";

        public const int MinReadingIntervalInMilliseconds = 100;
        public const int MaxReadingIntervalInMilliseconds = 500;

        public static readonly TimeSpan MinReadingInterval = TimeSpan.FromMilliseconds(MinReadingIntervalInMilliseconds);
        public static readonly TimeSpan MaxReadingInterval = TimeSpan.FromMilliseconds(MaxReadingIntervalInMilliseconds);

        /// <summary>Whether pumps are running when they are created.</summary>
        public const bool StartOn = true;
    }

    /// <summary>
    /// Default behaviour of the simulated pump, modelled on a hydraulic fracturing (frac) pump:
    /// pressure is fluid-end discharge pressure, temperature is power-end lube oil temperature.
    /// </summary>
    public static class PumpProcess
    {
        /// <summary>MPa. A typical treating pressure (~8,000 psi), well below a 103 MPa (15,000 psi) pump rating.</summary>
        public const double RunningPressure = 55;

        /// <summary>MPa. With the pump off, discharge falls back to the boost/suction pressure (~40 psi).</summary>
        public const double IdlePressure = 0.3;

        /// <summary>°C. Power-end lube oil normally runs around 60–80 °C.</summary>
        public const double RunningTemperature = 70;

        /// <summary>°C. Ambient.</summary>
        public const double IdleTemperature = 20;

        /// <summary>Bringing a pump online or offline takes seconds.</summary>
        public static readonly TimeSpan PressureTimeConstant = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Time-compressed for the demo: real lube oil takes 10–20 minutes to warm up, which would be
        /// too slow to watch. Still much slower than pressure, so temperature visibly lags.
        /// </summary>
        public static readonly TimeSpan TemperatureTimeConstant = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Standard deviation per √second as a fraction of the current pressure (~0.55 MPa at 55 MPa);
        /// plunger pulsation is a few percent of discharge pressure.
        /// </summary>
        public const double RelativePressureNoise = 0.01;

        /// <summary>Standard deviation in °C per √second.</summary>
        public const double TemperatureNoise = 0.2;
    }

    /// <summary>
    /// Default alert thresholds. <see cref="PumpMonitor.Core.Alerts.AlertOptions"/> uses these as its defaults;
    /// they can be overridden per monitor or changed at runtime with the <c>threshold</c> command.
    /// </summary>
    public static class Alerts
    {
        /// <summary>MPa (~10,000 psi). Above normal treating pressure, well below a 103 MPa pump rating.</summary>
        public const double PressureThreshold = 69;

        /// <summary>MPa. Pressure must fall this far below the threshold before the alert can fire again.</summary>
        public const double PressureDeadband = 1;

        /// <summary>°C. Power-end lube oil running hot (normal is around 60–80 °C).</summary>
        public const double TemperatureThreshold = 90;

        /// <summary>°C. Temperature must fall this far below the threshold before the alert can fire again.</summary>
        public const double TemperatureDeadband = 2;

        /// <summary>A device is silent once nothing has been heard from it for longer than this.</summary>
        public const int SilenceTimeoutInMilliseconds = 5000;

        /// <summary>How often to look for silent devices; bounds how late a silence alert can be.</summary>
        public const int SilenceCheckIntervalInMilliseconds = 500;

        public static readonly TimeSpan SilenceTimeout = TimeSpan.FromMilliseconds(SilenceTimeoutInMilliseconds);
        public static readonly TimeSpan SilenceCheckInterval = TimeSpan.FromMilliseconds(SilenceCheckIntervalInMilliseconds);
    }

    /// <summary>Queueing and shutdown of the processing pipeline.</summary>
    public static class Pipeline
    {
        /// <summary>Queued readings before the oldest are dropped.</summary>
        public const int QueueCapacity = 1000;

        /// <summary>How long shutdown waits for queued readings to be processed.</summary>
        public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    }
}
