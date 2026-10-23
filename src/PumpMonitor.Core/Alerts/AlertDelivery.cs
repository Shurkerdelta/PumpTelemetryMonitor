namespace PumpMonitor.Core.Alerts;

/// <summary>Hands alerts to a monitor's callback without letting one failed delivery lose the rest.</summary>
internal static class AlertDelivery
{
    /// <summary>
    /// Passes every alert to <paramref name="onAlert"/>, even after a call throws, then throws an
    /// <see cref="AggregateException"/> of the failures.
    /// </summary>
    public static void DeliverAll(Action<Alert> onAlert, List<Alert> alerts)
    {
        List<Exception> errors = [];
        foreach (Alert alert in alerts)
        {
            try
            {
                onAlert(alert);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException(errors);
        }
    }
}
