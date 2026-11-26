using CalFit.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace CalFit;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        builder.Services.AddSingleton<CalFitApi>();
        builder.Services.AddSingleton<AuthSession>();
        builder.Services.AddSingleton<AppearanceService>();
        builder.Services.AddSingleton<MealNotesService>();
        builder.Services.AddSingleton<ICalFitBotAddressStore, CalFitBotAddressStore>();
        builder.Services.AddSingleton(new CalFitBotInbox(
            Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory,
                CalFitBotConfiguration.SimulationEnabled ? "calfitbot-simulator-inbox" : "calfitbot-inbox")));
        builder.Services.AddSingleton<ICalFitBotDiscovery, CalFit.Platforms.Android.AndroidCalFitBotDiscovery>();
#if DEBUG && CALFITBOT_SIMULATION
        builder.Services.AddSingleton<ICalFitBotService>(_ =>
        {
            using var stream = typeof(MauiProgram).Assembly.GetManifestResourceStream("CalFit.Bot.Sample.jpg")
                ?? throw new InvalidOperationException("Missing simulator JPEG.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return new MockCalFitBotService(buffer.ToArray());
        });
#else
        builder.Services.AddHttpClient<ICalFitBotService, CalFitBotService>(client =>
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(4),
                MaxConnectionsPerServer = 2
            });
#endif
        builder.Services.AddSingleton<ICalFitBotPhotoValidator, CalFit.Platforms.Android.AndroidCalFitBotPhotoValidator>();
        builder.Services.AddTransient<CalFitBotSession>();

        return builder.Build();
    }
}