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

    // --- Layer 2: MaskSuspected (entropy) tests ---

    [Fact]
    public void MaskSuspected_NullInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LogSanitizer.MaskSuspected(null));
    }

    [Fact]
    public void MaskSuspected_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LogSanitizer.MaskSuspected(""));
    }

    [Fact]
    public void MaskSuspected_UnknownSecretInJsonField()
    {
        var input = """{"unknown_token": "AbR7xK9mP2qL4nW8sY1vZ5tU3oJ6hF0gDcEiBaLmNpQrSw"}""";
        var result = LogSanitizer.MaskSuspected(input);
        Assert.Contains("[SUSPECTED]", result);
        Assert.DoesNotContain("AbR7xK9mP2qL", result);
        Assert.Contains("unknown_token", result);
    }

    [Fact]
    public void MaskSuspected_UnknownSecretInPlainText()
    {
        var input = "Pod uses token AbR7xK9mP2qL4nW8sY1vZ5tU3oJ6hF0gDcEiBaLmNpQrSw in namespace ci";
        var result = LogSanitizer.MaskSuspected(input);
        Assert.Contains("[SUSPECTED]", result);
        Assert.DoesNotContain("AbR7xK9mP2qL", result);
        Assert.Contains("namespace", result);
    }

    [Theory]
    [InlineData("cluster-monitoring-operator-7b4f9c8d6-xk2mn")]
    [InlineData("grafana-deployment-5f7b8c9d4-abc12")]
    [InlineData("my-service-deployment-7b4f9c8d6-xk2mn")]
    public void MaskSuspected_K8sPodNames_PassThrough(string podName)
    {
        var input = $"Pod {podName} is running";
        var result = LogSanitizer.MaskSuspected(input);
        Assert.Contains(podName, result);
        Assert.DoesNotContain("[SUSPECTED]", result);
    }

    [Fact]
    public void MaskSuspected_HexCommitSha_PassThrough()
    {
        var input = "openshift/ci-tools@a3f2b1c4d5e6f7890123456789abcdef01234567";
        var result = LogSanitizer.MaskSuspected(input);
        Assert.Contains("a3f2b1c4d5e6f7890123456789abcdef01234567", result);
        Assert.DoesNotContain("[SUSPECTED]", result);
    }

    [Theory]
    [InlineData("console-openshift-console.apps.ci.l2s4.p1.openshiftapps.com")]
    [InlineData("/var/run/secrets/kubernetes.io/serviceaccount/ca.crt")]
    [InlineData("image-registry.openshift-image-registry.svc")]
    [InlineData("app.kubernetes.io/managed-by-prometheus-operator")]
    public void MaskSuspected_InfraIdentifiers_PassThrough(string identifier)
    {
        var result = LogSanitizer.MaskSuspected(identifier);
        Assert.Contains(identifier, result);
        Assert.DoesNotContain("[SUSPECTED]", result);
    }

    [Fact]
    public void MaskSuspected_ContainerId_PassThrough()
    {
        var input = "containerd://a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";
        var result = LogSanitizer.MaskSuspected(input);
        Assert.DoesNotContain("[SUSPECTED]", result);
    }

    [Fact]
    public void MaskSuspected_ShortHighEntropy_PassThrough()
    {
        var input = "short secret xK9mP2qL is fine";
        var result = LogSanitizer.MaskSuspected(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void MaskSuspected_AwsSecretKeyValue()
    {
        var input = "AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        var result = LogSanitizer.MaskSuspected(input);
        Assert.Contains("[SUSPECTED]", result);
        Assert.Contains("AWS_SECRET_ACCESS_KEY", result);
        Assert.DoesNotContain("wJalrXUtnFEMI", result);
    }

    [Fact]
    public void MaskSuspected_ArnSegment_PassThrough()
    {
        var input = "arn:aws:sts::123456789012:assumed-role/my-role/i-0abc123def456";
        var result = LogSanitizer.MaskSuspected(input);
        Assert.Contains("assumed-role/my-role/i-0abc123def456", result);
        Assert.DoesNotContain("[SUSPECTED]", result);
    }

    [Fact]
    public void MaskSuspected_RedactedTextNotDoubleMasked()
    {
        var input = "\"token\": \"[REDACTED]\" and normal text";
        var result = LogSanitizer.MaskSuspected(input);
        Assert.DoesNotContain("[SUSPECTED]", result);
    }

    // --- Combined: Layer 1 + Layer 2 ---

    [Fact]
    public void Combined_KnownAndUnknownSecrets()
    {
        var input = """
            "token": "sha256~lc1xPDAeMC39Xoy2I1jo3bMGVSorQ4WuaA0bvdjLi1CjydiI6MtrLSXbjCINSwlc38Hcmj01i1yvYpbGR",
            "unknown": "AbR7xK9mP2qL4nW8sY1vZ5tU3oJ6hF0gDcEiBaLmNpQrSw"
            """;
        var afterLayer1 = LogSanitizer.Redact(input);
        var afterBoth = LogSanitizer.MaskSuspected(afterLayer1);

        Assert.Contains("[REDACTED]", afterBoth);
        Assert.Contains("[SUSPECTED]", afterBoth);
        Assert.DoesNotContain("lc1xPDAeMC39", afterBoth);
        Assert.DoesNotContain("AbR7xK9mP2qL", afterBoth);
    }

    [Fact]
    public void Redact_DoesNotMaskEntropyOnlySecrets_OwnerViewRegression()
    {
        var input = "Secret: AbR7xK9mP2qL4nW8sY1vZ5tU3oJ6hF0gDcEiBaLmNpQrSw";
        var result = LogSanitizer.Redact(input);
        Assert.Contains("AbR7xK9mP2qL4nW8sY1vZ5tU3oJ6hF0gDcEiBaLmNpQrSw", result);
        Assert.DoesNotContain("[SUSPECTED]", result);
    }

    [Fact]
    public void Combined_PreservesStructureAroundMaskedTokens()
    {
        var input = """
            {
                "kind": "ServiceAccount",
                "namespace": "openshift-monitoring",
                "unknown_secret": "AbR7xK9mP2qL4nW8sY1vZ5tU3oJ6hF0gDcEiBaLmNpQrSw"
            }
            """;
        var afterLayer1 = LogSanitizer.Redact(input);
        var afterBoth = LogSanitizer.MaskSuspected(afterLayer1);

        Assert.Contains("ServiceAccount", afterBoth);
        Assert.Contains("openshift-monitoring", afterBoth);
        Assert.Contains("unknown_secret", afterBoth);
        Assert.Contains("[SUSPECTED]", afterBoth);
        Assert.DoesNotContain("AbR7xK9mP2qL", afterBoth);
    }
}
