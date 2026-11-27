namespace CalFit.Services;

// Window lifecycle, not robot standby. No Android background service or activity launch is needed.
public sealed class CalFitAppActivity
{
    public bool IsActive { get; private set; }
    public event Action? Changed;

    public void SetActive(bool active)
    {
        if (IsActive == active) return;
        IsActive = active;
        Changed?.Invoke();
    }
}
