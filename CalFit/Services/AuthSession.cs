using Microsoft.Maui.Storage;
using CalFit.Models;
namespace CalFit.Services;

public sealed class AuthSession
{
    private readonly CalFitApi _api;
    private const string TokenKey = "auth_token";
    public string? Token { get; private set; }
    public UserProfile? Profile { get; private set; }
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(Token);
    public event Action? Changed;
    public AuthSession(CalFitApi api) => _api = api;

    public async Task RestoreAsync()
    {
        try
        {
            Token = await SecureStorage.Default.GetAsync(TokenKey);
            if (IsSignedIn)
            {
                try { await RefreshProfileAsync(); }
                catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    await SignOutAsync();
                }
                catch { /* Preserve the saved login while offline. */ }
            }
        }
        catch { Token = null; }
        Changed?.Invoke();
    }

    public async Task SignInAsync(string email, string password)
    {
        var token = await _api.LoginAsync(email, password);
        await SecureStorage.Default.SetAsync(TokenKey, token);
        Token = token;
        Profile = null;
        try { await RefreshProfileAsync(); }
        catch { /* Login succeeded, profile can be loaded later. */ }
        Changed?.Invoke();
    }

    public async Task RefreshProfileAsync()
    {
        if (!IsSignedIn) return;
        Profile = await _api.GetProfileAsync(Token!);
        Changed?.Invoke();
    }

    public Task SignOutAsync()
    {
        SecureStorage.Default.Remove(TokenKey);
        Token = null;
        Profile = null;
        Changed?.Invoke();
        return Task.CompletedTask;
    }
}
