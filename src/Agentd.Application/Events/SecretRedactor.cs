using System.Text.RegularExpressions;

namespace Agentd.Application.Events;

/// <summary>
/// Removes credentials from text before it is stored (event payloads come from the agent's tool output,
/// which may show committed secrets or environment values). Applied at write time, never at read time.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "«redacted»";

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = PrivateKey().Replace(text, Mask);
        text = Jwt().Replace(text, Mask);
        text = Bearer().Replace(text, m => m.Groups[1].Value + Mask);
        text = AwsKey().Replace(text, Mask);
        text = GitHubToken().Replace(text, Mask);
        text = AnthropicKey().Replace(text, Mask);
        text = ConnectionSecret().Replace(text, m => m.Groups[1].Value + Mask);
        return NamedSecret().Replace(text, m => m.Groups[1].Value + Mask + m.Groups[3].Value);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Singleline)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(\b(?:Bearer|Basic|Bot)\s+)[A-Za-z0-9._~+/=-]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\b(?:ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{20,}")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_-]{20,}")]
    private static partial Regex AnthropicKey();

    // Password=…; / Pwd=…; / AccountKey=…; / SharedAccessKey=…; in connection strings.
    [GeneratedRegex(@"((?:Password|Pwd|AccountKey|SharedAccessKey)\s*=\s*)[^;""'\s\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionSecret();

    // "apiKey": "…", token = "…", client_secret: '…' (quoted values of secret-looking names).
    [GeneratedRegex(@"((?:api[_-]?key|secret|token|password|passwd|client[_-]?secret|access[_-]?key)\\?[""']?\s*[:=]\s*\\?[""'])([^""'\\]{6,})(\\?[""'])", RegexOptions.IgnoreCase)]
    private static partial Regex NamedSecret();
}
