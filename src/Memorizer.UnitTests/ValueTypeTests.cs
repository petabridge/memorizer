using System.Text.Json;
using Memorizer.Models.ValueTypes;

namespace Memorizer.UnitTests;

public class ValueTypeTests
{
    #region UnitInterval Tests

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void UnitInterval_ValidValues_Succeeds(double value)
    {
        var ui = new UnitInterval(value);
        Assert.Equal(value, ui.Value);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-1.0)]
    [InlineData(1.1)]
    [InlineData(2.0)]
    public void UnitInterval_InvalidValues_Throws(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnitInterval(value));
    }

    [Fact]
    public void UnitInterval_TryCreate_ValidValue_ReturnsTrue()
    {
        var result = UnitInterval.TryCreate(0.5, out var ui);

        Assert.True(result);
        Assert.Equal(0.5, ui.Value);
    }

    [Fact]
    public void UnitInterval_TryCreate_InvalidValue_ReturnsFalse()
    {
        var result = UnitInterval.TryCreate(1.5, out var ui);

        Assert.False(result);
        Assert.Equal(default, ui);
    }

    [Theory]
    [InlineData(0.0)] // lower bound is inclusive: guards `value >= 0.0` against `> 0.0`
    [InlineData(1.0)] // upper bound is inclusive: guards `value <= 1.0` against `< 1.0`
    public void UnitInterval_TryCreate_BoundariesAreInclusive(double value)
    {
        var result = UnitInterval.TryCreate(value, out var ui);

        Assert.True(result);
        Assert.Equal(value, ui.Value);
    }

    [Theory]
    [InlineData(-0.0001)] // just below the lower bound
    [InlineData(1.0001)]  // just above the upper bound
    public void UnitInterval_TryCreate_JustOutsideBounds_ReturnsFalse(double value)
    {
        var result = UnitInterval.TryCreate(value, out var ui);

        Assert.False(result);
        Assert.Equal(default, ui);
    }

    [Fact]
    public void UnitInterval_Constructor_BoundariesAreInclusive()
    {
        Assert.Equal(0.0, new UnitInterval(0.0).Value);
        Assert.Equal(1.0, new UnitInterval(1.0).Value);
    }

    [Fact]
    public void UnitInterval_Clamp_ClampsToRange()
    {
        Assert.Equal(0.0, UnitInterval.Clamp(-1.0).Value);
        Assert.Equal(1.0, UnitInterval.Clamp(2.0).Value);
        Assert.Equal(0.5, UnitInterval.Clamp(0.5).Value);
    }

    [Fact]
    public void UnitInterval_ImplicitConversionToDouble_Works()
    {
        var ui = new UnitInterval(0.7);
        double value = ui;

        Assert.Equal(0.7, value);
    }

    [Fact]
    public void UnitInterval_JsonSerialization_RoundTrips()
    {
        var original = new UnitInterval(0.75);
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<UnitInterval>(json);

        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void UnitInterval_JsonSerialization_SerializesAsNumber()
    {
        var ui = new UnitInterval(0.5);
        var json = JsonSerializer.Serialize(ui);

        Assert.Equal("0.5", json);
    }

    [Fact]
    public void UnitInterval_CompareTo_OrdersCorrectly()
    {
        var low = new UnitInterval(0.3);
        var high = new UnitInterval(0.7);

        Assert.True(low.CompareTo(high) < 0);
        Assert.True(high.CompareTo(low) > 0);
        Assert.Equal(0, low.CompareTo(low));
    }

    #endregion

    #region Confidence Tests

    [Fact]
    public void Confidence_ValidValue_Succeeds()
    {
        var conf = new Confidence(0.95);
        Assert.Equal(0.95, conf.Value);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Confidence_InvalidValue_Throws(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Confidence(value));
    }

    [Fact]
    public void Confidence_StaticValues_AreCorrect()
    {
        Assert.Equal(1.0, Confidence.Full.Value);
        Assert.Equal(0.0, Confidence.None.Value);
        Assert.Equal(0.9, Confidence.Default.Value);
    }

    [Fact]
    public void Confidence_TryCreate_ValidValue_ReturnsTrue()
    {
        var result = Confidence.TryCreate(0.5, out var conf);

        Assert.True(result);
        Assert.Equal(0.5, conf.Value);
    }

    [Fact]
    public void Confidence_TryCreate_InvalidValue_ReturnsFalse()
    {
        // Exercises the false-return branch (out = default) when the value is out of range.
        var result = Confidence.TryCreate(1.5, out var conf);

        Assert.False(result);
        Assert.Equal(default, conf);
    }

    [Fact]
    public void Confidence_ImplicitConversionToDouble_Works()
    {
        var conf = new Confidence(0.8);
        double value = conf;

        Assert.Equal(0.8, value);
    }

    [Fact]
    public void Confidence_JsonSerialization_RoundTrips()
    {
        var original = new Confidence(0.85);
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<Confidence>(json);

        Assert.Equal(original, deserialized);
    }

    #endregion

    #region SimilarityScore Tests

    [Fact]
    public void SimilarityScore_ValidValue_Succeeds()
    {
        var score = new SimilarityScore(0.75);
        Assert.Equal(0.75, score.Value);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void SimilarityScore_InvalidValue_Throws(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimilarityScore(value));
    }

    [Fact]
    public void SimilarityScore_StaticValues_AreCorrect()
    {
        Assert.Equal(1.0, SimilarityScore.Identical.Value);
        Assert.Equal(0.0, SimilarityScore.None.Value);
        Assert.Equal(0.7, SimilarityScore.DefaultThreshold.Value);
    }

    [Fact]
    public void SimilarityScore_TryCreate_ValidValue_ReturnsTrue()
    {
        var result = SimilarityScore.TryCreate(0.5, out var score);

        Assert.True(result);
        Assert.Equal(0.5, score.Value);
    }

    [Fact]
    public void SimilarityScore_TryCreate_InvalidValue_ReturnsFalse()
    {
        // Exercises the false-return branch (out = default) when the value is out of range.
        var result = SimilarityScore.TryCreate(-0.1, out var score);

        Assert.False(result);
        Assert.Equal(default, score);
    }

    [Fact]
    public void SimilarityScore_FromDistance_ConvertsCorrectly()
    {
        // Distance 0 = Similarity 1
        Assert.Equal(1.0, SimilarityScore.FromDistance(0.0).Value);

        // Distance 1 = Similarity 0
        Assert.Equal(0.0, SimilarityScore.FromDistance(1.0).Value);

        // Distance 0.3 = Similarity 0.7
        Assert.Equal(0.7, SimilarityScore.FromDistance(0.3).Value);
    }

    [Fact]
    public void SimilarityScore_ToDistance_ConvertsCorrectly()
    {
        var score = new SimilarityScore(0.7);
        Assert.Equal(0.3, score.ToDistance(), 10);
    }

    [Fact]
    public void SimilarityScore_JsonSerialization_RoundTrips()
    {
        var original = new SimilarityScore(0.85);
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SimilarityScore>(json);

        Assert.Equal(original, deserialized);
    }

    #endregion

    #region VersionNumber Tests

    [Fact]
    public void VersionNumber_ValidValue_Succeeds()
    {
        var version = new VersionNumber(5);
        Assert.Equal(5, version.Value);
    }

    [Fact]
    public void VersionNumber_Initial_IsOne()
    {
        Assert.Equal(1, VersionNumber.Initial.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void VersionNumber_InvalidValue_Throws(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VersionNumber(value));
    }

    [Fact]
    public void VersionNumber_Increment_ReturnsNextVersion()
    {
        var v1 = VersionNumber.Initial;
        var v2 = v1.Increment();
        var v3 = v2.Increment();

        Assert.Equal(1, v1.Value);
        Assert.Equal(2, v2.Value);
        Assert.Equal(3, v3.Value);
    }

    [Fact]
    public void VersionNumber_Previous_ReturnsPreviousOrNull()
    {
        var v1 = VersionNumber.Initial;
        var v3 = new VersionNumber(3);

        Assert.Null(v1.Previous());
        Assert.Equal(2, v3.Previous()!.Value.Value);
    }

    [Fact]
    public void VersionNumber_TryCreate_ValidValue_ReturnsTrue()
    {
        var result = VersionNumber.TryCreate(5, out var version);

        Assert.True(result);
        Assert.Equal(5, version.Value);
    }

    [Fact]
    public void VersionNumber_TryCreate_InvalidValue_ReturnsFalse()
    {
        var result = VersionNumber.TryCreate(0, out var version);

        Assert.False(result);
        Assert.Equal(default, version);
    }

    [Fact]
    public void VersionNumber_TryCreate_MinimumOne_IsInclusive()
    {
        // 1 is the smallest valid version: guards `value >= 1` against `value > 1`.
        var result = VersionNumber.TryCreate(1, out var version);

        Assert.True(result);
        Assert.Equal(1, version.Value);
    }

    [Fact]
    public void VersionNumber_TryParse_MinimumOne_IsInclusive()
    {
        // Same lower-bound guard on the TryParse path.
        var result = VersionNumber.TryParse("1", out var version);

        Assert.True(result);
        Assert.Equal(1, version.Value);
    }

    [Fact]
    public void VersionNumber_TryParse_ValidString_ReturnsTrue()
    {
        var result = VersionNumber.TryParse("5", out var version);

        Assert.True(result);
        Assert.Equal(5, version.Value);
    }

    [Fact]
    public void VersionNumber_TryParse_InvalidString_ReturnsFalse()
    {
        Assert.False(VersionNumber.TryParse("abc", out _));
        Assert.False(VersionNumber.TryParse("0", out _));
        Assert.False(VersionNumber.TryParse("-1", out _));
        Assert.False(VersionNumber.TryParse(null, out _));
    }

    [Fact]
    public void VersionNumber_ImplicitConversionToInt_Works()
    {
        var version = new VersionNumber(5);
        int value = version;

        Assert.Equal(5, value);
    }

    [Fact]
    public void VersionNumber_JsonSerialization_RoundTrips()
    {
        var original = new VersionNumber(10);
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<VersionNumber>(json);

        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void VersionNumber_JsonSerialization_SerializesAsNumber()
    {
        var version = new VersionNumber(5);
        var json = JsonSerializer.Serialize(version);

        Assert.Equal("5", json);
    }

    [Fact]
    public void VersionNumber_ComparisonOperators_WorkCorrectly()
    {
        var v1 = new VersionNumber(1);
        var v2 = new VersionNumber(2);
        var v2Again = new VersionNumber(2);

        Assert.True(v1 < v2);
        Assert.True(v2 > v1);
        Assert.True(v1 <= v2);
        Assert.True(v2 >= v1);
        Assert.True(v2 <= v2Again);
        Assert.True(v2 >= v2Again);
    }

    [Fact]
    public void VersionNumber_StrictOperators_AreFalseWhenEqual()
    {
        // On equal values `<` and `>` must be false. This distinguishes `<` from `<=`
        // and `>` from `>=` (the strict operators must NOT hold at equality).
        var v2 = new VersionNumber(2);
        var v2Again = new VersionNumber(2);

        Assert.False(v2 < v2Again);
        Assert.False(v2 > v2Again);
    }

    [Fact]
    public void VersionNumber_Operators_AtAdjacentValues()
    {
        // Adjacent values pin the direction of every comparison operator.
        var v2 = new VersionNumber(2);
        var v3 = new VersionNumber(3);

        Assert.True(v2 < v3);
        Assert.False(v3 < v2);
        Assert.True(v3 > v2);
        Assert.False(v2 > v3);
        Assert.True(v2 <= v3);
        Assert.False(v3 <= v2);
        Assert.True(v3 >= v2);
        Assert.False(v2 >= v3);
    }

    [Fact]
    public void VersionNumber_CompareTo_OrdersCorrectly()
    {
        var v1 = new VersionNumber(1);
        var v5 = new VersionNumber(5);

        Assert.True(v1.CompareTo(v5) < 0);
        Assert.True(v5.CompareTo(v1) > 0);
        Assert.Equal(0, v1.CompareTo(v1));
    }

    #endregion
}
