using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace CalFit;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private void OnBlazorWebViewInitialized(
        object? sender,
        BlazorWebViewInitializedEventArgs e)
    {
#if ANDROID
        // Allow the ESP32's local HTTP stream inside the WebView.
        e.WebView.Settings.MixedContentMode =
            global::Android.Webkit.MixedContentHandling.AlwaysAllow;
#endif
    }
}