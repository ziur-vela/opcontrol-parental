// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.Extensions.Logging;

namespace OpControl.Parental.Client;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileLoggerProvider(string path) => _path = path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line + Environment.NewLine);
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > 2_000_000)
                {
                    var old = _path + ".1";
                    File.Move(_path, old, overwrite: true);
                }
            }
        }
        catch
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.UtcNow:u} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }
}
