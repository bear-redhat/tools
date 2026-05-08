using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

/// <summary>
/// Manages GitHub authentication. Supports two modes (in priority order):
/// 1. GitHub App — generates JWTs from the App private key, exchanges for installation tokens.
/// 2. Personal Access Token — uses a fine-grained PAT directly as a bearer token.
/// Returns null when neither is configured (unauthenticated mode, 60 req/hr).
/// </summary>
public sealed class GitHubAppAuth
{
    private readonly GitHubOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubAppAuth> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _installationToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public bool IsConfigured { get; }

    /// <summary>Reports the active authentication mode: "app", "pat", or "none".</summary>
    public string AuthMode { get; }

    public GitHubAppAuth(IHttpClientFactory httpClientFactory, IOptions<GitHubOptions> options,
        ILogger<GitHubAppAuth> logger)
    {
        _options = options.Value;
        _httpClient = httpClientFactory.CreateClient("GitHub");
        _logger = logger;

        var appConfigured = !string.IsNullOrEmpty(_options.AppId)
            && !string.IsNullOrEmpty(_options.PrivateKeyFile)
            && !string.IsNullOrEmpty(_options.InstallationId);

        var patConfigured = !string.IsNullOrEmpty(_options.PersonalAccessToken);

        if (appConfigured)
        {
            AuthMode = "app";
            IsConfigured = true;
            if (patConfigured)
                _logger.LogInformation("GitHub App and PAT both configured; preferring GitHub App (higher rate limits)");
        }
        else if (patConfigured)
        {
            AuthMode = "pat";
            IsConfigured = true;

            if (!_options.PersonalAccessToken!.StartsWith("github_pat_", StringComparison.Ordinal))
                _logger.LogWarning("GitHub PAT does not have the fine-grained token prefix (github_pat_); consider using a fine-grained personal access token");
        }
        else
        {
            AuthMode = "none";
            IsConfigured = false;
            _logger.LogInformation("GitHub credentials not configured; using unauthenticated mode (60 req/hr)");
        }
    }

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        if (AuthMode == "pat")
            return _options.PersonalAccessToken;

        if (_installationToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
            return _installationToken;

        await _lock.WaitAsync(ct);
        try
        {
            if (_installationToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
                return _installationToken;

            var jwt = GenerateJwt();
            var token = await ExchangeForInstallationToken(jwt, ct);
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private string GenerateJwt()
    {
        var now = DateTimeOffset.UtcNow;

        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            new { alg = "RS256", typ = "JWT" }));

        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(10).ToUnixTimeSeconds(),
            iss = _options.AppId,
        }));

        var dataToSign = Encoding.ASCII.GetBytes($"{header}.{payload}");

        using var rsa = RSA.Create();
        var pem = File.ReadAllText(_options.PrivateKeyFile!);
        rsa.ImportFromPem(pem);

        var signature = Base64UrlEncode(rsa.SignData(dataToSign, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        return $"{header}.{payload}.{signature}";
    }

    private async Task<string> ExchangeForInstallationToken(string jwt, CancellationToken ct)
    {
        var url = $"https://api.github.com/app/installations/{_options.InstallationId}/access_tokens";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);

        var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("GitHub App token exchange failed ({Status}): {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"GitHub App token exchange failed: {response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        _installationToken = root.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("GitHub returned empty token");

        if (root.TryGetProperty("expires_at", out var expiresAt))
            _tokenExpiry = DateTimeOffset.Parse(expiresAt.GetString()!).AddMinutes(-5);
        else
            _tokenExpiry = DateTimeOffset.UtcNow.AddMinutes(50);

        _logger.LogInformation("GitHub App installation token acquired, expires at {Expiry}", _tokenExpiry);
        return _installationToken;
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
