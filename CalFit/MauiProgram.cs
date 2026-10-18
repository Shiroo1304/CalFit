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
        return builder.Build();
    }
}
