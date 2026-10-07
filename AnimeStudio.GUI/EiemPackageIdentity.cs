using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.GUI
{
    /// <summary>Readable package filenames; source identity remains in the manifest.</summary>
    internal static class EiemPackageIdentity
    {
        public static string PayloadFileName(string directory, string name, string cab, long pathId,
            string extension, IDictionary<string, string> usedFiles)
        {
            ArgumentNullException.ThrowIfNull(usedFiles);
            if (string.IsNullOrWhiteSpace(cab))
                throw new InvalidDataException("A payload requires its exact source CAB identity.");
            var readable = new string((name ?? "resource").Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray()).Trim('_');
            if (readable.Length == 0) readable = "resource";
            if (readable.Length > 64) readable = readable[..64];
            var deviceName = readable.ToUpperInvariant();
            if (deviceName is "CON" or "PRN" or "AUX" or "NUL" ||
                deviceName.Length == 4 && (deviceName.StartsWith("COM") || deviceName.StartsWith("LPT")) &&
                deviceName[3] >= '1' && deviceName[3] <= '9') readable = "_" + readable;
            var identity = cab.Replace('\\', '/').ToLowerInvariant() + ":" + pathId.ToString(CultureInfo.InvariantCulture);
            bool Reserve(string relative)
            {
                if (usedFiles.TryGetValue(relative, out var previous))
                {
                    if (previous == identity)
                        throw new InvalidDataException("Duplicate source payload identity: " + relative);
                    return false;
                }
                usedFiles.Add(relative, identity);
                return true;
            }
            var candidate = Path.Combine(directory, readable + extension);
            if (Reserve(candidate)) return candidate;
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..8];
            candidate = Path.Combine(directory, readable + "_" + suffix + extension);
            if (Reserve(candidate)) return candidate;
            // The short suffix is only a filename aid. Check actual collisions
            // instead of treating a truncated hash as an exact source identity.
            for (long index = 2; ; index = checked(index + 1))
            {
                candidate = Path.Combine(directory, readable + "_" + suffix + "_" +
                    index.ToString(CultureInfo.InvariantCulture) + extension);
                if (Reserve(candidate)) return candidate;
            }
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
