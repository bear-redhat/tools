namespace Investigator.Services;

public enum AuthMode { None, Oidc, Token }

public class AuthSettings
{
    public AuthMode Mode { get; init; }
}
