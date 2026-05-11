using Investigator.Services;

namespace Investigator.Tests;

public class LogSanitizerTests
{
    [Fact]
    public void Redact_NullInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LogSanitizer.Redact(null));
    }

    [Fact]
    public void Redact_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LogSanitizer.Redact(""));
    }

    [Fact]
    public void Redact_NoSecrets_PassesThrough()
    {
        var input = """
            NAME         SECRETS   AGE
            builder      2         365d
            default      2         365d
            deployer     2         365d
            """;
        Assert.Equal(input, LogSanitizer.Redact(input));
    }

    [Fact]
    public void Redact_OpenshiftSaToken()
    {
        var input = "found token sha256~abc123DEF456_-ghiJKL789 in output";
        var result = LogSanitizer.Redact(input);
        Assert.Contains("sha256~[REDACTED]", result);
        Assert.DoesNotContain("abc123DEF456", result);
    }

    [Fact]
    public void Redact_OpenshiftSaToken_InJsonField()
    {
        var input = "\"token\": \"sha256~abc123DEF456_-ghiJKL789\"";
        var result = LogSanitizer.Redact(input);
        Assert.Contains("[REDACTED]", result);
        Assert.DoesNotContain("abc123DEF456", result);
    }

    [Fact]
    public void Redact_Jwt()
    {
        var jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var input = $"Authorization: Bearer {jwt}";
        var result = LogSanitizer.Redact(input);
        Assert.DoesNotContain("eyJhbGci", result);
        Assert.DoesNotContain(jwt, result);
    }

    [Fact]
    public void Redact_OcTokenFlag()
    {
        var input = "oc login https://api.ci.openshift.com:6443 --token=sha256~verySecretTokenValue123";
        var result = LogSanitizer.Redact(input);
        Assert.Contains("--token=[REDACTED]", result);
        Assert.DoesNotContain("verySecretTokenValue", result);
    }

    [Fact]
    public void Redact_BearerToken()
    {
        var input = "Authorization: Bearer abcdef1234567890ABCDEF1234567890";
        var result = LogSanitizer.Redact(input);
        Assert.Contains("Bearer [REDACTED]", result);
        Assert.DoesNotContain("abcdef1234567890", result);
    }

    [Theory]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmn")]
    [InlineData("ghs_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmn")]
    public void Redact_GithubTokens(string token)
    {
        var input = $"Using token: {token}";
        var result = LogSanitizer.Redact(input);
        Assert.DoesNotContain(token, result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Redact_GithubPatToken()
    {
        var input = "token=github_pat_ABCDEF1234567890abcdef1234567890";
        var result = LogSanitizer.Redact(input);
        Assert.DoesNotContain("github_pat_ABCDEF", result);
    }

    [Fact]
    public void Redact_GitCloneUrl()
    {
        var input = "git clone https://x-access-token:ghs_abc123xyz@github.com/org/repo.git";
        var result = LogSanitizer.Redact(input);
        Assert.Contains("x-access-token:[REDACTED]@github.com", result);
        Assert.DoesNotContain("ghs_abc123xyz", result);
    }

    [Fact]
    public void Redact_AwsAccessKey()
    {
        var input = "AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE";
        var result = LogSanitizer.Redact(input);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Theory]
    [InlineData("\"token\": \"some-very-long-secret-token-value\"")]
    [InlineData("\"access_token\": \"ya29.a0ARrdaM-long-access-token\"")]
    [InlineData("\"refresh_token\": \"1//0dx-refresh-token-value-here\"")]
    [InlineData("\"client_secret\": \"GOCSPX-secretvalue\"")]
    public void Redact_JsonSecretFields(string input)
    {
        var result = LogSanitizer.Redact(input);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Redact_ApiKeyInQueryString()
    {
        var input = "GET https://customsearch.googleapis.com/v1?key=AIzaSyD-LONG_API_KEY_VALUE_HERE&cx=123";
        var result = LogSanitizer.Redact(input);
        Assert.Contains("key=[REDACTED]", result);
        Assert.DoesNotContain("AIzaSyD-LONG_API_KEY", result);
    }

    [Fact]
    public void Redact_RealisticOcGetSaOutput()
    {
        var input = """
            {
                "kind": "ServiceAccount",
                "metadata": {
                    "name": "builder",
                    "namespace": "ci"
                },
                "secrets": [
                    {
                        "name": "builder-token-abc12"
                    }
                ],
                "token": "sha256~lc1xPDAeMC39Xoy2I1jo3bMGVSorQ4WuaA0bvdjLi1CjydiI6MtrLSXbjCINSwlc38Hcmj01i1yvYpbGR"
            }
            """;
        var result = LogSanitizer.Redact(input);
        Assert.Contains("[REDACTED]", result);
        Assert.DoesNotContain("lc1xPDAeMC39", result);
        Assert.Contains("builder", result);
        Assert.Contains("ci", result);
    }

    [Fact]
    public void Redact_MultipleSecretsInSameOutput()
    {
        var input = """
            Token: sha256~firstTokenValue1234
            JWT: eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U
            GitHub: ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmn
            AWS: AKIAIOSFODNN7EXAMPLE
            """;
        var result = LogSanitizer.Redact(input);
        Assert.DoesNotContain("firstTokenValue", result);
        Assert.DoesNotContain("eyJhbGci", result);
        Assert.DoesNotContain("ghp_ABCDEF", result);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result);
    }

    [Fact]
    public void Redact_PreservesNonSensitiveContext()
    {
        var input = "Pod builder-build-1-abc is running in namespace ci on cluster build12";
        Assert.Equal(input, LogSanitizer.Redact(input));
    }

    [Fact]
    public void Redact_ShortTokenFieldNotRedacted()
    {
        var input = "\"token\": \"short\"";
        Assert.Equal(input, LogSanitizer.Redact(input));
    }
}
