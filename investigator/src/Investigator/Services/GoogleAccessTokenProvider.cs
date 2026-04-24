using Google.Apis.Auth.OAuth2;

namespace Investigator.Services;

public sealed class GoogleAccessTokenProvider
{
    private readonly string? _serviceAccountKeyPath;
    private readonly ILogger _logger;
    private GoogleCredential? _credential;

    public GoogleAccessTokenProvider(string? serviceAccountKeyPath, ILogger logger)
    {
        _serviceAccountKeyPath = serviceAccountKeyPath;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_credential is null)
        {
            if (string.IsNullOrEmpty(_serviceAccountKeyPath))
            {
                _logger.LogInformation("Loading Google credentials from application default");
                _credential = await GoogleCredential.GetApplicationDefaultAsync(ct);
            }
            else
            {
                _logger.LogInformation("Loading Google credentials from service account key: {Path}", _serviceAccountKeyPath);
                if (!File.Exists(_serviceAccountKeyPath))
                    throw new FileNotFoundException(
                        $"Service account key file not found: {_serviceAccountKeyPath}",
                        _serviceAccountKeyPath);

#pragma warning disable CS0618
                await using var stream = File.OpenRead(_serviceAccountKeyPath);
                _credential = await GoogleCredential.FromStreamAsync(stream, ct);
#pragma warning restore CS0618
            }

            _credential = _credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        }

        return await _credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: ct);
    }
}
