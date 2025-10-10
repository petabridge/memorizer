using Memorizer.Settings;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Integration tests for CORS configuration
/// </summary>
public class CorsConfigurationTests
{
    private readonly ITestOutputHelper _output;

    public CorsConfigurationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CorsSettings_LoadsFromConfiguration()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://example.com",
                ["Cors:AllowedOrigins:1"] = "https://app.example.com",
                ["Cors:AllowedMethods:0"] = "GET",
                ["Cors:AllowedMethods:1"] = "POST",
                ["Cors:AllowedHeaders:0"] = "Content-Type",
                ["Cors:AllowCredentials"] = "false"
            })
            .Build();

        // Act
        var corsSettings = config.GetSection("Cors").Get<CorsSettings>();

        // Assert
        Assert.NotNull(corsSettings);
        Assert.Equal(2, corsSettings.AllowedOrigins.Length);
        Assert.Contains("https://example.com", corsSettings.AllowedOrigins);
        Assert.Contains("https://app.example.com", corsSettings.AllowedOrigins);
        Assert.Equal(2, corsSettings.AllowedMethods.Length);
        Assert.Contains("GET", corsSettings.AllowedMethods);
        Assert.Contains("POST", corsSettings.AllowedMethods);
        Assert.Single(corsSettings.AllowedHeaders);
        Assert.Contains("Content-Type", corsSettings.AllowedHeaders);
        Assert.False(corsSettings.AllowCredentials);

        _output.WriteLine("✓ CORS settings load correctly from configuration");
    }

    [Fact]
    public void CorsSettings_DefaultValues_AreCorrect()
    {
        // Act
        var corsSettings = new CorsSettings();
        corsSettings.ApplyDefaults();

        // Assert
        Assert.Single(corsSettings.AllowedOrigins);
        Assert.Equal("*", corsSettings.AllowedOrigins[0]);
        Assert.Single(corsSettings.AllowedMethods);
        Assert.Equal("*", corsSettings.AllowedMethods[0]);
        Assert.Single(corsSettings.AllowedHeaders);
        Assert.Equal("*", corsSettings.AllowedHeaders[0]);
        Assert.False(corsSettings.AllowCredentials);

        _output.WriteLine("✓ CORS default settings are correct");
    }

    [Fact]
    public void CorsSettings_PartialConfiguration_MergesWithDefaults()
    {
        // Arrange - Only configure origins, others should use defaults
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://trusted-domain.com",
                ["Cors:AllowedOrigins:1"] = "https://app.example.com"
            })
            .Build();

        // Act
        var corsSettings = config.GetSection("Cors").Get<CorsSettings>() ?? new CorsSettings();
        corsSettings.ApplyDefaults();

        // Assert
        Assert.Equal(2, corsSettings.AllowedOrigins.Length);
        Assert.Contains("https://trusted-domain.com", corsSettings.AllowedOrigins);
        Assert.Contains("https://app.example.com", corsSettings.AllowedOrigins);
        // Methods and Headers should still have defaults
        Assert.Single(corsSettings.AllowedMethods);
        Assert.Equal("*", corsSettings.AllowedMethods[0]);
        Assert.Single(corsSettings.AllowedHeaders);
        Assert.Equal("*", corsSettings.AllowedHeaders[0]);

        _output.WriteLine("✓ CORS partial configuration works correctly");
    }

    [Fact]
    public void CorsSettings_ProductionConfiguration_WorksCorrectly()
    {
        // Arrange - Production-like configuration
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://app.example.com",
                ["Cors:AllowedOrigins:1"] = "https://admin.example.com",
                ["Cors:AllowedMethods:0"] = "GET",
                ["Cors:AllowedMethods:1"] = "POST",
                ["Cors:AllowedMethods:2"] = "PUT",
                ["Cors:AllowedMethods:3"] = "DELETE",
                ["Cors:AllowedHeaders:0"] = "Content-Type",
                ["Cors:AllowedHeaders:1"] = "Authorization",
                ["Cors:AllowCredentials"] = "true"
            })
            .Build();

        // Act
        var corsSettings = config.GetSection("Cors").Get<CorsSettings>();

        // Assert
        Assert.NotNull(corsSettings);
        Assert.Equal(2, corsSettings.AllowedOrigins.Length);
        Assert.Contains("https://app.example.com", corsSettings.AllowedOrigins);
        Assert.Contains("https://admin.example.com", corsSettings.AllowedOrigins);
        Assert.Equal(4, corsSettings.AllowedMethods.Length);
        Assert.Contains("GET", corsSettings.AllowedMethods);
        Assert.Contains("POST", corsSettings.AllowedMethods);
        Assert.Contains("PUT", corsSettings.AllowedMethods);
        Assert.Contains("DELETE", corsSettings.AllowedMethods);
        Assert.Equal(2, corsSettings.AllowedHeaders.Length);
        Assert.Contains("Content-Type", corsSettings.AllowedHeaders);
        Assert.Contains("Authorization", corsSettings.AllowedHeaders);
        Assert.True(corsSettings.AllowCredentials);

        _output.WriteLine("✓ CORS production configuration works correctly");
    }

    [Fact]
    public void CorsSettings_EmptyConfiguration_UsesDefaults()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act
        var corsSettings = config.GetSection("Cors").Get<CorsSettings>() ?? new CorsSettings();
        corsSettings.ApplyDefaults();

        // Assert - Should have all defaults
        Assert.Single(corsSettings.AllowedOrigins);
        Assert.Equal("*", corsSettings.AllowedOrigins[0]);
        Assert.Single(corsSettings.AllowedMethods);
        Assert.Equal("*", corsSettings.AllowedMethods[0]);
        Assert.Single(corsSettings.AllowedHeaders);
        Assert.Equal("*", corsSettings.AllowedHeaders[0]);
        Assert.False(corsSettings.AllowCredentials);

        _output.WriteLine("✓ CORS empty configuration uses defaults");
    }
}
