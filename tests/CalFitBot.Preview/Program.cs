using CalFit.Services;
using CalFitBotPreview.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<AuthSession>();
builder.Services.AddScoped<CalFitApi>();
builder.Services.AddScoped<MealNotesService>();
builder.Services.AddScoped<ICalFitBotAddressStore, PreviewAddressStore>();
builder.Services.AddScoped<ICalFitBotService>(_ => new MockCalFitBotService(
    File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "sample.jpg"))));
builder.Services.AddScoped(_ => new CalFitBotInbox(Path.Combine(Path.GetTempPath(), "calfitbot-preview-" + Guid.NewGuid())));
builder.Services.AddScoped<CalFitBotSession>();
var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapGet("/css/calfit.css", () => Results.File(Path.Combine(AppContext.BaseDirectory, "wwwroot", "css", "calfit.css"), "text/css"));
app.MapGet("/test-counts", () => new { analyze = CalFitApi.AnalysisCalls, save = CalFitApi.SaveCalls });
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
