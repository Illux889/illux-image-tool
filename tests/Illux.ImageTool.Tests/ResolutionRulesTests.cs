using System.Text.Json;
using System.Text.Json.Nodes;
using Illux.ImageTool.Options;
using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Tests;

public sealed class ResolutionRulesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-rules-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static ResolutionRule Fits(double w, double h, int min = 250, int target = 300, int max = 350) =>
        new("Small", SizeTest.FitsWithin, w, h, null, null, target, min, max);

    private static ResolutionRule All(int min = 150, int target = 200, int max = 250) =>
        new("Large", SizeTest.All, null, null, null, null, target, min, max);

    [Fact]
    public void Defaults_AreIlluxSpec()
    {
        var d = ResolutionRules.Defaults;
        Assert.Equal(2, d.Count);
        Assert.Equal(new ResolutionRule("Small", SizeTest.FitsWithin, 40, 30, null, null, 300, 250, 350), d[0]);
        Assert.Equal(new ResolutionRule("Large", SizeTest.All, null, null, null, null, 200, 150, 250), d[1]);
        Assert.Empty(ResolutionRules.Validate(d));
    }

    [Theory]
    [InlineData(40, 30, true)]
    [InlineData(30, 40, true)]
    [InlineData(38, 28, true)]
    [InlineData(40.1, 30, false)]
    [InlineData(60, 20, false)]
    [InlineData(35, 35, false)]
    public void FitsWithin_BoundaryAndRotation(double w, double h, bool expected) =>
        Assert.Equal(expected, ResolutionRules.Matches(Fits(40, 30), w, h));

    [Fact]
    public void FitsWithin_RuleStoredPortraitBehavesLikeLandscape()
    {
        Assert.True(ResolutionRules.Matches(Fits(30, 40), 40, 30));
        Assert.False(ResolutionRules.Matches(Fits(30, 40), 40.1, 30));
    }

    [Fact]
    public void LongestSide_ShortestSide_Area_Match()
    {
        var longest = new ResolutionRule("L", SizeTest.LongestSide, null, null, 50, null, 300, 250, 350);
        Assert.True(ResolutionRules.Matches(longest, 50, 10));
        Assert.True(ResolutionRules.Matches(longest, 10, 50));
        Assert.False(ResolutionRules.Matches(longest, 50.1, 10));

        var shortest = new ResolutionRule("S", SizeTest.ShortestSide, null, null, 20, null, 300, 250, 350);
        Assert.True(ResolutionRules.Matches(shortest, 20, 500));
        Assert.False(ResolutionRules.Matches(shortest, 20.1, 500));

        var area = new ResolutionRule("A", SizeTest.Area, null, null, null, 1000, 300, 250, 350);
        Assert.True(ResolutionRules.Matches(area, 40, 25));
        Assert.False(ResolutionRules.Matches(area, 40, 25.1));

        Assert.True(ResolutionRules.Matches(All(), 5000, 5000));
    }

    [Fact]
    public void Select_FirstMatchWins_AllCatchesRest()
    {
        var rules = ResolutionRules.Defaults;
        Assert.Equal("Small", ResolutionRules.Select(rules, 30, 40).Name);
        Assert.Equal("Large", ResolutionRules.Select(rules, 100, 70).Name);

        var custom = new List<ResolutionRule>
        {
            new("Tiny", SizeTest.LongestSide, null, null, 20, null, 300, 250, 350),
            new("Small", SizeTest.LongestSide, null, null, 50, null, 300, 250, 350),
            All(),
        };
        Assert.Equal("Tiny", ResolutionRules.Select(custom, 10, 20).Name);
        Assert.Equal("Small", ResolutionRules.Select(custom, 10, 30).Name);
    }

    [Fact]
    public void Matches_ToleratesFloatingPointNoiseAtTheLimit()
    {
        // Print sizes come from pixels / ppi * 2.54, so an image of exactly 40 cm can compute as 40.0000000001.
        Assert.True(ResolutionRules.Matches(Fits(40, 30), 40.0000000001, 30));
        Assert.True(ResolutionRules.Matches(Fits(40, 30), 30.0000000001, 40.0000000001));
        Assert.True(ResolutionRules.Matches(new ResolutionRule("L", SizeTest.LongestSide, null, null, 50, null, 300, 250, 350), 50.0000000001, 1));
        Assert.True(ResolutionRules.Matches(new ResolutionRule("S", SizeTest.ShortestSide, null, null, 20, null, 300, 250, 350), 20.0000000001, 500));
        Assert.True(ResolutionRules.Matches(new ResolutionRule("A", SizeTest.Area, null, null, null, 1000, 300, 250, 350), 40.0000000001, 25));
        Assert.False(ResolutionRules.Matches(Fits(40, 30), 40.001, 30));
    }

    [Fact]
    public void Select_EmptyList_ThrowsClearArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => ResolutionRules.Select([], 10, 10));
        Assert.Contains("at least one", ex.Message);
    }

    public static TheoryData<string, ResolutionRule[]> Invalid => new()
    {
        { "min>target", [Fits(40, 30, min: 301), All()] },
        { "target>max", [Fits(40, 30, max: 299), All()] },
        { "2401", [Fits(40, 30, max: 2401), All()] },
        { "zero ppi", [Fits(40, 30, min: 0, target: 0, max: 0), All()] },
        { "negative ppi", [Fits(40, 30, min: -1), All()] },
        { "size 0", [Fits(0, 30), All()] },
        { "size too big", [Fits(1001, 30), All()] },
        { "all not last", [All(), Fits(40, 30)] },
        { "two all", [All(), All()] },
        { "no all", [Fits(40, 30)] },
        { "empty list", [] },
        { "empty name", [Fits(40, 30) with { Name = " " }, All()] },
        { "area 0", [new ResolutionRule("A", SizeTest.Area, null, null, null, 0, 300, 250, 350), All()] },
        { "limit missing", [new ResolutionRule("L", SizeTest.LongestSide, null, null, null, null, 300, 250, 350), All()] },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Validate_Rejects(string _, ResolutionRule[] rules) =>
        Assert.NotEmpty(ResolutionRules.Validate(rules));

    [Fact]
    public void Json_RoundTrip_CamelCaseTests()
    {
        var path = Path.Combine(_dir, "config.json");
        ConfigStore.Save(new WatcherConfig(), path);
        var text = File.ReadAllText(path);
        Assert.Contains("\"resolutionRules\"", text);
        Assert.Contains("\"test\": \"fitsWithin\"", text);
        Assert.Contains("\"test\": \"all\"", text);
        Assert.Contains("\"widthCm\": 40", text);
        Assert.Contains("\"targetPpi\": 300", text);
        Assert.DoesNotContain("limitCm", text);
        Assert.DoesNotContain("areaCm2", text);

        var loaded = ConfigStore.Load(path);
        Assert.Equal(ResolutionRules.Defaults, loaded.ResolutionRules);

        using var doc = JsonDocument.Parse(text);
        Assert.Equal(2, doc.RootElement.GetProperty("resolutionRules").GetArrayLength());
    }

    [Fact]
    public void Load_V01Config_UsesDefaultRules()
    {
        var path = Write("""{ "folders": [], "checkForUpdates": true, "pollSeconds": 5 }""");
        Assert.Equal(ResolutionRules.Defaults, ConfigStore.Load(path).ResolutionRules);
    }

    [Fact]
    public void Load_PopulatedV01Config_SavesUnchangedApartFromAddedRules()
    {
        // Exactly what v0.1's ConfigStore.Save wrote: every property, originals null when not set.
        var v01 = """
            {
              "folders": [
                {
                  "name": "Prepress",
                  "in": "C:\\Hot\\In",
                  "out": "C:\\Hot\\Out",
                  "error": "C:\\Hot\\Error",
                  "originals": "D:\\Archive\\Originals",
                  "parameters": [ "8bit", "upsample", "output=pdf", "filter=mitchell" ],
                  "enabled": true
                },
                {
                  "name": "Second",
                  "in": "\\\\server\\share\\In",
                  "out": "\\\\server\\share\\Out",
                  "error": "\\\\server\\share\\Error",
                  "originals": null,
                  "parameters": [],
                  "enabled": false
                }
              ],
              "checkForUpdates": false,
              "pollSeconds": 12
            }
            """;
        var path = Write(v01);

        ConfigStore.Save(ConfigStore.Load(path), path);

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(saved.Remove("resolutionRules"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(v01), saved), saved.ToJsonString());
    }

    [Fact]
    public void Load_InvalidRules_ThrowsConfigException()
    {
        var path = Write("""
            { "resolutionRules": [ { "name": "Only", "test": "fitsWithin", "widthCm": 40, "heightCm": 30,
              "targetPpi": 300, "minPpi": 250, "maxPpi": 350 } ] }
            """);
        var ex = Assert.Throws<ConfigException>(() => ConfigStore.Load(path));
        Assert.Contains("all", ex.Message);
    }

    [Fact]
    public void LoadRulesForRun_Invalid_ReturnsDefaultsWithWarning()
    {
        var path = Write("""{ "resolutionRules": [] }""");
        var (rules, warning) = ConfigStore.LoadRulesForRun(path);
        Assert.Equal(ResolutionRules.Defaults, rules);
        Assert.False(string.IsNullOrWhiteSpace(warning));

        var (rules2, warning2) = ConfigStore.LoadRulesForRun(Write("{ not json"));
        Assert.Equal(ResolutionRules.Defaults, rules2);
        Assert.NotNull(warning2);
    }

    [Fact]
    public void LoadRulesForRun_Valid_ReturnsConfiguredRules()
    {
        var config = new WatcherConfig { ResolutionRules = [Fits(10, 10), All()] };
        var path = Path.Combine(_dir, "config.json");
        ConfigStore.Save(config, path);
        var (rules, warning) = ConfigStore.LoadRulesForRun(path);
        Assert.Null(warning);
        Assert.Equal(config.ResolutionRules, rules);
    }

    [Fact]
    public void LoadRulesForRun_MissingFile_DefaultsNoWarning()
    {
        var (rules, warning) = ConfigStore.LoadRulesForRun(Path.Combine(_dir, "nope.json"));
        Assert.Equal(ResolutionRules.Defaults, rules);
        Assert.Null(warning);
    }
}
