using System.Security.Cryptography;
using System.Text;
using Agentd.Application.Ports;
using Microsoft.AspNetCore.DataProtection;

namespace Agentd.Host.Configuration;

/// <summary>Encrypts delegated tokens at rest with the daemon's Data Protection keys (<c>~/.agentd/keys</c>).</summary>
internal sealed class DataProtectionTokenProtector(IDataProtectionProvider provider) : ITokenProtector
{
    public const string Purpose = "agentd.ado-user-tokens";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public byte[] Protect(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return _protector.Protect(Encoding.UTF8.GetBytes(token));
    }

    public string? Unprotect(byte[] protectedToken)
    {
        ArgumentNullException.ThrowIfNull(protectedToken);
        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(protectedToken));
        }
        catch (CryptographicException)
        {
            return null;   // other keys (a new machine, lost keys): the person connects again
        }
    }
}
