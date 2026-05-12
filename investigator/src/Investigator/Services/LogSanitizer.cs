using System.Text.RegularExpressions;

namespace Investigator.Services;

public static partial class LogSanitizer
{
    private const string Redacted = "[REDACTED]";

    private static readonly (Regex Pattern, string Replacement)[] s_rules =
    [
        // OpenShift SA tokens: sha256~<base64>
        (OpenshiftTokenRegex(), $"sha256~{Redacted}"),

        // JWT tokens (3-part base64url)
        (JwtRegex(), Redacted),

        // oc login --token=<value>
        (OcTokenFlagRegex(), $"--token={Redacted}"),

        // Authorization: Bearer <token>
        (BearerRegex(), $"Bearer {Redacted}"),

        // GitHub tokens
        (GithubPatRegex(), Redacted),
        (GithubAppTokenRegex(), Redacted),

        // GitHub clone URLs with embedded tokens: x-access-token:<token>@
        (GitCloneTokenRegex(), $"x-access-token:{Redacted}@"),

        // AWS access key IDs
        (AwsAccessKeyRegex(), Redacted),

        // JSON fields containing secrets
        (JsonTokenFieldRegex(), "\"token\": \"[REDACTED]\""),
        (JsonAccessTokenFieldRegex(), "\"access_token\": \"[REDACTED]\""),
        (JsonRefreshTokenFieldRegex(), "\"refresh_token\": \"[REDACTED]\""),
        (JsonClientSecretFieldRegex(), "\"client_secret\": \"[REDACTED]\""),

        // API keys in query strings: key=<long-value>
        (ApiKeyQueryRegex(), $"key={Redacted}"),
    ];

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        foreach (var (pattern, replacement) in s_rules)
            input = pattern.Replace(input, replacement);

        return input;
    }

    [GeneratedRegex(@"sha256~[A-Za-z0-9_-]{10,}", RegexOptions.Compiled)]
    private static partial Regex OpenshiftTokenRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.Compiled)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"--token=\S+", RegexOptions.Compiled)]
    private static partial Regex OcTokenFlagRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]{20,}", RegexOptions.Compiled)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"gh[ps]_[A-Za-z0-9]{36,}", RegexOptions.Compiled)]
    private static partial Regex GithubPatRegex();

    [GeneratedRegex(@"(?:ghu_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.Compiled)]
    private static partial Regex GithubAppTokenRegex();

    [GeneratedRegex(@"x-access-token:[^@]+@", RegexOptions.Compiled)]
    private static partial Regex GitCloneTokenRegex();

    [GeneratedRegex(@"AKIA[A-Z0-9]{16}", RegexOptions.Compiled)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex("\"token\"\\s*:\\s*\"[^\"]{10,}\"", RegexOptions.Compiled)]
    private static partial Regex JsonTokenFieldRegex();

    [GeneratedRegex("\"access_token\"\\s*:\\s*\"[^\"]{10,}\"", RegexOptions.Compiled)]
    private static partial Regex JsonAccessTokenFieldRegex();

    [GeneratedRegex("\"refresh_token\"\\s*:\\s*\"[^\"]{10,}\"", RegexOptions.Compiled)]
    private static partial Regex JsonRefreshTokenFieldRegex();

    [GeneratedRegex("\"client_secret\"\\s*:\\s*\"[^\"]{5,}\"", RegexOptions.Compiled)]
    private static partial Regex JsonClientSecretFieldRegex();

    [GeneratedRegex(@"key=[A-Za-z0-9_-]{20,}", RegexOptions.Compiled)]
    private static partial Regex ApiKeyQueryRegex();

    // --- Layer 2: Entropy-based suspected token masking (independent of regex rules) ---

    private const string Suspected = "[SUSPECTED]";
    private const double EntropyThreshold = 4.5;
    private const int MinSegmentLength = 20;
    private const int MinCharsetClasses = 3;

    [GeneratedRegex(@"[^\s""'{}[\],:;()|<>@=]+")]
    private static partial Regex SegmentRegex();

    public static string MaskSuspected(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        return SegmentRegex().Replace(input, match =>
        {
            if (match.Length < MinSegmentLength)
                return match.Value;
            var span = match.ValueSpan;
            if (ShannonEntropy(span) <= EntropyThreshold)
                return match.Value;
            if (CharsetClasses(span) < MinCharsetClasses)
                return match.Value;
            return Suspected;
        });
    }

    private static double ShannonEntropy(ReadOnlySpan<char> s)
    {
        Span<int> counts = stackalloc int[128];
        int nonAscii = 0;
        foreach (var c in s)
        {
            if (c < 128)
                counts[c]++;
            else
                nonAscii++;
        }

        double length = s.Length;
        double entropy = 0;
        for (int i = 0; i < 128; i++)
        {
            if (counts[i] == 0) continue;
            double freq = counts[i] / length;
            entropy -= freq * Math.Log2(freq);
        }
        if (nonAscii > 0)
        {
            double freq = nonAscii / length;
            entropy -= freq * Math.Log2(freq);
        }

        return entropy;
    }

    private static int CharsetClasses(ReadOnlySpan<char> s)
    {
        bool hasUpper = false, hasLower = false, hasDigit = false, hasOther = false;
        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z') hasUpper = true;
            else if (c is >= 'a' and <= 'z') hasLower = true;
            else if (c is >= '0' and <= '9') hasDigit = true;
            else hasOther = true;
        }
        return (hasUpper ? 1 : 0) + (hasLower ? 1 : 0) + (hasDigit ? 1 : 0) + (hasOther ? 1 : 0);
    }
}
