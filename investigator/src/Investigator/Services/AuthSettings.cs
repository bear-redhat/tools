namespace Investigator.Services;

public enum AuthMode { None, Oidc, Token, TokenAndOidc }

public class AuthSettings
{
    public AuthMode Mode { get; init; }
    public bool HasOidc => Mode is AuthMode.Oidc or AuthMode.TokenAndOidc;
    public bool HasToken => Mode is AuthMode.Token or AuthMode.TokenAndOidc;
}
