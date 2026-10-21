using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCLDownloadFix
{
    public sealed class WindowsSearchResult
    {
        public IList<string> Paths { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Queries the Windows Search index for executable files and verifies them by
    /// their version resource. This is dramatically faster than walking drives and
    /// is the preferred scan path when the index is available.
    /// </summary>
    public static class WindowsSearchScanner
    {
        private const string ConnectionString =
            "Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\";";

        public static WindowsSearchResult FindPclExecutables(TimeSpan timeout, CancellationToken token,
            IEnumerable<string> scopeRoots = null)
        {
            var result = new WindowsSearchResult { Paths = new List<string>() };
            var scopes = NormalizeScopes(scopeRoots);
            try
            {
                token.ThrowIfCancellationRequested();
                using (var connection = new OleDbConnection(ConnectionString))
                {
                    connection.Open();
                    token.ThrowIfCancellationRequested();

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
                        // Searching all indexed EXEs by name would still miss renamed
                        // copies, so use the index as a file list and verify each PE's
                        // version resource. The size window keeps the candidate set small.
                        command.CommandText =
                            "SELECT System.ItemPathDisplay FROM SYSTEMINDEX " +
                            "WHERE System.FileName LIKE '%.exe' " +
                            "AND System.Size > 512000 AND System.Size < 104857600";

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                token.ThrowIfCancellationRequested();
                                var value = reader.GetValue(0);
                                if (value == null || value == DBNull.Value) continue;

                                var path = value.ToString();
                                if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                                {
                                    try { path = new Uri(path).LocalPath; }
                                    catch { continue; }
                                }

                                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                                var full = Path.GetFullPath(path);
                                if (!IsInScope(full, scopes)) continue;
                                if (!PclIdentity.IsPclExecutable(full)) continue;
                                result.Paths.Add(full);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            result.Paths = result.Paths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return result;
        }

        internal static IList<string> NormalizeScopes(IEnumerable<string> roots)
        {
            var scopes = new List<string>();
            foreach (var root in roots ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                try
                {
                    var full = Path.GetFullPath(root.Trim().Trim('"'));
                    if (File.Exists(full)) full = Path.GetDirectoryName(full) ?? full;
                    full = full.TrimEnd('\\');
                    if (full.Length == 2 && full[1] == ':') full += "\\";
                    if (!scopes.Contains(full, StringComparer.OrdinalIgnoreCase)) scopes.Add(full);
                }
                catch
                {
                }
            }
            return scopes;
        }

        internal static bool IsInScope(string path, IList<string> scopes)
        {
            if (scopes == null || scopes.Count == 0) return true;
            foreach (var scope in scopes)
            {
                if (string.Equals(path, scope, StringComparison.OrdinalIgnoreCase)) return true;
                var prefix = scope.EndsWith("\\", StringComparison.Ordinal)
                    ? scope
                    : scope + "\\";
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
