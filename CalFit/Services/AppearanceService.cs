using Microsoft.Maui.Storage;
namespace CalFit.Services;
public sealed class AppearanceService
{
    public event Action? Changed;
    public string Theme
    {
        get => Preferences.Default.Get("calfit.theme", "emerald");
        set { Preferences.Default.Set("calfit.theme", value); Changed?.Invoke(); }
    }
    public string CustomAccent
    {
        get => Preferences.Default.Get("calfit.customaccent", "");
        set
        {
            if (!string.IsNullOrEmpty(value) && !System.Text.RegularExpressions.Regex.IsMatch(value, @"^#[0-9a-fA-F]{6}$"))
                throw new ArgumentException("Enter a six-digit hexadecimal color, such as #69E0A6.");
            Preferences.Default.Set("calfit.customaccent", value); Changed?.Invoke();
        }
    }
    public string AccentText
    {
        get
        {
            if (CustomAccent.Length != 7) return "#082719";
            int r = Convert.ToInt32(CustomAccent.Substring(1, 2), 16), g = Convert.ToInt32(CustomAccent.Substring(3, 2), 16), b = Convert.ToInt32(CustomAccent.Substring(5, 2), 16);
            return r * .299 + g * .587 + b * .114 > 150 ? "#102019" : "#FFFFFF";
        }
    }
    public double FontScale
    {
        get => Preferences.Default.Get("calfit.fontscale", 1.0);
        set { Preferences.Default.Set("calfit.fontscale", Math.Clamp(value, .9, 1.25)); Changed?.Invoke(); }
    }
}
