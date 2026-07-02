using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class SeriesOrderPlannerTests
{
    [Fact]
    public void Move_ForwardMove_ShiftsItemTowardTheEnd()
    {
        var result = SeriesOrderPlanner.Move(new[] { "a", "b", "c", "d" }, fromIndex: 0, toIndex: 2);

        Assert.Equal(new[] { "b", "c", "a", "d" }, result);
    }

    [Fact]
    public void Move_BackwardMove_ShiftsItemTowardTheStart()
    {
        var result = SeriesOrderPlanner.Move(new[] { "a", "b", "c", "d" }, fromIndex: 3, toIndex: 1);

        Assert.Equal(new[] { "a", "d", "b", "c" }, result);
    }

    [Fact]
    public void Move_SameIndex_IsNoOp()
    {
        var source = new[] { "a", "b", "c" };
        var result = SeriesOrderPlanner.Move(source, fromIndex: 1, toIndex: 1);

        Assert.Equal(source, result);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(0, 99)]
    [InlineData(99, 0)]
    public void Move_OutOfRangeIndex_IsNoOp(int fromIndex, int toIndex)
    {
        var source = new[] { "a", "b", "c" };
        var result = SeriesOrderPlanner.Move(source, fromIndex, toIndex);

        Assert.Equal(source, result);
    }

    [Fact]
    public void Move_DoesNotMutateSourceList()
    {
        var source = new List<string> { "a", "b", "c" };
        SeriesOrderPlanner.Move(source, fromIndex: 0, toIndex: 2);

        Assert.Equal(new[] { "a", "b", "c" }, source);
    }

    [Fact]
    public void FlattenInDisplayOrder_AllKeysEqual_PreservesPhysicalEnumerationOrder()
    {
        var groups = new[]
        {
            new[] { "a1", "a2" },
            new[] { "b1", "b2", "b3" },
        };

        var flat = SeriesOrderPlanner.FlattenInDisplayOrder(groups, static _ => 0);

        Assert.Equal(new[] { "a1", "a2", "b1", "b2", "b3" }, flat);
    }

    [Fact]
    public void FlattenInDisplayOrder_SortsByKeyAscending()
    {
        var groups = new[] { new[] { (Key: 3, Name: "c"), (Key: 1, Name: "a"), (Key: 2, Name: "b") } };

        var flat = SeriesOrderPlanner.FlattenInDisplayOrder(groups, static item => item.Key);

        Assert.Equal(new[] { "a", "b", "c" }, flat.Select(static item => item.Name));
    }

    [Fact]
    public void FlattenInDisplayOrder_SameKeyGroup_KeepsPhysicalOrderWithinTheGroup()
    {
        // 3 件とも DisplayOrder=0 の中で、別グループの DisplayOrder=1 な要素と
        // 混ぜても、0 同士は物理列挙順 (安定ソート) を保ったまま前に来る。
        var groups = new[]
        {
            new[] { (Order: 0, Name: "first"), (Order: 0, Name: "second") },
            new[] { (Order: 1, Name: "third") },
            new[] { (Order: 0, Name: "fourth") },
        };

        var flat = SeriesOrderPlanner.FlattenInDisplayOrder(groups, static item => item.Order);

        Assert.Equal(new[] { "first", "second", "fourth", "third" }, flat.Select(static item => item.Name));
    }

    [Fact]
    public void FlattenInDisplayOrder_MultipleGroupsAllDefaultOrder_MatchesNestedLoopEnumerationOrder()
    {
        // 回帰テスト: GetSeriesAutoColorIndex の旧実装 (先行テーブル全列数の
        // 累積 + テーブル内 index) と、新実装 (フラット表示順の通し番号) の
        // 同値性を、DisplayOrder=0 (未採番) のケースで検証する。
        var tables = new[]
        {
            new[] { "t0s0", "t0s1", "t0s2" },
            new[] { "t1s0" },
            new[] { "t2s0", "t2s1" },
        };

        var nestedLoopOrder = new List<string>();
        foreach (var table in tables)
        {
            foreach (var series in table)
            {
                nestedLoopOrder.Add(series);
            }
        }

        var flat = SeriesOrderPlanner.FlattenInDisplayOrder(tables, static _ => 0);

        Assert.Equal(nestedLoopOrder, flat);
    }
}
