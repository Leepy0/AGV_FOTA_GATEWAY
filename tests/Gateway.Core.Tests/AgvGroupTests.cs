using Gateway.Core.Acs;
using Gateway.Core.Groups;

namespace Gateway.Core.Tests;

public class AgvGroupTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 9, 4, 0, 0, TimeSpan.Zero);
    static AgvAssignment A(int agv, string pf, string fl = "", string ln = "", string pr = "", string rt = "") =>
        new() { Agv = agv, Group = AgvGroup.Trimmed(pf, fl, ln, pr, rt), UpdatedAt = Now };

    [Fact]
    public void Path_and_depth()
    {
        var g = new AgvGroup("A", "2F", "", "조립", "");
        Assert.Equal(4, g.Depth);
        Assert.Equal("A / 2F / (미지정) / 조립", g.Path);
        Assert.Equal("A / 2F", g.PathTo(2));
        Assert.Equal("미분류", AgvGroup.Empty.Path);
        Assert.True(AgvGroup.Empty.IsEmpty);
    }

    [Fact]
    public void Tree_groups_by_levels_and_counts_descendants()
    {
        var root = GroupTree.Build([A(7, "A", "2F", "L1", "조립", "자재X"), A(12, "A", "2F", "L1", "검사"), A(23, "B", "3F", "L4", "포장"), A(5, "")]);
        Assert.Equal(new[] { 5 }, root.Agvs);                       // 미분류는 루트 직속
        Assert.Equal(new[] { 5, 7, 12, 23 }, root.AllAgvs);
        var a = root.Children.Single(c => c.Name == "A");
        Assert.Equal(0, a.Level); Assert.Equal(2, a.Count);
        var l1 = GroupTree.Find(root, "A / 2F / L1")!;
        Assert.Equal(2, l1.Level); Assert.Equal(new[] { 7, 12 }, l1.AllAgvs);
        Assert.Equal(new[] { 7 }, GroupTree.AgvsUnder(root, "A / 2F / L1 / 조립"));
        Assert.Empty(GroupTree.AgvsUnder(root, "없는 경로"));
        Assert.Equal("라인", l1.LevelName);
        Assert.Equal(new[] { "A", "B" }, root.Children.Select(c => c.Name));   // 이름순
    }

    [Fact]
    public void Csv_roundtrip_with_header_comments_and_quotes()
    {
        var text = "# 주석\n" + GroupCsv.Header + "\n7,A,2F,L1,조립,\"자재,X\",AGV-07\n12\tA\t2F\tL1\t검사\n0,A\nxx,A\n";
        var items = GroupCsv.Parse(text, Now, out var errors);
        Assert.Equal(2, items.Count);
        Assert.Equal("자재,X", items[0].Group.Route); Assert.Equal("AGV-07", items[0].Name); Assert.Equal("import", items[0].Source);
        Assert.Equal("검사", items[1].Group.Process); Assert.Equal("", items[1].Group.Route);
        Assert.Equal(2, errors.Count);
        var back = GroupCsv.Parse(GroupCsv.Format(items), Now, out var e2);
        Assert.Empty(e2);
        Assert.Equal(items.Select(i => i.Group), back.Select(i => i.Group));
    }

    [Fact]
    public void Inventory_mapper_parses_sample_with_default_field_map()
    {
        var r = AcsInventoryMapper.Parse(AcsInventoryMapper.SampleJson, new AcsInventoryFieldMap(), Now);
        Assert.Equal(3, r.TotalRecords); Assert.Empty(r.Warnings);
        Assert.Equal(new[] { 7, 12, 23 }, r.Items.Select(i => i.Agv));          // "AGV-12" → 12
        Assert.Equal(new AgvGroup("A제품", "2F", "L1", "조립", "자재X"), r.Items[0].Group);
        Assert.Equal("", r.Items[2].Group.Route);
        Assert.All(r.Items, i => Assert.Equal("acs", i.Source));
    }

    [Fact]
    public void Inventory_mapper_honours_field_map_with_nested_paths_and_custom_root()
    {
        const string json = """
            { "ok": true, "payload": { "vehicles": [
                { "id": "V-03", "attrs": { "family": "C", "level": 1, "lineNo": "L9" }, "zone": { "name": "도장" }, "material": "M1" },
                { "id": "V-100", "attrs": { "family": "C" } },
                { "id": "bad" }
            ] } }
            """;
        var map = new AcsInventoryFieldMap
        {
            Root = "payload.vehicles", Agv = "id", Name = "id", ProductFamily = "attrs.family", Floor = "attrs.level",
            Line = "attrs.lineNo", Process = "zone.name", Route = "material", AgvIdRegex = @"V-(\d+)",
        };
        var r = AcsInventoryMapper.Parse(json, map, Now);
        Assert.Equal(3, r.TotalRecords);
        Assert.Single(r.Items);
        Assert.Equal(3, r.Items[0].Agv);
        Assert.Equal(new AgvGroup("C", "1", "L9", "도장", "M1"), r.Items[0].Group);
        Assert.Equal(2, r.Warnings.Count);   // 100 (범위 밖), bad (번호 없음)
    }

    [Fact]
    public void Inventory_mapper_rejects_three_digit_names_with_default_regex()
    {
        var r = AcsInventoryMapper.Parse("""[{"agvId":"AGV-999"},{"agvId":"AGV 07"},{"agvId":"7"}]""", new AcsInventoryFieldMap(), Now);
        Assert.Equal(new[] { 7 }, r.Items.Select(i => i.Agv));     // 999 거부, "AGV 07" 과 "7" 은 같은 호기 → 중복 경고
        Assert.Equal(2, r.Warnings.Count);
    }

    [Fact]
    public void Inventory_mapper_reports_missing_root()
    {
        var r = AcsInventoryMapper.Parse("""{"a":1,"b":[1],"c":[2]}""", new AcsInventoryFieldMap(), Now);
        Assert.Empty(r.Items); Assert.Single(r.Warnings);
    }
}
