using LogLens.Core.Filtering;
using LogLens.Core.Models;

namespace LogLens.Core.Tests;

public class PropertyQueryTests
{
    private static readonly LogEvent[] Events =
    [
        Make(0, "Order 1", "Order {Id}", ("Id", 1L), ("Customer", "Ann"), ("Elapsed", 12.5), ("Ok", true), ("SourceContext", "Shop.Orders")),
        Make(1, "Order 2", "Order {Id}", ("Id", 2L), ("Customer", "bob"), ("Elapsed", 900.0), ("Ok", false), ("SourceContext", "Shop.Payments")),
        Make(2, "Http", "HTTP {Http}", ("Http", new Dictionary<string, LogValue> { ["Status"] = new(500L), ["Path"] = new("/api") }), ("Request Id", "r-1"), ("Count", "42")),
        Make(3, "Plain text", null),
        Make(4, "Nulls", "Nulls", ("Customer", null)),
    ];

    private static LogEvent Make(int id, string message, string? template, params (string Name, object? Value)[] properties)
        => new(
            id,
            null,
            0,
            0,
            new DateTimeOffset(2026, 11, 13, 8, 0, id, TimeSpan.Zero),
            LogLevel.Information,
            message,
            null,
            template,
            properties.Length == 0 ? null : properties.ToDictionary(p => p.Name, p => new LogValue(p.Value)));

    private static int[] Ids(string query)
    {
        Assert.True(QueryParser.TryParse(query, out var compiled, out var error), $"{query}: {error.Message}");
        return [.. Events.Where(compiled!.Predicate).Select(e => e.Id)];
    }

    [Theory]
    [InlineData("Id = 1", new[] { 0 })]
    [InlineData("Id = '2'", new[] { 1 })]
    [InlineData("Id != 1", new[] { 1 })]
    [InlineData("Id in [1, 2]", new[] { 0, 1 })]
    [InlineData("Customer = 'ANN'", new[] { 0 })]
    [InlineData("Customer like 'b%'", new[] { 1 })]
    [InlineData("Customer contains 'n'", new[] { 0 })]
    [InlineData("Customer in ['ann', 'nobody']", new[] { 0 })]
    [InlineData("Elapsed > 100", new[] { 1 })]
    [InlineData("Elapsed <= 12.5", new[] { 0 })]
    [InlineData("Elapsed >= '13'", new[] { 1 })]
    [InlineData("Ok = true", new[] { 0 })]
    [InlineData("Ok != true", new[] { 1 })]
    [InlineData("Ok = false", new[] { 1 })]
    [InlineData("SourceContext startswith 'Shop.P'", new[] { 1 })]
    [InlineData("Http.Status = 500", new[] { 2 })]
    [InlineData("Http.Path = '/api'", new[] { 2 })]
    [InlineData("Http.Missing = 1", new int[0])]
    [InlineData("@p['Request Id'] = 'r-1'", new[] { 2 })]
    [InlineData("Count > 40", new[] { 2 })]
    [InlineData("Customer = null", new[] { 2, 3, 4 })]
    [InlineData("Customer != null", new[] { 0, 1 })]
    [InlineData("exists(Customer)", new[] { 0, 1 })]
    [InlineData("not exists(Id)", new[] { 2, 3, 4 })]
    [InlineData("Customer > 'azz'", new[] { 1 })]
    [InlineData("@mt = 'Order {Id}'", new[] { 0, 1 })]
    [InlineData("@mt like 'HTTP%'", new[] { 2 })]
    [InlineData("exists(@mt)", new[] { 0, 1, 2, 4 })]
    [InlineData("@mt = null", new[] { 3 })]
    [InlineData("Id >= 1 and not (Customer = 'Ann')", new[] { 1 })]
    [InlineData("Http = '{ \"Status\": 500, \"Path\": \"/api\" }'", new[] { 2 })]
    public void Properties_can_be_queried(string query, int[] expected)
    {
        Assert.Equal(expected, Ids(query));
    }

    [Fact]
    public void Missing_properties_match_no_comparison()
    {
        Assert.Empty(Ids("Nope = 1"));
        Assert.Empty(Ids("Nope != 1"));
        Assert.Empty(Ids("Nope > 1"));
        Assert.Empty(Ids("Nope like '%'"));
        Assert.Equal([0, 1, 2, 3, 4], Ids("not (Nope = 1)"));
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Request Id")]
    [InlineData("in")]
    [InlineData("Http.Status")]
    [InlineData("_x1")]
    [InlineData("1st")]
    [InlineData("it's")]
    public void Writer_names_any_property_so_the_parser_reads_it_back(string name)
    {
        var events = new[] { Make(0, "m", "t", (name, "it's \"here\"")) };
        var query = QueryWriter.PropertyEquals(name, new LogValue("it's \"here\""));

        Assert.True(QueryParser.TryParse(query, out var compiled, out var error), $"{query}: {error.Message}");
        Assert.Single(events, e => compiled!.Predicate(e));
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData(true, "true")]
    [InlineData(42L, "42")]
    [InlineData(1.5, "1.5")]
    [InlineData(1e300, "'1E+300'")]
    [InlineData("O'Hara", "'O''Hara'")]
    public void Writer_writes_literals_the_parser_reads(object? value, string expected)
    {
        Assert.Equal(expected, QueryWriter.Literal(new LogValue(value)));
    }

    [Fact]
    public void Writer_builds_filters_and_exclusions()
    {
        Assert.Equal("Id = 1", QueryWriter.PropertyEquals("Id", new LogValue(1L)));
        Assert.Equal("not (Customer = 'Ann')", QueryWriter.PropertyEquals("Customer", new LogValue("Ann"), exclude: true));
        Assert.Equal("@p['Request Id'] = 'r-1'", QueryWriter.PropertyEquals("Request Id", new LogValue("r-1")));
        Assert.Equal("Id = 1", QueryWriter.And("  ", "Id = 1"));
        Assert.Equal("@l = 'Error' and Id = 1", QueryWriter.And("@l = 'Error' ", "Id = 1"));
        Assert.Equal("(@l = 'Error' or @l = 'Fatal') and Id = 1", QueryWriter.And("@l = 'Error' or @l = 'Fatal'", "Id = 1"));
        Assert.Equal([1], Ids(QueryWriter.And("Elapsed > 100", QueryWriter.PropertyEquals("Customer", new LogValue("Ann"), exclude: true))));
    }
}
