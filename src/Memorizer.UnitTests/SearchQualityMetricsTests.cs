using Memorizer.Services;

namespace Memorizer.UnitTests;

/// <summary>
/// Exercises the pure search-quality math in <see cref="SearchQualityMetrics"/> with
/// small ranked lists and hand-computed expected values. Every metric is checked at its
/// loop boundaries (position K and end of list), its aggregation, and its edge cases so
/// that operator/boundary mutations produce a failing assertion.
/// </summary>
public class SearchQualityMetricsTests
{
    private const double Tol = 1e-9;

    private static HashSet<string> Set(params string[] ids) => new(ids);

    #region ReciprocalRank

    [Fact]
    public void ReciprocalRank_FirstItemRelevant_ReturnsOne()
    {
        var rr = SearchQualityMetrics.ReciprocalRank(["a", "b", "c"], Set("a"));
        Assert.Equal(1.0, rr, Tol);
    }

    [Fact]
    public void ReciprocalRank_SecondItemRelevant_ReturnsOneHalf()
    {
        var rr = SearchQualityMetrics.ReciprocalRank(["a", "b", "c"], Set("b"));
        Assert.Equal(0.5, rr, Tol);
    }

    [Fact]
    public void ReciprocalRank_LastItemRelevant_UsesOneIndexedPosition()
    {
        // Third position -> 1/3. Guards the (i + 1) offset against off-by-one mutation.
        var rr = SearchQualityMetrics.ReciprocalRank(["a", "b", "c"], Set("c"));
        Assert.Equal(1.0 / 3.0, rr, Tol);
    }

    [Fact]
    public void ReciprocalRank_FirstRelevantWins_WhenMultipleRelevant()
    {
        // Both b and c are relevant; the FIRST hit (b, position 2) determines the score.
        var rr = SearchQualityMetrics.ReciprocalRank(["a", "b", "c"], Set("b", "c"));
        Assert.Equal(0.5, rr, Tol);
    }

    [Fact]
    public void ReciprocalRank_NoRelevantInList_ReturnsZero()
    {
        var rr = SearchQualityMetrics.ReciprocalRank(["a", "b", "c"], Set("x"));
        Assert.Equal(0.0, rr, Tol);
    }

    [Fact]
    public void ReciprocalRank_EmptyRankedList_ReturnsZero()
    {
        var rr = SearchQualityMetrics.ReciprocalRank([], Set("a"));
        Assert.Equal(0.0, rr, Tol);
    }

    #endregion

    #region MeanReciprocalRank

    [Fact]
    public void MeanReciprocalRank_AveragesValues()
    {
        // Average of {1.0, 0.5, 0.0} = 0.5. Chosen so Average != Min (0.0) and != first
        // element (1.0): an Average()->Min()/First() mutation would fail here.
        var mrr = SearchQualityMetrics.MeanReciprocalRank([1.0, 0.5, 0.0]);
        Assert.Equal(0.5, mrr, Tol);
    }

    [Fact]
    public void MeanReciprocalRank_DistinctFromSumAndMax()
    {
        // Average of {0.25, 0.75} = 0.5, which differs from Sum (1.0) and Max (0.75).
        var mrr = SearchQualityMetrics.MeanReciprocalRank([0.25, 0.75]);
        Assert.Equal(0.5, mrr, Tol);
    }

    [Fact]
    public void MeanReciprocalRank_EmptyList_ReturnsZero()
    {
        var mrr = SearchQualityMetrics.MeanReciprocalRank([]);
        Assert.Equal(0.0, mrr, Tol);
    }

    #endregion

    #region RecallAtK

    [Fact]
    public void RecallAtK_CountsRelevantInTopK()
    {
        // relevant = {a, c, x}; top-3 of [a,b,c,d] contains a and c => 2 of 3 relevant.
        var recall = SearchQualityMetrics.RecallAtK(["a", "b", "c", "d"], Set("a", "c", "x"), 3);
        Assert.Equal(2.0 / 3.0, recall, Tol);
    }

    [Fact]
    public void RecallAtK_ItemAtPositionK_IsIncluded()
    {
        // c sits at index 2 (position 3). With k=3 it is inside the window => recall 1.0.
        var recall = SearchQualityMetrics.RecallAtK(["a", "b", "c", "d"], Set("c"), 3);
        Assert.Equal(1.0, recall, Tol);
    }

    [Fact]
    public void RecallAtK_ItemJustBeyondK_IsExcluded()
    {
        // Same c at index 2, but k=2 stops before it => recall 0.0. Together with the test
        // above this pins the `i < topK` loop bound against off-by-one mutation.
        var recall = SearchQualityMetrics.RecallAtK(["a", "b", "c", "d"], Set("c"), 2);
        Assert.Equal(0.0, recall, Tol);
    }

    [Fact]
    public void RecallAtK_KLargerThanList_UsesListLength()
    {
        // k=10 but only 2 results; both relevant => 2/2 = 1.0. Guards Math.Min(k, count).
        var recall = SearchQualityMetrics.RecallAtK(["a", "b"], Set("a", "b"), 10);
        Assert.Equal(1.0, recall, Tol);
    }

    [Fact]
    public void RecallAtK_NoRelevantSet_ReturnsOneVacuously()
    {
        var recall = SearchQualityMetrics.RecallAtK(["a", "b"], Set(), 5);
        Assert.Equal(1.0, recall, Tol);
    }

    [Fact]
    public void RecallAtK_NoneFoundInTopK_ReturnsZero()
    {
        var recall = SearchQualityMetrics.RecallAtK(["a", "b", "c"], Set("z"), 2);
        Assert.Equal(0.0, recall, Tol);
    }

    #endregion

    #region HitRateAtK

    [Fact]
    public void HitRateAtK_RelevantAtPositionK_ReturnsOne()
    {
        var hit = SearchQualityMetrics.HitRateAtK(["a", "b", "c"], Set("c"), 3);
        Assert.Equal(1.0, hit, Tol);
    }

    [Fact]
    public void HitRateAtK_RelevantJustBeyondK_ReturnsZero()
    {
        // c at index 2, k=2 -> outside the window -> 0.0 (off-by-one guard).
        var hit = SearchQualityMetrics.HitRateAtK(["a", "b", "c"], Set("c"), 2);
        Assert.Equal(0.0, hit, Tol);
    }

    [Fact]
    public void HitRateAtK_NoRelevant_ReturnsZero()
    {
        var hit = SearchQualityMetrics.HitRateAtK(["a", "b", "c"], Set("z"), 3);
        Assert.Equal(0.0, hit, Tol);
    }

    [Fact]
    public void HitRateAtK_EmptyRankedList_ReturnsZero()
    {
        var hit = SearchQualityMetrics.HitRateAtK([], Set("a"), 3);
        Assert.Equal(0.0, hit, Tol);
    }

    [Fact]
    public void HitRateAtK_KLargerThanList_StillFindsHit()
    {
        var hit = SearchQualityMetrics.HitRateAtK(["a", "b"], Set("b"), 10);
        Assert.Equal(1.0, hit, Tol);
    }

    #endregion

    #region NdcgAtK - binary relevance

    [Fact]
    public void NdcgAtK_BinaryRelevance_ImperfectRanking()
    {
        // ranked = [a, b, c], relevant = {a, c}.
        // DCG  = 1/log2(2) + 0 + 1/log2(4) = 1 + 0 + 0.5 = 1.5
        // IDCG = 1/log2(2) + 1/log2(3)     = 1 + 0.6309298 = 1.6309298
        // NDCG = 1.5 / 1.6309298 = 0.9197207891481876
        var ndcg = SearchQualityMetrics.NdcgAtK(["a", "b", "c"], Set("a", "c"), 3);
        Assert.Equal(0.9197207891481876, ndcg, Tol);
    }

    [Fact]
    public void NdcgAtK_PerfectRanking_ReturnsOne()
    {
        // Relevant items ranked first (a, c) -> DCG == IDCG -> 1.0.
        var ndcg = SearchQualityMetrics.NdcgAtK(["a", "c", "b"], Set("a", "c"), 3);
        Assert.Equal(1.0, ndcg, Tol);
    }

    [Fact]
    public void NdcgAtK_LogDiscount_SingleRelevantAtSecondPosition()
    {
        // ranked = [a, b], relevant = {b}. DCG = 0 + 1/log2(3) = 0.6309298.
        // IDCG (1 relevant, placed first) = 1/log2(2) = 1.0. NDCG = 0.6309298.
        var ndcg = SearchQualityMetrics.NdcgAtK(["a", "b"], Set("b"), 2);
        Assert.Equal(1.0 / Math.Log2(3), ndcg, Tol);
    }

    [Fact]
    public void NdcgAtK_EmptyRankedList_ReturnsZero()
    {
        // DCG = 0 but IDCG > 0 (one relevant item), so NDCG = 0, not the idcg==0 shortcut.
        var ndcg = SearchQualityMetrics.NdcgAtK([], Set("a"), 5);
        Assert.Equal(0.0, ndcg, Tol);
    }

    [Fact]
    public void NdcgAtK_NoRelevantItems_ReturnsZeroViaIdcgZero()
    {
        // Empty relevant set -> IDCG == 0 -> early return 0.0 (division-by-zero guard).
        var ndcg = SearchQualityMetrics.NdcgAtK(["a", "b", "c"], Set(), 3);
        Assert.Equal(0.0, ndcg, Tol);
    }

    [Fact]
    public void NdcgAtK_KLargerThanList_UsesAvailableResults()
    {
        // ranked = [a, b], relevant = {a, b}, k=10. DCG uses both, IDCG uses both -> 1.0.
        var ndcg = SearchQualityMetrics.NdcgAtK(["a", "b"], Set("a", "b"), 10);
        Assert.Equal(1.0, ndcg, Tol);
    }

    [Fact]
    public void NdcgAtK_IdealRankingBeatsPoorRanking()
    {
        // A ranking that buries the relevant item scores strictly below a perfect one.
        var good = SearchQualityMetrics.NdcgAtK(["a", "b", "c"], Set("a"), 3);
        var bad = SearchQualityMetrics.NdcgAtK(["b", "c", "a"], Set("a"), 3);
        Assert.Equal(1.0, good, Tol);
        Assert.True(bad < good);
        Assert.Equal(1.0 / Math.Log2(4), bad, Tol); // a at index 2 -> 1/log2(4) = 0.5
    }

    #endregion

    #region NdcgAtK - graded relevance

    [Fact]
    public void NdcgAtK_GradedRelevance_UsesGradesAndIdealOrdering()
    {
        // ranked = [b, a, c]; graded = {a:3, b:2, c:1}; relevant = {a, b, c}.
        // DCG  = 2/log2(2) + 3/log2(3) + 1/log2(4) = 2 + 1.8927893 + 0.5 = 4.3927893
        // Ideal orders grades descending [3,2,1]:
        // IDCG = 3/log2(2) + 2/log2(3) + 1/log2(4) = 3 + 1.2618595 + 0.5 = 4.7618595
        // NDCG = 4.3927893 / 4.7618595 = 0.9224945116765986
        var graded = new Dictionary<string, double> { ["a"] = 3.0, ["b"] = 2.0, ["c"] = 1.0 };
        var ndcg = SearchQualityMetrics.NdcgAtK(["b", "a", "c"], Set("a", "b", "c"), 3, graded);
        Assert.Equal(0.9224945116765986, ndcg, Tol);
    }

    [Fact]
    public void NdcgAtK_GradedRelevance_PerfectOrderReturnsOne()
    {
        // Highest grade first -> DCG == IDCG -> 1.0. Guards OrderDescending in the ideal.
        var graded = new Dictionary<string, double> { ["a"] = 3.0, ["b"] = 2.0, ["c"] = 1.0 };
        var ndcg = SearchQualityMetrics.NdcgAtK(["a", "b", "c"], Set("a", "b", "c"), 3, graded);
        Assert.Equal(1.0, ndcg, Tol);
    }

    [Fact]
    public void NdcgAtK_GradedRelevance_MissingGradeDefaultsToOneInIdeal()
    {
        // "d" is relevant but absent from graded map: ideal treats it as grade 1.0.
        // ranked = [d], relevant = {a, d}, graded = {a:3}.
        // DCG  = relevance(d) = 1.0 (falls back to relevantSet) / log2(2) = 1.0
        // Ideal scores = [a->3, d->1] sorted desc = [3, 1]
        // IDCG = 3/log2(2) + 1/log2(3) = 3 + 0.6309298 = 3.6309298
        // NDCG = 1.0 / 3.6309298
        var graded = new Dictionary<string, double> { ["a"] = 3.0 };
        var ndcg = SearchQualityMetrics.NdcgAtK(["d"], Set("a", "d"), 5, graded);
        var expected = 1.0 / (3.0 + 1.0 / Math.Log2(3));
        Assert.Equal(expected, ndcg, Tol);
    }

    #endregion
}
