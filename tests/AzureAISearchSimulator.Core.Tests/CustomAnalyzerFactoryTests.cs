using System.Text.Json;
using AzureAISearchSimulator.Core.Models;
using AzureAISearchSimulator.Search;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Miscellaneous;
using Lucene.Net.Analysis.TokenAttributes;

namespace AzureAISearchSimulator.Core.Tests;

/// <summary>
/// Tests for CustomAnalyzerFactory — building Lucene analyzers from custom definitions.
/// </summary>
public class CustomAnalyzerFactoryTests
{
    #region CustomTokenFilter serialization

    [Fact]
    public void CustomTokenFilter_ShouldPreserveLanguageProperty_OnRoundTrip()
    {
        // Arrange
        var json = """
        {
            "@odata.type": "#Microsoft.Azure.Search.StemmerTokenFilter",
            "name": "englishStemmer",
            "language": "english"
        }
        """;

        // Act
        var filter = JsonSerializer.Deserialize<CustomTokenFilter>(json);

        // Assert
        Assert.NotNull(filter);
        Assert.Equal("#Microsoft.Azure.Search.StemmerTokenFilter", filter.ODataType);
        Assert.Equal("englishStemmer", filter.Name);
        Assert.NotNull(filter.Properties);
        Assert.True(filter.Properties.ContainsKey("language"));
        Assert.Equal("english", filter.Properties["language"].GetString());
    }

    [Fact]
    public void CustomTokenFilter_ShouldPreserveLanguageProperty_AfterSerializeDeserialize()
    {
        // Arrange
        var json = """
        {
            "@odata.type": "#Microsoft.Azure.Search.StemmerTokenFilter",
            "name": "englishStemmer",
            "language": "english"
        }
        """;
        var filter = JsonSerializer.Deserialize<CustomTokenFilter>(json)!;

        // Act — round-trip
        var serialized = JsonSerializer.Serialize(filter);
        var deserialized = JsonSerializer.Deserialize<CustomTokenFilter>(serialized);

        // Assert
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Properties);
        Assert.True(deserialized.Properties.ContainsKey("language"));
        Assert.Equal("english", deserialized.Properties["language"].GetString());
    }

    [Fact]
    public void CustomTokenFilter_WithoutExtraProperties_ShouldDeserialize()
    {
        // Arrange — a filter with only name and type (no extra properties)
        var json = """
        {
            "@odata.type": "#Microsoft.Azure.Search.LowercaseTokenFilter",
            "name": "myLowercase"
        }
        """;

        // Act
        var filter = JsonSerializer.Deserialize<CustomTokenFilter>(json);

        // Assert
        Assert.NotNull(filter);
        Assert.Equal("myLowercase", filter.Name);
        Assert.Null(filter.Properties);
    }

    [Fact]
    public void CustomTokenizer_ShouldPreserveExtraProperties_OnRoundTrip()
    {
        // Arrange
        var json = """
        {
            "@odata.type": "#Microsoft.Azure.Search.PatternTokenizer",
            "name": "myTokenizer",
            "pattern": "\\W+"
        }
        """;

        // Act
        var tokenizer = JsonSerializer.Deserialize<CustomTokenizer>(json);

        // Assert
        Assert.NotNull(tokenizer);
        Assert.Equal("myTokenizer", tokenizer.Name);
        Assert.NotNull(tokenizer.Properties);
        Assert.True(tokenizer.Properties.ContainsKey("pattern"));
    }

    #endregion

    #region BuildCustomAnalyzer

    [Fact]
    public void BuildCustomAnalyzer_WithStemmerFilter_ShouldApplyStemming()
    {
        // Arrange
        var schema = CreateSchemaWithStemmerAnalyzer();
        var analyzerDef = schema.Analyzers![0];

        // Act
        using var analyzer = CustomAnalyzerFactory.BuildCustomAnalyzer(analyzerDef, schema);
        var tokens = Tokenize(analyzer, "commentEnUS", "The running club is fantastic");

        // Assert — "running" should be stemmed to "run"
        Assert.Contains("run", tokens);
        Assert.Contains("fantast", tokens); // "fantastic" stems to "fantast"
        Assert.DoesNotContain("running", tokens);
    }

    [Fact]
    public void BuildCustomAnalyzer_WithLowercaseFilter_ShouldLowercase()
    {
        // Arrange
        var schema = new SearchIndex
        {
            Name = "test",
            Fields = new List<SearchField>(),
            Analyzers = new List<CustomAnalyzer>
            {
                new()
                {
                    Name = "lcAnalyzer",
                    Tokenizer = "whitespace",
                    TokenFilters = new List<string> { "lowercase" }
                }
            }
        };

        // Act
        using var analyzer = CustomAnalyzerFactory.BuildCustomAnalyzer(schema.Analyzers[0], schema);
        var tokens = Tokenize(analyzer, "test", "Hello WORLD");

        // Assert
        Assert.Equal(new[] { "hello", "world" }, tokens);
    }

    [Fact]
    public void BuildCustomAnalyzer_WhitespaceTokenizer_ShouldSplitOnWhitespace()
    {
        // Arrange
        var schema = new SearchIndex
        {
            Name = "test",
            Fields = new List<SearchField>(),
            Analyzers = new List<CustomAnalyzer>
            {
                new()
                {
                    Name = "wsAnalyzer",
                    Tokenizer = "whitespace",
                    TokenFilters = new List<string>()
                }
            }
        };

        // Act
        using var analyzer = CustomAnalyzerFactory.BuildCustomAnalyzer(schema.Analyzers[0], schema);
        var tokens = Tokenize(analyzer, "test", "foo bar  baz");

        // Assert
        Assert.Equal(new[] { "foo", "bar", "baz" }, tokens);
    }

    [Fact]
    public void BuildCustomAnalyzer_WithWordDelimiter_ShouldSplitCompoundWords()
    {
        // Arrange
        var schema = new SearchIndex
        {
            Name = "test",
            Fields = new List<SearchField>(),
            Analyzers = new List<CustomAnalyzer>
            {
                new()
                {
                    Name = "wdAnalyzer",
                    Tokenizer = "whitespace",
                    TokenFilters = new List<string> { "word_delimiter" }
                }
            }
        };

        // Act
        using var analyzer = CustomAnalyzerFactory.BuildCustomAnalyzer(schema.Analyzers[0], schema);
        var tokens = Tokenize(analyzer, "test", "camelCase");

        // Assert — word_delimiter splits on case changes
        Assert.Contains("camel", tokens);
        Assert.Contains("Case", tokens);
    }

    #endregion

    #region BuildPerFieldAnalyzer

    [Fact]
    public void BuildPerFieldAnalyzer_ShouldReturnPerFieldWrapper_WhenCustomAnalyzersExist()
    {
        // Arrange
        var schema = CreateSchemaWithStemmerAnalyzer();

        // Act
        using var analyzer = CustomAnalyzerFactory.BuildPerFieldAnalyzer(schema, forSearch: false);

        // Assert
        Assert.IsType<PerFieldAnalyzerWrapper>(analyzer);
    }

    [Fact]
    public void BuildPerFieldAnalyzer_ShouldApplyCustomAnalyzer_ToConfiguredField()
    {
        // Arrange
        var schema = CreateSchemaWithStemmerAnalyzer();

        // Act
        using var analyzer = CustomAnalyzerFactory.BuildPerFieldAnalyzer(schema, forSearch: false);
        var tokens = Tokenize(analyzer, "commentEnUS", "The running club is fantastic");

        // Assert — stemmer should reduce "running" → "run"
        Assert.Contains("run", tokens);
        Assert.DoesNotContain("running", tokens);
    }

    [Fact]
    public void BuildPerFieldAnalyzer_ShouldUseStandardAnalyzer_ForFieldWithoutCustomAnalyzer()
    {
        // Arrange
        var schema = CreateSchemaWithStemmerAnalyzer();
        // "comment" field has no analyzer set → should use StandardAnalyzer

        // Act
        using var analyzer = CustomAnalyzerFactory.BuildPerFieldAnalyzer(schema, forSearch: false);
        var tokens = Tokenize(analyzer, "comment", "The running club");

        // Assert — StandardAnalyzer does NOT stem, but lowercases
        Assert.Contains("running", tokens);
        Assert.DoesNotContain("run", tokens);
    }

    [Fact]
    public void BuildPerFieldAnalyzer_WithNoCustomAnalyzers_ShouldReturnStandardAnalyzer()
    {
        // Arrange
        var schema = new SearchIndex
        {
            Name = "test",
            Fields = new List<SearchField>
            {
                new() { Name = "id", Type = "Edm.String", Key = true },
                new() { Name = "title", Type = "Edm.String", Searchable = true }
            }
        };

        // Act
        using var analyzer = CustomAnalyzerFactory.BuildPerFieldAnalyzer(schema, forSearch: false);

        // Assert — no custom analyzers, so returns plain StandardAnalyzer
        Assert.IsNotType<PerFieldAnalyzerWrapper>(analyzer);
    }

    [Fact]
    public void BuildPerFieldAnalyzer_ForSearch_ShouldUseSearchAnalyzer()
    {
        // Arrange — field with separate searchAnalyzer
        var schema = new SearchIndex
        {
            Name = "test",
            Fields = new List<SearchField>
            {
                new() { Name = "id", Type = "Edm.String", Key = true },
                new()
                {
                    Name = "content",
                    Type = "Edm.String",
                    Searchable = true,
                    Analyzer = "en.lucene",
                    SearchAnalyzer = "standard.lucene"
                }
            }
        };

        // Act
        using var searchAnalyzer = CustomAnalyzerFactory.BuildPerFieldAnalyzer(schema, forSearch: true);
        using var indexAnalyzer = CustomAnalyzerFactory.BuildPerFieldAnalyzer(schema, forSearch: false);

        // Both should be PerFieldAnalyzerWrapper since fields have explicit analyzers
        var searchTokens = Tokenize(searchAnalyzer, "content", "running");
        var indexTokens = Tokenize(indexAnalyzer, "content", "running");

        // standard.lucene (search) keeps "running"; en.lucene (index) stems to "run"
        Assert.Contains("running", searchTokens);
        Assert.Contains("run", indexTokens);
    }

    #endregion

    #region Stemmer language mapping

    [Fact]
    public void StemmerFilter_English_ShouldStemCorrectly()
    {
        var tokens = TokenizeWithStemmer("english", "running dogs");
        Assert.Contains("run", tokens);
        Assert.Contains("dog", tokens);
    }

    [Fact]
    public void StemmerFilter_French_ShouldStemCorrectly()
    {
        var tokens = TokenizeWithStemmer("french", "maisons");
        Assert.Contains("maison", tokens);
    }

    [Fact]
    public void StemmerFilter_German_ShouldStemCorrectly()
    {
        var tokens = TokenizeWithStemmer("german", "Häuser");
        // German stemmer lowercases and stems
        Assert.Contains("haus", tokens);
    }

    #endregion

    #region Helpers

    private static SearchIndex CreateSchemaWithStemmerAnalyzer()
    {
        var stemmerJson = """{"language": "english"}""";
        var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stemmerJson);

        return new SearchIndex
        {
            Name = "test-index",
            Fields = new List<SearchField>
            {
                new() { Name = "id", Type = "Edm.String", Key = true },
                new() { Name = "comment", Type = "Edm.String", Searchable = true },
                new()
                {
                    Name = "commentEnUS",
                    Type = "Edm.String",
                    Searchable = true,
                    Analyzer = "commentEnUSAnalyzer"
                }
            },
            Analyzers = new List<CustomAnalyzer>
            {
                new()
                {
                    Name = "commentEnUSAnalyzer",
                    Tokenizer = "whitespace",
                    TokenFilters = new List<string> { "lowercase", "word_delimiter", "englishStemmer" }
                }
            },
            TokenFilters = new List<CustomTokenFilter>
            {
                new()
                {
                    ODataType = "#Microsoft.Azure.Search.StemmerTokenFilter",
                    Name = "englishStemmer",
                    Properties = props
                }
            }
        };
    }

    private static List<string> TokenizeWithStemmer(string language, string text)
    {
        var langJson = $$$"""{"language": "{{{language}}}"}""";
        var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(langJson);

        var schema = new SearchIndex
        {
            Name = "test",
            Fields = new List<SearchField>(),
            Analyzers = new List<CustomAnalyzer>
            {
                new()
                {
                    Name = "stemAnalyzer",
                    Tokenizer = "standard",
                    TokenFilters = new List<string> { "lowercase", "myStemmer" }
                }
            },
            TokenFilters = new List<CustomTokenFilter>
            {
                new()
                {
                    ODataType = "#Microsoft.Azure.Search.StemmerTokenFilter",
                    Name = "myStemmer",
                    Properties = props
                }
            }
        };

        using var analyzer = CustomAnalyzerFactory.BuildCustomAnalyzer(schema.Analyzers[0], schema);
        return Tokenize(analyzer, "test", text);
    }

    private static List<string> Tokenize(Analyzer analyzer, string fieldName, string text)
    {
        var tokens = new List<string>();
        using var stream = analyzer.GetTokenStream(fieldName, new StringReader(text));
        var attr = stream.AddAttribute<ICharTermAttribute>();
        stream.Reset();
        while (stream.IncrementToken())
        {
            tokens.Add(attr.ToString());
        }
        stream.End();
        return tokens;
    }

    #endregion
}
