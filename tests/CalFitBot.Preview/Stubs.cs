// Browser-only test doubles. These files are NOT referenced by the Android project.
namespace Microsoft.Maui.Storage
{
    public static class Preferences { public static Store Default { get; } = new(); }
    public sealed class Store { public string Get(string key, string fallback) => fallback; public void Set(string key, string value) { } }
}
namespace CalFit.Services
{
    public sealed class AuthSession
    {
        public string? Token => "browser-test-token";
        public CalFit.Models.UserProfile? Profile { get; } = new() { Id = 1, Name = "Preview" };
        public Task RefreshProfileAsync() => Task.CompletedTask;
        public Task SignInAsync(string email, string password) => Task.CompletedTask;
    }
    public sealed class CalFitApi
    {
        public static int AnalysisCalls, SaveCalls;
        public string BaseUrl { get; set; } = "https://api.example.invalid/";
        public Task<bool> PingAsync() => Task.FromResult(true);
        public Task RegisterAsync(string name, string email, string password) => Task.CompletedTask;
        public Task<CalFit.Models.FoodAnalysisResult> AnalyzeAsync(string token, string file, double? grams)
        {
            AnalysisCalls++;
            return Task.FromResult(new CalFit.Models.FoodAnalysisResult
            { IsFood = true, FoodName = "Simulated rice and chicken", Calories = 520, ProteinGrams = 32, CarbsGrams = 58, FatGrams = 14 });
        }
        public Task<CalFit.Models.Meal> SaveMealAsync(string token, CalFit.Models.Meal meal)
        { SaveCalls++; meal.Id = SaveCalls; meal.UserId = 1; return Task.FromResult(meal); }
    }
    public sealed class MealNotesService { public void Save(int user, int id, CalFit.Models.MealNotes notes) { } }
    public sealed class PreviewAddressStore : ICalFitBotAddressStore { public string? LastAddress { get; set; } }
}
