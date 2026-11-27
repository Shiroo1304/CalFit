namespace CalFit;
public partial class App : Application
{
    private readonly CalFit.Services.CalFitAppActivity activity;
    public App(CalFit.Services.CalFitAppActivity activity)
    {
        this.activity = activity;
        InitializeComponent();
    }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage());
        window.Activated += (_, _) => activity.SetActive(true);
        window.Resumed += (_, _) => activity.SetActive(true);
        window.Deactivated += (_, _) => activity.SetActive(false);
        window.Stopped += (_, _) => activity.SetActive(false);
        window.Destroying += (_, _) => activity.SetActive(false);
        return window;
    }
}
