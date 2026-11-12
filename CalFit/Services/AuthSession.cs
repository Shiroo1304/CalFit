
using Microsoft.Maui.Storage;
using CalFit.Models;

namespace CalFit.Services;

public sealed class AuthSession
{
    private readonly CalFitApi _api;
    private const string TokenKey = "auth_token";

    public string? Token { get; private set; }
    public UserProfile? Profile { get; private set; }

    public bool IsSignedIn =>
        !string.IsNullOrWhiteSpace(Token);

    public string? LogoutReason { get; private set; }

    public event Action? Changed;

    public AuthSession(CalFitApi api)
    {
        _api = api;
    }

    public async Task RestoreAsync()
    {
        try
        {
            Token = await SecureStorage.Default.GetAsync(TokenKey);

            if (IsSignedIn)
            {
                try
                {
                    await RefreshProfileAsync();
                }
                catch (ApiException ex)
                    when (ex.StatusCode ==
                          System.Net.HttpStatusCode.Unauthorized)
                {
                    await SignOutAsync("Session expired.");
                }
                catch
                {
                    // Server monitoring will handle outages.
                }
            }
        }
        catch
        {
            Token = null;
        }

        Changed?.Invoke();
    }

    public async Task SignInAsync(string email, string password)
    {
        var token = await _api.LoginAsync(email, password);

        await SecureStorage.Default.SetAsync(TokenKey, token);

        Token = token;
        Profile = null;
        LogoutReason = null;

        try
        {
            await RefreshProfileAsync();
        }
        catch
        {
            // Profile can load later.
        }

        Changed?.Invoke();
    }

    public async Task RefreshProfileAsync()
    {
        if (!IsSignedIn)
            return;

        Profile = await _api.GetProfileAsync(Token!);
        Changed?.Invoke();
    }


    public async Task SignOutAsync(string? reason = null)
    {
        var token = Token;

        // Clear the local session immediately.
        SecureStorage.Default.Remove(TokenKey);
        Token = null;
        Profile = null;
        LogoutReason = reason;
        Changed?.Invoke();

        // Only manual logout should notify the API.
        if (reason is null && !string.IsNullOrWhiteSpace(token))
        {
            try
            {
                await _api.SendLogoutAsync(token);
            }
            catch
            {
                // Local logout must still succeed if API is offline.
            }
        }
    }

}
