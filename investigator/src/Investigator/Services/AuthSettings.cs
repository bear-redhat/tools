namespace Investigator.Services;

public enum AuthMode { None, Oidc, Token }

public class AuthSettings
{
    public bool HasOidc { get; init; }
    public bool HasToken { get; init; }
    public bool IsEnabled => HasOidc || HasToken;
}
