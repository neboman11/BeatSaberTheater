using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using IPA.Logging;
using SiraUtil.Logging;
using Zenject;

namespace BeatSaberTheater.Util;

public class LoggingService(SiraLog siraLogger) : IInitializable
{
    internal SiraLog SiraLogger = siraLogger;

#if DEBUG
    private void _Log(string message, Logger.Level logLevel, string filePath, string member, int line)
    {
        var pathParts = filePath.Split('\\');
        var className = pathParts[pathParts.Length - 1].Replace(".cs", "");
        var caller = new StackFrame(3, true).GetMethod().Name;
        var prefix = $"[{caller}->{className}.{member}:{line}]: ";
        SiraLogger.Logger?.Log(logLevel, $"{prefix}{message}");
    }

    public void Debug(string message, [CallerFilePath] string filePath = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        _Log(message, Logger.Level.Debug, filePath, member, line);
    }

    public void Debug(string message, bool evenInReleaseBuild, [CallerFilePath] string filePath = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        _Log(message, Logger.Level.Debug, filePath, member, line);
    }

    public void Info(string message, [CallerFilePath] string filePath = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        _Log(message, Logger.Level.Info, filePath, member, line);
    }

    public void Warn(string message, [CallerFilePath] string filePath = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        _Log(message, Logger.Level.Warning, filePath, member, line);
    }

    public void Error(string message, [CallerFilePath] string filePath = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        _Log(message, Logger.Level.Error, filePath, member, line);
    }
#else
    private void _Log(string message, Logger.Level logLevel)
    {
        SiraLogger.Logger?.Log(logLevel, message);
    }

    public void Debug(string message, bool evenInReleaseBuild)
    {
        _Log(message, Logger.Level.Debug);
    }

    public void Info(string message)
    {
        _Log(message, Logger.Level.Info);
    }

    public void Warn(string message)
    {
        _Log(message, Logger.Level.Warning);
    }

    public void Error(string message)
    {
        _Log(message, Logger.Level.Error);
    }
#endif
    [Conditional("DEBUG")]
    public void Debug(Exception exception)
    {
        SiraLogger.Debug(exception);
    }

    [Conditional("DEBUG")]
    public void Debug(string message)
    {
        SiraLogger.Debug(message);
    }

    public void Info(Exception exception)
    {
        SiraLogger.Info(exception);
    }

    public void Warn(Exception exception)
    {
        SiraLogger.Warn(exception);
    }

    public void Error(Exception exception)
    {
        SiraLogger.Error(exception);
    }

    public void Initialize()
    {
    }
}