using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TestSolution.Services
{
    /// <summary>
    /// Creates and validates time-limited, tamper-proof, upload-only share tokens.
    /// A token grants an anonymous third party the ability to upload files into a
    /// single folder prefix until it expires. It intentionally does NOT grant the
    /// ability to browse, download, or delete existing files, and it cannot be
    /// edited to widen its scope because it is HMAC-signed.
    /// </summary>
    public interface IShareLinkService
    {
        string CreateToken(string purpose, string prefix, DateTimeOffset expiresAt, int? staffId = null);
        bool TryValidateToken(string token, string expectedPurpose, out string prefix, out int? staffId);
    }

    public static class ShareLinkPurposes
    {
        public const string Upload = "upload";
        public const string Register = "register";
        public const string Terms = "terms";
    }

    public sealed class ShareLinkService : IShareLinkService
    {
        private readonly byte[] _key;
        private readonly TimeProvider _timeProvider;

        public ShareLinkService(IConfiguration configuration, TimeProvider timeProvider)
        {
            var signingKey = configuration["ShareLinks:SigningKey"];
            if (string.IsNullOrWhiteSpace(signingKey))
            {
                throw new InvalidOperationException(
                    "ShareLinks:SigningKey must be configured with a strong secret to issue share links.");
            }

            _key = Encoding.UTF8.GetBytes(signingKey);
            _timeProvider = timeProvider;
        }

        private sealed record Payload(string Prefix, long ExpiresUnix, string? Purpose = ShareLinkPurposes.Upload, int? StaffId = null);

        public string CreateToken(string purpose, string prefix, DateTimeOffset expiresAt, int? staffId = null)
        {
            var payload = new Payload(prefix ?? "", expiresAt.ToUnixTimeSeconds(), purpose, staffId);
            var json = JsonSerializer.SerializeToUtf8Bytes(payload);
            var payloadPart = Base64UrlEncode(json);
            var signature = Base64UrlEncode(Sign(payloadPart));
            return $"{payloadPart}.{signature}";
        }

        public bool TryValidateToken(string token, string expectedPurpose, out string prefix, out int? staffId)
        {
            prefix = "";
            staffId = null;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var parts = token.Split('.');
            if (parts.Length != 2)
            {
                return false;
            }

            var payloadPart = parts[0];
            byte[] providedSignature;
            try
            {
                providedSignature = Base64UrlDecode(parts[1]);
            }
            catch
            {
                return false;
            }

            var expectedSignature = Sign(payloadPart);
            if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
            {
                return false;
            }

            Payload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<Payload>(Base64UrlDecode(payloadPart));
            }
            catch
            {
                return false;
            }

            if (payload is null)
            {
                return false;
            }

            var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (payload.ExpiresUnix < now)
            {
                return false;
            }

            // Tokens issued before purposes existed default to upload-only.
            var purpose = payload.Purpose ?? ShareLinkPurposes.Upload;
            if (!string.Equals(purpose, expectedPurpose, StringComparison.Ordinal))
            {
                return false;
            }

            prefix = payload.Prefix;
            staffId = payload.StaffId;
            return true;
        }

        private byte[] Sign(string payloadPart)
        {
            using var hmac = new HMACSHA256(_key);
            return hmac.ComputeHash(Encoding.ASCII.GetBytes(payloadPart));
        }

        private static string Base64UrlEncode(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Base64UrlDecode(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Convert.FromBase64String(padded);
        }
    }
}
