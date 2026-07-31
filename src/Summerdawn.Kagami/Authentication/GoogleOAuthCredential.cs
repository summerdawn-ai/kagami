using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Summerdawn.Kagami.Authentication;

/// <summary>
/// Authenticates against Google OAuth and caches tokens for subsequent requests.
/// </summary>
/// <remarks>
/// The cache is scoped to the configured endpoint name. A cached entry is usable only when it
/// contains the expected account email, which prevents a token obtained for one configured
/// Google account from being used by another endpoint. A mismatch is intentionally treated as a
/// cache miss so the normal interactive authorization flow can obtain a replacement credential.
///
/// The account email is obtained from the ID token returned by the authorization-code exchange.
/// This class uses that token only as a source of the email claim; it does not validate the token
/// signature or any other claims. Refresh responses are trusted to belong to the account already
/// accepted when the cached refresh token was created.
/// </remarks>
public sealed class GoogleOAuthCredential : IConnectorCredential
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RedirectUri = "http://localhost:4189/";
    private static readonly TimeSpan TokenExpiryBuffer = TimeSpan.FromMinutes(5);

    private readonly string clientId;
    private readonly string clientSecret;
    private readonly string endpointName;
    private readonly string expectedUserId;
    private readonly IReadOnlyList<string> scopes;
    private readonly HttpClient httpClient;
    private readonly GoogleTokenCache tokenCache;
    private string? accessToken;
    private string? refreshToken;
    private string? accountEmail;
    private string? idToken;
    private DateTimeOffset tokenExpiry;

    /// <summary>
    /// Initializes a Google OAuth credential.
    /// </summary>
    public GoogleOAuthCredential(
        string clientId,
        string clientSecret,
        string endpointName,
        string expectedUserId,
        IReadOnlyList<string> scopes,
        HttpClient httpClient,
        GoogleTokenCache tokenCache)
    {
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        this.endpointName = endpointName;
        this.expectedUserId = expectedUserId;
        this.scopes = scopes;
        this.httpClient = httpClient;
        this.tokenCache = tokenCache;

        var cached = tokenCache.Load(endpointName);
        // A cache entry for a different account is deliberately ignored without diagnostics. The
        // caller will fall through to interactive authorization when it next requests a token.
        if (cached is { AccountEmail: not null } && string.Equals(cached.AccountEmail, expectedUserId, StringComparison.OrdinalIgnoreCase))
        {
            accessToken = cached.AccessToken;
            refreshToken = cached.RefreshToken;
            accountEmail = cached.AccountEmail;
            idToken = cached.IdToken;
            tokenExpiry = cached.Expiry;
        }
    }

    /// <summary>
    /// Gets a valid Google access token, refreshing or interactively acquiring one when necessary.
    /// </summary>
    /// <remarks>
    /// Access tokens are considered stale five minutes before their reported expiry. The method
    /// first returns a still-valid access token, then tries the cached refresh token, and finally
    /// starts the interactive flow if neither cached value can be used.
    /// </remarks>
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

    /// <summary>
    /// Attempts to replace the cached access token by using its refresh token.
    /// </summary>
    /// <remarks>
    /// Google may omit a replacement refresh token, in which case the existing one remains in
    /// memory and is persisted again. A failed HTTP response or a response without an access
    /// token is reported as <c>false</c>, allowing the caller to fall back to interactive login.
    /// The response is not checked for an ID token because the cached account identity was already
    /// established during the authorization-code exchange.
    /// </remarks>
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

    /// <summary>
    /// Runs Google's localhost authorization-code flow and stores the resulting credential.
    /// </summary>
    /// <remarks>
    /// A random state value is included in the authorization URL and verified by the callback
    /// listener. Offline access and consent are requested so Google returns a refresh token that
    /// can be cached for later invocations.
    /// </remarks>
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

    /// <summary>
    /// Waits for the OAuth callback on the fixed localhost redirect URI.
    /// </summary>
    /// <remarks>
    /// The listener returns a short browser response before processing the callback. It rejects
    /// provider errors, mismatched state values, and callbacks without an authorization code.
    /// Cancellation stops the listener and is surfaced as <see cref="OperationCanceledException"/>.
    /// </remarks>
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

    /// <summary>
    /// Exchanges an authorization code for Google tokens and verifies the authorized account.
    /// </summary>
    /// <remarks>
    /// The access token and ID token are both required. The ID token payload is decoded only to
    /// read its email claim, which is compared with the configured account before any token is
    /// persisted. A mismatch therefore cannot poison the endpoint's cache.
    /// </remarks>
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
        string idToken = root.TryGetProperty("id_token", out var idTokenElement)
            ? idTokenElement.GetString() ?? throw new InvalidOperationException("Google OAuth token response did not include an ID token.")
            : throw new InvalidOperationException("Google OAuth token response did not include an ID token.");
        this.idToken = idToken;
        accountEmail = GetIdTokenEmail(idToken);
        ValidateAccountEmail(accountEmail);
        int expiresIn = root.TryGetProperty("expires_in", out var expElem) ? expElem.GetInt32() : 3600;
        tokenExpiry = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(expiresIn);

        if (root.TryGetProperty("refresh_token", out var rtElem) && rtElem.GetString() is string newRefreshToken)
        {
            refreshToken = newRefreshToken;
        }

        PersistTokens();
    }

    /// <summary>
    /// Opens the authorization URL using the operating system's default browser when possible.
    /// </summary>
    /// <remarks>
    /// Browser startup is best effort because the URL is also printed for manual navigation.
    /// </remarks>
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

    /// <summary>
    /// Saves the current Google token state for this endpoint.
    /// </summary>
    private void PersistTokens()
    {
        tokenCache.Save(endpointName, new GoogleTokenCacheEntry(accessToken, refreshToken, tokenExpiry, accountEmail, idToken));
    }

    /// <summary>
    /// Reads the email claim from the payload of a Google ID token.
    /// </summary>
    /// <remarks>
    /// This is deliberately payload decoding rather than ID-token validation. The token is used
    /// only to identify the account returned by the interactive login; Google token signature and
    /// claim validation are outside this credential's responsibility.
    /// </remarks>
    private static string GetIdTokenEmail(string idToken)
    {
        string[] parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("Google OAuth response did not contain a usable ID token.");
        }

        try
        {
            byte[] payload = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/') + new string('=', (4 - parts[1].Length % 4) % 4));
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.GetProperty("email").GetString()
                ?? throw new InvalidOperationException("Google OAuth ID token did not contain an email.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Google OAuth response did not contain a usable ID token.", ex);
        }
    }

    /// <summary>
    /// Ensures that a newly authorized Google account matches the endpoint configuration.
    /// </summary>
    private void ValidateAccountEmail(string accountEmail)
    {
        if (!string.Equals(accountEmail, expectedUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Google credential belongs to '{accountEmail}', but endpoint '{endpointName}' requires '{expectedUserId}'.");
        }
    }
}
