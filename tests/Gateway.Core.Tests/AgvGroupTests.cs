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
        Assert.Empty(root.Agvs);                                     // 미분류는 "미분류" 노드로
        Assert.Equal(new[] { 5 }, GroupTree.AgvsUnder(root, "미분류"));
        Assert.Equal(new[] { 5, 7, 12, 23 }, root.AllAgvs);
        var a = root.Children.Single(c => c.Name == "A");
        Assert.Equal(0, a.Level); Assert.Equal(2, a.Count);
        var l1 = GroupTree.Find(root, "A / 2F / L1")!;
        Assert.Equal(2, l1.Level); Assert.Equal(new[] { 7, 12 }, l1.AllAgvs);
        Assert.Equal(new[] { 7 }, GroupTree.AgvsUnder(root, "A / 2F / L1 / 조립"));
        Assert.Empty(GroupTree.AgvsUnder(root, "없는 경로"));
        Assert.Equal("라인", l1.LevelName);
        Assert.Equal(new[] { "A", "B", "미분류" }, root.Children.Select(c => c.Name));   // 이름순
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

    // ── G-01: 단계 값 안의 '/' 가 경로 구분자와 겹치지 않음 ──
    [Fact]
    public void Slash_inside_level_value_does_not_collide_with_deeper_path()
    {
        var root = GroupTree.Build([A(3, "P", "2F", "L1", "조립 / 검사"), A(4, "P", "2F", "L1", "조립", "검사")]);
        var p3 = AgvGroup.Trimmed("P", "2F", "L1", "조립 / 검사", "").Path;
        var p4 = AgvGroup.Trimmed("P", "2F", "L1", "조립", "검사").Path;
        Assert.NotEqual(p3, p4);
        Assert.Equal(new[] { 3 }, GroupTree.AgvsUnder(root, p3));
        Assert.Equal(new[] { 4 }, GroupTree.AgvsUnder(root, p4));
        Assert.Empty(GroupTree.DuplicatePaths(root));
        // 배포 화면은 경로를 " / " 로 쪼개 단계별 선택에 채운다 — 단계 수가 그대로여야 함
        Assert.Equal(4, p3.Split(AgvGroup.Separator).Length);
        Assert.Equal("조립 ／ 검사", p3.Split(AgvGroup.Separator)[3]);
        // 쪼갠 단계를 다시 이어도 같은 노드
        Assert.Equal(new[] { 3 }, GroupTree.AgvsUnder(root, string.Join(AgvGroup.Separator, p3.Split(AgvGroup.Separator))));
    }

    [Fact]
    public void Slash_at_trim_boundary_does_not_collide()
    {
        var root = GroupTree.Build([A(7, "A /", "B"), A(12, "A", "/ B"), A(20, "A / B")]);
        var paths = new[] { A(7, "A /", "B"), A(12, "A", "/ B"), A(20, "A / B") }.Select(a => a.Group.Path).ToList();
        Assert.Equal(3, paths.Distinct().Count());
        Assert.Equal(new[] { 7 }, GroupTree.AgvsUnder(root, paths[0]));
        Assert.Equal(new[] { 12 }, GroupTree.AgvsUnder(root, paths[1]));
        Assert.Equal(new[] { 20 }, GroupTree.AgvsUnder(root, paths[2]));
        Assert.Empty(GroupTree.DuplicatePaths(root));
    }

    [Fact]
    public void Input_paths_all_normalize_slash()
    {
        Assert.Equal("A／B", AgvGroup.Trimmed(" A/B ", null, null, null, null).ProductFamily);
        Assert.Equal("A／B", new AgvGroup("A/B").Cleaned().ProductFamily);
        // 예전 DB 값('/' 그대로)도 트리 경로에서는 '／'
        var root = GroupTree.Build([new AgvAssignment { Agv = 1, Group = new AgvGroup("A/B") }, A(2, "A", "B")]);
        Assert.Equal(new[] { 1 }, GroupTree.AgvsUnder(root, "A／B"));
        Assert.Equal(new[] { 2 }, GroupTree.AgvsUnder(root, "A / B"));
        // CSV 가져오기·ACS 받기
        var csv = GroupCsv.Parse("5,조립/검사,2F", Now, out _);
        Assert.Equal("조립／검사", csv[0].Group.ProductFamily);
        var acs = AcsInventoryMapper.Parse("""[{"agvId":6,"process":"조립 / 검사"}]""", new AcsInventoryFieldMap(), Now);
        Assert.Equal("조립 ／ 검사", acs.Items[0].Group.Process);
    }

    [Fact]
    public void Duplicate_paths_are_reported()
    {
        var root = new GroupNode { Level = -1, Path = "" };
        root.Children.Add(new GroupNode { Level = 0, Name = "x", Path = "X / Y" });
        root.Children.Add(new GroupNode { Level = 0, Name = "y", Path = "X / Y" });
        Assert.Equal(new[] { "X / Y" }, GroupTree.DuplicatePaths(root));
    }

    // ── G-02: '미분류' 경로가 소속 없는 호기를 가리킴 ──
    [Fact]
    public void Unclassified_path_finds_agvs_without_group()
    {
        var root = GroupTree.Build([A(5, ""), A(7, "A")]);
        Assert.Equal("미분류", AgvGroup.Empty.Path);
        Assert.Equal(new[] { 5 }, GroupTree.AgvsUnder(root, AgvGroup.Empty.Path));
        // 값이 "미분류" 인 제품군과 같은 노드 — 같은 이름으로 보이는 두 호기가 함께 잡힘 (한쪽이 빠지지 않음)
        var root2 = GroupTree.Build([A(5, ""), A(9, "미분류")]);
        Assert.Equal(new[] { 5, 9 }, GroupTree.AgvsUnder(root2, AgvGroup.Empty.Path));
        Assert.Single(root2.Children);
    }

    // ── X-11: CSV 내보내기 수식 중화·줄바꿈 묶음 ──
    [Fact]
    public void Csv_export_neutralizes_formulas_and_quotes_newlines()
    {
        var items = new List<AgvAssignment>
        {
            new() { Agv = 7, Name = "=HYPERLINK(\"http://x/?\"&B2,\"7호기\")", Group = new AgvGroup("+A", "-1", "@L", "조립", "") },
            new() { Agv = 8, Name = "줄1\n줄2", Group = new AgvGroup("A,B", "2F") },
            new() { Agv = 9, Name = "'그대로", Group = new AgvGroup("=cmd|' /C calc'!A0") },
        };
        var csv = GroupCsv.Format(items);
        var lines = csv.Split('\n');
        Assert.StartsWith("7,'+A,'-1,'@L,조립,,\"'=HYPERLINK(", lines[1]);
        Assert.Contains("\"줄1\n줄2\"", csv);
        Assert.StartsWith("8,\"A,B\",2F", lines[2]);
        Assert.DoesNotContain(lines, l => l.Length > 0 && (l.Contains(",=") || l.Contains(",+") || l.Contains(",-") || l.Contains(",@")));
        var back = GroupCsv.Parse(csv, Now, out var errors);
        Assert.Empty(errors);
        Assert.Equal(items.Select(i => i.Agv), back.Select(i => i.Agv));
        Assert.Equal(items.Select(i => i.Name), back.Select(i => i.Name));
        Assert.Equal(new AgvGroup("+A", "-1", "@L", "조립", ""), back[0].Group);
        Assert.Equal(new AgvGroup("A,B", "2F"), back[1].Group);
        Assert.Equal("=cmd|' ／C calc'!A0", back[2].Group.ProductFamily);   // '/' 는 가져올 때 '／'
    }

    // ── S-04 / I-04: 오류 응답·빈 목록은 반영 거부, 대량 삭제 보류 ──
    [Theory]
    [InlineData("""{"success":false,"message":"db error"}""")]
    [InlineData("""{"result":"error","message":"busy"}""")]
    [InlineData("""{"agvs":[]}""")]
    [InlineData("""[{"vehicleId":7},{"vehicleId":8}]""")]
    public void Inventory_error_or_empty_response_is_rejected(string json)
    {
        var r = AcsInventoryMapper.Parse(json, new AcsInventoryFieldMap(), Now);
        Assert.NotNull(r.RejectReason);
    }

    [Fact]
    public void Inventory_good_response_is_accepted_and_missing_root_flagged()
    {
        Assert.Null(AcsInventoryMapper.Parse(AcsInventoryMapper.SampleJson, new AcsInventoryFieldMap(), Now).RejectReason);
        var r = AcsInventoryMapper.Parse("""{"success":false}""", new AcsInventoryFieldMap(), Now);
        Assert.False(r.RootFound);
        Assert.Contains("찾지 못함", r.RejectReason);
    }

    static Dictionary<int, AgvAssignment> Acs(params int[] agvs) =>
        agvs.ToDictionary(n => n, n => new AgvAssignment { Agv = n, Group = new AgvGroup("A"), Source = "acs" });

    [Fact]
    public void Merge_holds_mass_removal()
    {
        var cur = Acs(1, 2, 3, 4);
        var plan = AcsMerge.Plan(cur, [new AgvAssignment { Agv = 1, Group = new AgvGroup("A") }], overrideManual: false, removeMissing: true);
        Assert.Empty(plan.Removes);
        Assert.Equal(new[] { 2, 3, 4 }, plan.Held);
    }

    [Fact]
    public void Merge_removes_few_missing_and_keeps_manual()
    {
        var cur = Acs(1, 2, 3, 4);
        cur[9] = new AgvAssignment { Agv = 9, Group = new AgvGroup("M"), Source = "manual" };
        var from = new List<AgvAssignment> { new() { Agv = 1, Group = new AgvGroup("A") }, new() { Agv = 2, Group = new AgvGroup("B") }, new() { Agv = 3, Group = new AgvGroup("A") },
                                             new() { Agv = 9, Group = new AgvGroup("X") } };
        var plan = AcsMerge.Plan(cur, from, overrideManual: false, removeMissing: true);
        Assert.Equal(new[] { 4 }, plan.Removes);          // 4대 중 1대 → 삭제
        Assert.Empty(plan.Held);
        Assert.Equal(new[] { 2 }, plan.Upserts.Select(a => a.Agv));   // 바뀐 것만, manual(9) 은 유지
        Assert.Empty(AcsMerge.Plan(cur, from, false, removeMissing: false).Removes);
    }
}
