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
    /// Queries the Windows Search index for PCL executables. This is dramatically
    /// faster than walking drives and is the preferred scan path when the index is
    /// available. Any failure is reported to the caller so it can fall back to a
    /// bounded directory scan.
    /// </summary>
    public static class WindowsSearchScanner
    {
        private const string ConnectionString =
            "Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\";";

        public static WindowsSearchResult FindPclExecutables(TimeSpan timeout, CancellationToken token)
        {
            var result = new WindowsSearchResult { Paths = new List<string>() };
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
                        command.CommandText =
                            "SELECT System.ItemPathDisplay FROM SYSTEMINDEX " +
                            "WHERE System.FileName = 'Plain Craft Launcher 2.exe'";

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
                                result.Paths.Add(Path.GetFullPath(path));
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
    }
}
