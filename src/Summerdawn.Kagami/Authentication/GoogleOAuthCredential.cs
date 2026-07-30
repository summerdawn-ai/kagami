using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Summerdawn.Kagami.Authentication;

/// <summary>
/// Authenticates against Google OAuth and caches tokens for subsequent requests.
/// </summary>
public sealed class GoogleOAuthCredential : IConnectorCredential
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RedirectUri = "http://localhost:4189/";
    private static readonly TimeSpan TokenExpiryBuffer = TimeSpan.FromMinutes(5);

    private readonly string clientId;
    private readonly string clientSecret;
    private readonly string endpointName;
    private readonly IReadOnlyList<string> scopes;
    private readonly HttpClient httpClient;
    private readonly GoogleTokenCache tokenCache;
    private string? accessToken;
    private string? refreshToken;
    private DateTimeOffset tokenExpiry;

    /// <summary>
    /// Initializes a Google OAuth credential.
    /// </summary>
    public GoogleOAuthCredential(
        string clientId,
        string clientSecret,
        string endpointName,
        IReadOnlyList<string> scopes,
        HttpClient httpClient,
        GoogleTokenCache tokenCache)
    {
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        this.endpointName = endpointName;
        this.scopes = scopes;
        this.httpClient = httpClient;
        this.tokenCache = tokenCache;

        var cached = tokenCache.Load(endpointName);
        if (cached is not null)
        {
            accessToken = cached.AccessToken;
            refreshToken = cached.RefreshToken;
            tokenExpiry = cached.Expiry;
        }
    }

    /// <summary>
    /// Gets a valid Google access token, refreshing or interactively acquiring one when necessary.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(accessToken) && DateTimeOffset.UtcNow < tokenExpiry - TokenExpiryBuffer)
        {
            return accessToken;
        }

        if (!string.IsNullOrEmpty(refreshToken))
        {
            bool refreshed = await TryRefreshTokenAsync(cancellationToken);
            if (refreshed)
            {
                return accessToken!;
            }
        }

        await AuthorizeInteractivelyAsync(cancellationToken);
        return accessToken!;
    }

    private async Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken)
    {
        FormUrlEncodedContent body = new([
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("refresh_token", refreshToken!),
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
        ]);

        var response = await httpClient.PostAsync(TokenEndpoint, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        string? newAccessToken = root.TryGetProperty("access_token", out var atElem) ? atElem.GetString() : null;
        if (string.IsNullOrEmpty(newAccessToken))
        {
            return false;
        }

        accessToken = newAccessToken;
        int expiresIn = root.TryGetProperty("expires_in", out var expElem) ? expElem.GetInt32() : 3600;
        tokenExpiry = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(expiresIn);

        if (root.TryGetProperty("refresh_token", out var rtElem) && rtElem.GetString() is string newRefreshToken)
        {
            refreshToken = newRefreshToken;
        }

        PersistTokens();
        return true;
    }

    private async Task AuthorizeInteractivelyAsync(CancellationToken cancellationToken)
    {
        string state = Guid.NewGuid().ToString("N");
        string scopeString = string.Join(" ", scopes);

        string authUrl = AuthEndpoint
            + "?client_id=" + Uri.EscapeDataString(clientId)
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&response_type=code"
            + "&scope=" + Uri.EscapeDataString(scopeString)
            + "&state=" + state
            + "&access_type=offline"
            + "&prompt=consent";

        Console.Error.WriteLine("Opening browser for Google OAuth authorization...");
        Console.Error.WriteLine("If the browser does not open automatically, navigate to:");
        Console.Error.WriteLine(authUrl);

        OpenBrowser(authUrl);

        string code = await ListenForCallbackAsync(state, cancellationToken);
        await ExchangeCodeAsync(code, cancellationToken);
    }

    private static async Task<string> ListenForCallbackAsync(string expectedState, CancellationToken cancellationToken)
    {
        using HttpListener listener = new();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();

        try
        {
            using var registration = cancellationToken.Register(listener.Stop);
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            string? code = context.Request.QueryString["code"];
            string? state = context.Request.QueryString["state"];
            string? error = context.Request.QueryString["error"];

            string html = string.IsNullOrEmpty(error)
                ? "<html><body><h2>Authorization successful! You may close this tab.</h2></body></html>"
                : $"<html><body><h2>Authorization failed: {WebUtility.HtmlEncode(error)}. You may close this tab.</h2></body></html>";

            byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = htmlBytes.Length;
            await context.Response.OutputStream.WriteAsync(htmlBytes, cancellationToken);
            context.Response.Close();

            if (!string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException($"Google OAuth authorization failed: {error}");
            }

            if (state != expectedState)
            {
                throw new InvalidOperationException("Google OAuth state parameter mismatch.");
            }

            return code ?? throw new InvalidOperationException("Google OAuth callback did not include an authorization code.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        FormUrlEncodedContent body = new([
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
        ]);

        var response = await httpClient.PostAsync(TokenEndpoint, body, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Token exchange response did not include an access token.");
        int expiresIn = root.TryGetProperty("expires_in", out var expElem) ? expElem.GetInt32() : 3600;
        tokenExpiry = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(expiresIn);

        if (root.TryGetProperty("refresh_token", out var rtElem) && rtElem.GetString() is string newRefreshToken)
        {
            refreshToken = newRefreshToken;
        }

        PersistTokens();
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Browser open is best-effort; the user can navigate to the printed URL manually.
        }
    }

    private void PersistTokens()
    {
        tokenCache.Save(endpointName, new GoogleTokenCacheEntry(accessToken, refreshToken, tokenExpiry));
    }
}
