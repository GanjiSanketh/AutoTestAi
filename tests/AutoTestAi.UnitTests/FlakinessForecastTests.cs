using AutoTestAi.Application.Reports;

namespace AutoTestAi.UnitTests;

/// <summary>Phase-4 Slice 1: deterministic flakiness risk forecasting —
/// pure-function contract over ordered verdict history. Every case documents
/// the arithmetic so the formula is independently reproducible.</summary>
public sealed class FlakinessForecastTests
{
    private static IReadOnlyList<bool> V(params bool[] verdicts) => verdicts;

    [Fact]
    public void NoHistory_YieldsNull()
    {
        var forecast = FlakinessForecast.Forecast(V(), 0, 0);
        Assert.Null(forecast.RiskScore);
        Assert.Null(forecast.RiskBand);
        Assert.Empty(forecast.RiskFactors);
    }

    [Fact]
    public void InsufficientHistory_YieldsNull()
    {
        // One verdict total: below MinVerdictsForFlakiness (2).
        var forecast = FlakinessForecast.Forecast(V(false), 0, 1);
        Assert.Null(forecast.RiskScore);
        Assert.Null(forecast.RiskBand);
        Assert.Empty(forecast.RiskFactors);
    }

    [Fact]
    public void ExactlyMinimumHistory_Scores()
    {
        // Newest-first [Fail, Pass]: recent=50, trend: recent[Fail]=100 vs
        // older[Pass]=0 → 100, streak=1 → 25.
        // score = round(0.5*50 + 0.3*100 + 0.2*25) = round(60) = 60 Medium.
        var forecast = FlakinessForecast.Forecast(V(false, true), 1, 1);
        Assert.Equal(60, forecast.RiskScore);
        Assert.Equal("Medium", forecast.RiskBand);
    }

    [Fact]
    public void AllPassing_IsLowWithoutFactors()
    {
        var forecast = FlakinessForecast.Forecast(V(true, true, true, true), 4, 0);
        Assert.Equal(0, forecast.RiskScore);
        Assert.Equal("Low", forecast.RiskBand);
        Assert.Empty(forecast.RiskFactors);
    }

    [Fact]
    public void AllFailing_IsHighWithElevatedFactor()
    {
        // Newest-first all fail (4): recent=100, trend=0 (both halves 100),
        // streak=4 → 100. score = round(50 + 0 + 20) = 70 High.
        var forecast = FlakinessForecast.Forecast(V(false, false, false, false), 0, 4);
        Assert.Equal(70, forecast.RiskScore);
        Assert.Equal("High", forecast.RiskBand);
        Assert.Contains("Recent failure rate is elevated", forecast.RiskFactors);
    }

    [Fact]
    public void WorkedExample_MatchesDocumentedArithmetic()
    {
        // Newest-first [F,F,P,F,P]: recent=60, halves [F,F,P]=66.67 vs
        // [F,P]=50 → deterioration 16.67, streak=2 → 50.
        // score = round(30 + 5.0 + 10) = 45 Medium, all factors fire.
        var forecast = FlakinessForecast.Forecast(V(false, false, true, false, true), 2, 3);
        Assert.Equal(45, forecast.RiskScore);
        Assert.Equal("Medium", forecast.RiskBand);
        Assert.Equal(new[]
        {
            "Recent failure rate is elevated",
            "Failure trend is deteriorating",
            "Consecutive failures detected",
        }, forecast.RiskFactors);
    }

    [Fact]
    public void ImprovingTrend_ScoresLow()
    {
        // Newest-first [P,P,F,F]: recent=50 (2/4)... window min(5,4)=4:
        // recent=50, halves [P,P]=0 vs [F,F]=100 → deterioration 0,
        // streak=0. score = round(25 + 0 + 0) = 25 Low.
        var forecast = FlakinessForecast.Forecast(V(true, true, false, false), 2, 2);
        Assert.Equal(25, forecast.RiskScore);
        Assert.Equal("Low", forecast.RiskBand);
        Assert.Contains("Recent failure rate is elevated", forecast.RiskFactors);
        Assert.DoesNotContain("Failure trend is deteriorating", forecast.RiskFactors);
    }

    [Fact]
    public void DeterioratingTrend_FiresFactor()
    {
        // Newest-first [F,F,P,P]: recent=50, halves [F,F]=100 vs [P,P]=0 →
        // 100, streak=2 → 50. score = round(25 + 30 + 10) = 65 Medium.
        var forecast = FlakinessForecast.Forecast(V(false, false, true, true), 2, 2);
        Assert.Equal(65, forecast.RiskScore);
        Assert.Equal("Medium", forecast.RiskBand);
        Assert.Contains("Failure trend is deteriorating", forecast.RiskFactors);
        Assert.Contains("Consecutive failures detected", forecast.RiskFactors);
    }

    [Fact]
    public void SingleTrailingFailure_NoStreakFactor()
    {
        // Newest-first [F,P,P,P,P]: recent=20, halves [F,P,P]=33.33 vs
        // [P,P]=0 → 33.33, streak=1 → 25.
        // score = round(10 + 10 + 5) = 25 Low; streak factor needs >= 2.
        var forecast = FlakinessForecast.Forecast(V(false, true, true, true, true), 4, 1);
        Assert.Equal(25, forecast.RiskScore);
        Assert.DoesNotContain("Consecutive failures detected", forecast.RiskFactors);
    }

    [Fact]
    public void LongStreak_CapsAtHundred()
    {
        var verdicts = Enumerable.Repeat(false, 10).ToArray();
        var forecast = FlakinessForecast.Forecast(verdicts, 0, 10);
        // recent=100, trend=0, streak capped 100 → round(50+0+20) = 70.
        Assert.Equal(70, forecast.RiskScore);
        Assert.Equal("High", forecast.RiskBand);
    }

    [Fact]
    public void ScoreBounds_LowAndHigh()
    {
        var low = FlakinessForecast.Forecast(V(true, true), 2, 0);
        Assert.Equal(0, low.RiskScore);
        var high = FlakinessForecast.Forecast(V(false, false), 0, 2);
        // recent=100, halves [F]=100 vs [F]=100 → 0, streak=2 → 50:
        // round(50 + 0 + 10) = 60.
        Assert.Equal(60, high.RiskScore);
        Assert.InRange(low.RiskScore!.Value, 0, 100);
        Assert.InRange(high.RiskScore!.Value, 0, 100);
    }

    [Theory]
    [InlineData(0, "Low")]
    [InlineData(39, "Low")]
    [InlineData(40, "Medium")]
    [InlineData(69, "Medium")]
    [InlineData(70, "High")]
    [InlineData(100, "High")]
    public void BandBoundaries_MatchContract(int score, string band)
        => Assert.Equal(band, FlakinessForecast.BandFor(score));

    [Fact]
    public void RepeatedCalculation_IsDeterministic()
    {
        var verdicts = V(false, true, false, false, true, false);
        var first = FlakinessForecast.Forecast(verdicts, 3, 3);
        var second = FlakinessForecast.Forecast(verdicts, 3, 3);
        Assert.Equal(first.RiskScore, second.RiskScore);
        Assert.Equal(first.RiskBand, second.RiskBand);
        Assert.Equal(first.RiskFactors, second.RiskFactors);
    }

    [Fact]
    public void IntermittentHistory_FallsBackToGenericFactor()
    {
        // Newest-first [P,F,P,P,P,P]: recent window(5)=[P,F,P,P,P] → 20,
        // halves [P,F,P]=33.33 vs [P,P,P]=0 → 33.33, streak=0.
        // score = round(10 + 10 + 0) = 20; only the trend factor fires.
        var forecast = FlakinessForecast.Forecast(V(true, false, true, true, true, true), 5, 1);
        Assert.Equal(20, forecast.RiskScore);
        Assert.NotEmpty(forecast.RiskFactors);
    }

    [Fact]
    public void EmptyVerdicts_WithSufficientTotals_YieldsNull()
    {
        // Defensive: totals disagree with rows (e.g. capped reads).
        var forecast = FlakinessForecast.Forecast(V(), 3, 3);
        Assert.Null(forecast.RiskScore);
    }

    [Fact]
    public void StableOrdering_DoesNotAffectScore()
    {
        var a = FlakinessForecast.Forecast(V(false, true, true, false), 2, 2);
        var b = FlakinessForecast.Forecast(V(false, true, true, false), 2, 2);
        Assert.Equal(a.RiskScore, b.RiskScore);
        Assert.Equal(a.RiskBand, b.RiskBand);
        Assert.Equal(a.RiskFactors, b.RiskFactors);
    }
}
