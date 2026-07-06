using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PCStatsMonitor.Core.Logging;

/// <summary>
/// Application-wide logger factory accessor.
/// Named AppLog (not Log) to avoid clashing with Serilog.Log in consuming projects.
/// </summary>
public static class AppLog
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    public static void Initialize(ILoggerFactory factory) => _factory = factory;

    public static ILogger<T> For<T>() => _factory.CreateLogger<T>();

    public static ILogger ForContext(string categoryName) => _factory.CreateLogger(categoryName);
}
