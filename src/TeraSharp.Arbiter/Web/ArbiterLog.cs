// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.Web;

// =============================================================================================
// ArbiterLog - T106. Two problems, one file.
//
// 1. THE CONSOLE WAS UNREADABLE. Program.cs asked for LogLevel.Debug on a console sink, and a
//    live server puts a line on it for every W->A frame. The console is now WARNING by default
//    and TERASHARP_LOG_LEVEL overrides it; everything still goes to the file at Debug, so
//    nothing is lost - it just stops scrolling past.
//
// 2. THERE WAS NO FILE AT ALL. A crash at 3am left nothing to read. This adds a daily-rolling
//    file, arbiter-yyyy-MM-dd.log under TERASHARP_LOGS, and keeps the last few hundred lines in
//    memory so the admin page's Status tab can tail it without reading the file back.
//
// It lives under Web/ because the tail is what the admin page shows; nothing here depends on
// the HTTP listener, and the logging half works with the admin tool switched off.
// =============================================================================================

/// <summary>
/// An <see cref="ILoggerProvider"/> that appends to a daily file and remembers the last
/// <see cref="TailCapacity"/> lines. One instance per process; <see cref="Tail"/> is static so
/// <c>AdminApi</c> can read it without being handed the provider.
/// </summary>
public sealed class ArbiterLogProvider : ILoggerProvider
{
    /// <summary>Overrides the CONSOLE level only. Any Microsoft.Extensions.Logging name works
    /// (Trace, Debug, Information, Warning, Error, Critical, None); anything else is ignored.</summary>
    public const string LevelVariable = "TERASHARP_LOG_LEVEL";

    /// <summary>What the console shows when the variable is unset. The file always gets Debug.</summary>
    public const LogLevel DefaultConsoleLevel = LogLevel.Warning;

    /// <summary>How many lines the in-memory tail holds. The Status tab asks for 200.</summary>
    public const int TailCapacity = 2000;

    private static readonly ConcurrentQueue<string> Lines = new();
    private static readonly object FileGate = new();
    private static string _folder = string.Empty;
    private static string _openPath = string.Empty;
    private static DateTime _openDay = DateTime.MinValue;
    private static StreamWriter? _writer;

    /// <summary>Where the file went, for the startup banner. Empty when file logging is off.</summary>
    public static string CurrentPath { get { lock (FileGate) return _openPath; } }

    public ArbiterLogProvider(string folder) => _folder = folder ?? string.Empty;

    public ILogger CreateLogger(string categoryName) => new Sink(categoryName);

    public void Dispose()
    {
        lock (FileGate) { _writer?.Flush(); _writer?.Dispose(); _writer = null; }
    }

    /// <summary>
    /// The console level for this run. Unset or unparseable gives
    /// <see cref="DefaultConsoleLevel"/> - a bad value must not silently turn logging off.
    /// </summary>
    public static LogLevel ConsoleLevel(string? envValue)
        => Enum.TryParse(envValue, ignoreCase: true, out LogLevel parsed) ? parsed : DefaultConsoleLevel;

    /// <summary>As above, against the live environment.</summary>
    public static LogLevel ConsoleLevel()
        => ConsoleLevel(TerasConfig.Get(LevelVariable));

    /// <summary>The file this day's lines go to: <c>arbiter-yyyy-MM-dd.log</c>.</summary>
    public static string FileNameFor(DateTime day)
        => "arbiter-" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";

    /// <summary>The newest <paramref name="lines"/> lines, oldest first. Never throws.</summary>
    public static IReadOnlyList<string> Tail(int lines)
    {
        if (lines < 1) lines = 1;
        var all = Lines.ToArray();
        if (all.Length <= lines) return all;
        return all[^lines..];
    }

    /// <summary>One formatted line into the ring and the file. Public so tests can drive it.</summary>
    public static void Append(string line)
    {
        Lines.Enqueue(line);
        while (Lines.Count > TailCapacity) Lines.TryDequeue(out _);

        if (_folder.Length == 0) return;
        lock (FileGate)
        {
            try
            {
                var today = DateTime.Now.Date;
                if (_writer == null || today != _openDay)
                {
                    _writer?.Flush(); _writer?.Dispose();
                    Directory.CreateDirectory(_folder);
                    _openPath = Path.Combine(_folder, FileNameFor(today));
                    _writer = new StreamWriter(
                        new FileStream(_openPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        { AutoFlush = true };
                    _openDay = today;
                }
                _writer.WriteLine(line);
            }
            catch (Exception)
            {
                // A log sink that throws takes the server with it. Drop the line and carry on;
                // the ring still has it, so the Status tab shows it either way.
                _folder = string.Empty;
                _writer = null;
            }
        }
    }

    /// <summary>The line format, shared by the file and the tail so both read the same.</summary>
    public static string Format(DateTime when, LogLevel level, string category, string message, Exception? error)
    {
        var sb = new StringBuilder(message.Length + 64);
        sb.Append(when.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append(' ').Append(Short(level)).Append(' ').Append(Leaf(category)).Append(": ").Append(message);
        if (error != null) sb.Append(" | ").Append(error.GetType().Name).Append(": ").Append(error.Message);
        return sb.ToString();

        static string Leaf(string c)
        {
            int dot = c.LastIndexOf('.');
            return dot >= 0 && dot + 1 < c.Length ? c[(dot + 1)..] : c;
        }
    }

    /// <summary>Five characters, so the levels line up in a column.</summary>
    public static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => "none ",
    };

    private sealed class Sink : ILogger
    {
        private readonly string _category;
        public Sink(string category) => _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The FILE takes everything from Debug up; the console filter is separate and set on the
        // console sink alone, which is the whole point of the split.
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            Append(Format(DateTime.Now, level, _category, formatter(state, error), error));
        }
    }
}
