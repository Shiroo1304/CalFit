using Microsoft.Maui.Storage;

namespace CalFit.Services;

public sealed class CalFitBotAddressStore : ICalFitBotAddressStore
{
    private static string Key => CalFitBotConfiguration.SimulationEnabled ? "calfit.bot.simulatorAddress" : "calfit.bot.lastAddress";
    public string? LastAddress
    {
        get => Preferences.Default.Get(Key, "");
        set => Preferences.Default.Set(Key, value ?? "");
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
