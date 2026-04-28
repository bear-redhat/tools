using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.SecurityToken;
using Investigator.Models;
using Microsoft.Extensions.Logging;

namespace Investigator.Services;

internal static class BedrockCredentialHelper
{
    public static AWSCredentials? Resolve(
        ProviderCredentials creds, string profileName, ILogger logger)
    {
        var accessKey = creds.AccessKeyId;
        var secretKey = creds.SecretAccessKey;
        var roleArn = creds.RoleArn;
        var sessionName = creds.RoleSessionName ?? "investigator";

        AWSCredentials? baseCreds = null;

        if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey))
        {
            logger.LogInformation("Using static AWS credentials for profile '{Profile}'", profileName);
            baseCreds = new BasicAWSCredentials(accessKey, secretKey);
        }

        if (!string.IsNullOrEmpty(roleArn))
        {
            logger.LogInformation("Configuring auto-refreshing AssumeRole for {RoleArn} (profile '{Profile}')",
                roleArn, profileName);
            var sourceCreds = baseCreds ?? DefaultAWSCredentialsIdentityResolver.GetCredentials();
            return new AssumeRoleAWSCredentials(sourceCreds, roleArn, sessionName);
        }

        if (baseCreds is not null)
            return baseCreds;

        logger.LogInformation("Using default AWS credential chain for profile '{Profile}'", profileName);
        return null;
    }
}
