namespace Investigator.Services;

/// <summary>
/// Scoped per Blazor circuit. Single source of truth for whether
/// the current circuit is authenticated, regardless of auth mode
/// (OIDC populates this from the HTTP auth state; token mode
/// populates it from the Login page).
/// </summary>
public class CircuitAuthState
{
    public bool IsAuthenticated { get; set; }
    public string? UserName { get; set; }
}
