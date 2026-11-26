using Microsoft.Maui.Storage;

namespace CalFit.Services;

public sealed class CalFitBotAddressStore : ICalFitBotAddressStore
{
    public string? LastAddress
    {
        get => Preferences.Default.Get("calfit.bot.lastAddress", "");
        set => Preferences.Default.Set("calfit.bot.lastAddress", value ?? "");
    }
}

public static class CalFitBotConfiguration
{
    // Opt-in only: dotnet build ... -c Debug -p:CalFitBotSimulation=true
    public static bool SimulationEnabled
    {
        get
        {
#if DEBUG && CALFITBOT_SIMULATION
            return true;
#else
            return false;
#endif
        }
    }
}
