namespace PumpMonitor.Core;

/// <summary>
/// An on/off setting that one thread can flip while others read it, e.g. whether readings are printed:
/// the command loop sets it and the pipeline consumer checks it for every reading.
/// </summary>
public sealed class Toggle(bool isOn = false)
{
    private volatile bool _isOn = isOn;

    public bool IsOn
    {
        get => _isOn;
        set => _isOn = value;
    }
}
