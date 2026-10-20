using System;
using System.Globalization;

namespace PCLDownloadFix
{
    public enum LogLevel
    {
        Detail,
        Info,
        Step,
        Success,
        Warn,
        Error
    }

    public sealed class LogEntry
    {
        public LogEntry(LogLevel level, string message)
        {
            Time = DateTime.Now;
            Level = level;
            Message = message ?? string.Empty;
        }

        public DateTime Time { get; private set; }
        public LogLevel Level { get; private set; }
        public string Message { get; private set; }

        public string LevelText
        {
            get
            {
                switch (Level)
                {
                    case LogLevel.Detail: return "细节";
                    case LogLevel.Info: return "信息";
                    case LogLevel.Step: return "步骤";
                    case LogLevel.Success: return "成功";
                    case LogLevel.Warn: return "警告";
                    case LogLevel.Error: return "错误";
                    default: return "信息";
                }
            }
        }

        public string ToLine()
        {
            return string.Format(CultureInfo.InvariantCulture, "[{0:HH:mm:ss}] [{1}] {2}", Time, LevelText, Message);
        }
    }

    public static class Log
    {
        public static void Write(Action<LogEntry> sink, LogLevel level, string message)
        {
            if (sink != null) sink(new LogEntry(level, message));
        }
    }

    public sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }
        public PatchException(string message, Exception inner) : base(message, inner) { }
    }
}
