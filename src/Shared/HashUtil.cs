using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace PCLDownloadFix
{
    public static class HashUtil
    {
        public static string Sha256File(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                return ToHex(hash);
            }
        }

        public static string Sha256Bytes(byte[] data)
        {
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(data));
        }

        public static string ToHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            const string table = "0123456789abcdef";
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = table[bytes[i] >> 4];
                chars[i * 2 + 1] = table[bytes[i] & 0xF];
            }
            return new string(chars);
        }

        public static string Short(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "";
            return hash.Length <= 16 ? hash : hash.Substring(0, 8) + "…" + hash.Substring(hash.Length - 8);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
        }
    }
}
