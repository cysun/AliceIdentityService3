using System.Security.Cryptography;
using System.Text;

namespace AliceIdentityService.Services;

// An optimistic-edit version, not an authentication credential. Never expose the raw stamp.
public static class AdminUserVersion
{
    public static string For(string id, string? concurrencyStamp) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + "\0" + concurrencyStamp)));
}
