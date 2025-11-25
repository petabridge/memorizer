using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests.Logging;

public class XUnitLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _helper;
    private readonly LogLevel _logLevel;

    public XUnitLoggerProvider(ITestOutputHelper helper, LogLevel logLevel = LogLevel.Debug)
    {
        _helper = helper;
        _logLevel = logLevel;
    }

    public void Dispose()
    {
        // no-op
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new XUnitLogger(categoryName, _helper, _logLevel);
    }
}

public static class XUnitLoggerExtensions
{
    public static ILoggingBuilder AddXUnit(this ILoggingBuilder builder, ITestOutputHelper output, LogLevel minLevel = LogLevel.Debug)
    {
        builder.AddProvider(new XUnitLoggerProvider(output, minLevel));
        return builder;
    }
}
