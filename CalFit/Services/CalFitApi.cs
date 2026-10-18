using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CalFit.Models;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace CalFit.Services;

public sealed class ApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public ApiException(string message, HttpStatusCode? statusCode = null) : base(message) => StatusCode = statusCode;
}

public sealed class CalFitApi
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ApiSetting = "calfit.api.url";

    public string DefaultBaseUrl => DeviceInfo.Current.Platform == DevicePlatform.Android
        ? DeviceInfo.Current.DeviceType == DeviceType.Virtual ? "http://10.0.2.2:5206/" : "http://127.0.0.1:5206/"
        : "http://localhost:5206/";

    public string BaseUrl
    {
        get => Preferences.Default.Get(ApiSetting, DefaultBaseUrl);
        set
        {
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("Enter a complete http:// or https:// API address.");
            Preferences.Default.Set(ApiSetting, value.TrimEnd('/') + "/");
        }
    }

    private Uri Endpoint(string route)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var root))
            throw new ApiException("The API address is invalid. Correct it in Settings.");
        return new Uri(root, route.TrimStart('/'));
    }

    private HttpRequestMessage Request(HttpMethod method, string route, string? token = null)
    {
        var request = new HttpRequestMessage(method, Endpoint(route));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        try
        {
            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string detail = "";
                try
                {
                    var content = await response.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(content) && content.Length < 320)
                        detail = content.Trim('"', ' ', '\r', '\n');
                }
                catch { }
                var status = response.StatusCode;
                response.Dispose();
                throw new ApiException(status switch
                {
                    HttpStatusCode.Unauthorized => "Your session has expired or your credentials are incorrect.",
                    HttpStatusCode.NotFound => "The requested API endpoint was not found. Check the server address and version.",
                    HttpStatusCode.BadGateway => "The food analysis provider is unavailable. Try again later.",
                    HttpStatusCode.UnprocessableEntity => "Food was not recognized in this photo.",
                    _ => $"Server returned HTTP {(int)status}. {detail}"
                }, status);
            }
            return response;
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"Cannot connect to {BaseUrl} — {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            throw new ApiException($"The request timed out. Check your API server and connection: {BaseUrl}");
        }
    }

    private async Task<T> ReadAsync<T>(HttpRequestMessage request)
    {
        using (request)
        using (var response = await SendAsync(request))
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions)
                ?? throw new ApiException("The API returned an empty response.");
    }

    public async Task<bool> PingAsync()
    {
        // Profile is JWT-protected. An HTTP 401 means we reached the real API.
        using var req = Request(HttpMethod.Get, "api/profile");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            return response.StatusCode == HttpStatusCode.Unauthorized || response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) { return false; }
    }

    public async Task<string> LoginAsync(string email, string password)
    {
        using var req = Request(HttpMethod.Post, "api/auth/login");
        req.Content = JsonContent.Create(new { Email = email.Trim(), Password = password });
        var login = await ReadAsync<LoginResponse>(req);
        if (string.IsNullOrWhiteSpace(login.Token)) throw new ApiException("Login succeeded but the server returned no token.");
        return login.Token;
    }
    private sealed class LoginResponse { public string Token { get; set; } = ""; }

    public async Task RegisterAsync(string name, string email, string password)
    {
        using var req = Request(HttpMethod.Post, "api/auth/register");
        req.Content = JsonContent.Create(new { Name = name.Trim(), Email = email.Trim(), Password = password });
        using var response = await SendAsync(req);
    }

    private static string DateQuery(DateTime date)
    {
        string day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        int offset = (int)TimeZoneInfo.Local.GetUtcOffset(date).TotalMinutes;
        return $"date={day}&offsetMinutes={offset}";
    }

    public Task<DashboardSummary> GetDashboardAsync(string token, DateTime date)
        => ReadAsync<DashboardSummary>(Request(HttpMethod.Get, "api/dashboard?" + DateQuery(date), token));

    public Task<UserProfile> GetProfileAsync(string token)
        => ReadAsync<UserProfile>(Request(HttpMethod.Get, "api/profile", token));

    public async Task UpdateProfileAsync(string token, UserProfile profile)
    {
        using var req = Request(HttpMethod.Put, "api/profile", token);
        req.Content = JsonContent.Create(new {
            profile.Name, profile.CalorieGoal, profile.ProteinGoal, profile.CarbsGoal,
            profile.FatGoal, profile.HeightCm, profile.WeightKg
        });
        using var response = await SendAsync(req);
    }

    public Task<List<Meal>> GetMealsAsync(string token, DateTime date)
        => ReadAsync<List<Meal>>(Request(HttpMethod.Get, "api/meals?" + DateQuery(date), token));

    public Task<List<Meal>> GetAllMealsAsync(string token)
        => ReadAsync<List<Meal>>(Request(HttpMethod.Get, "api/meals/all", token));

    public Task<Meal> SaveMealAsync(string token, Meal meal)
    {
        var req = Request(HttpMethod.Post, "api/meals", token);
        req.Content = JsonContent.Create(new {
            meal.FoodName, meal.MealType, meal.WeightGrams, meal.Calories,
            meal.ProteinGrams, meal.CarbsGrams, meal.FatGrams
        });
        return ReadAsync<Meal>(req);
    }

    public async Task DeleteMealAsync(string token, int id)
    {
        using var req = Request(HttpMethod.Delete, $"api/meals/{id}", token);
        using var response = await SendAsync(req);
    }

    public async Task<FoodAnalysisResult> AnalyzeAsync(string token, string filePath, double? grams)
    {
        if (!File.Exists(filePath)) throw new ApiException("Choose or take a food photo first.");
        var info = new FileInfo(filePath);
        if (info.Length > 10 * 1024 * 1024) throw new ApiException("The image is too large (maximum 10 MB).");
        using var req = Request(HttpMethod.Post, "api/ai/analyze-food", token);
        using var form = new MultipartFormDataContent();
        using var file = File.OpenRead(filePath);
        using var content = new StreamContent(file);
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        content.Headers.ContentType = new MediaTypeHeaderValue(extension switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        });
        form.Add(content, "image", Path.GetFileName(filePath));
        if (grams is > 0)
            form.Add(new StringContent(grams.Value.ToString(CultureInfo.InvariantCulture)), "weightGrams");
        req.Content = form;
        return await ReadAsync<FoodAnalysisResult>(req);
    }
}
