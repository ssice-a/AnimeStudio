using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.GUI
{
    /// <summary>Stable payload names include CAB identity; PathID is only unique inside a CAB.</summary>
    internal static class EiemPackageIdentity
    {
        public static string PayloadFileName(string directory, string name, string cab, long pathId, string extension)
        {
            if (string.IsNullOrWhiteSpace(cab))
                throw new InvalidDataException("A payload requires its exact source CAB identity.");
            var readable = new string((name ?? "resource").Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray()).Trim('_');
            if (readable.Length == 0) readable = "resource";
            if (readable.Length > 64) readable = readable[..64];
            var identity = cab.Replace('\\', '/').ToLowerInvariant() + ":" + pathId.ToString(CultureInfo.InvariantCulture);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            return Path.Combine(directory, readable + "_" + pathId.ToString(CultureInfo.InvariantCulture) + "_" + digest + extension);
        }

        public static string SnapshotFileName(string member)
        {
            if (string.IsNullOrWhiteSpace(member)) throw new InvalidDataException("A source snapshot requires its exact member identity.");
            var safe = new string(member.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == '_' ? c : '_').ToArray());
            if (safe == member && member.Length <= 128 && member != "." && member != "..") return member + ".bytes";
            if (safe.Length > 64) safe = safe[..64];
            return safe + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(member))) + ".bytes";
        }
    }
}
