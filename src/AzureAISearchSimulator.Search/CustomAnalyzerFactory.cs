using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Miscellaneous;
using Lucene.Net.Analysis.Snowball;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.NGram;
using Lucene.Net.Util;
using AzureAISearchSimulator.Core.Models;
using System.Text.Json;

namespace AzureAISearchSimulator.Search;

/// <summary>
/// Builds Lucene.NET Analyzer instances from Azure AI Search custom analyzer definitions.
/// </summary>
public static class CustomAnalyzerFactory
{
    private static readonly LuceneVersion Version = LuceneDocumentMapper.AppLuceneVersion;

    /// <summary>
    /// Builds a PerFieldAnalyzerWrapper from the index schema.
    /// Each searchable field gets its configured analyzer (custom or built-in).
    /// Fields without an explicit analyzer use StandardAnalyzer.
    /// </summary>
    /// <param name="schema">The index schema.</param>
    /// <param name="forSearch">True for search-time analyzers, false for index-time.</param>
    public static Analyzer BuildPerFieldAnalyzer(SearchIndex schema, bool forSearch)
    {
        var defaultAnalyzer = new StandardAnalyzer(Version);
        var fieldAnalyzers = new Dictionary<string, Analyzer>();

        foreach (var field in schema.Fields)
        {
            if (field.Searchable != true) continue;

            var analyzerName = forSearch
                ? (field.SearchAnalyzer ?? field.Analyzer)
                : (field.IndexAnalyzer ?? field.Analyzer);

            if (string.IsNullOrEmpty(analyzerName)) continue;

            // Check if it references a custom analyzer defined in the index
            var customDef = schema.Analyzers?.FirstOrDefault(a =>
                a.Name.Equals(analyzerName, StringComparison.OrdinalIgnoreCase));

            if (customDef != null)
            {
                fieldAnalyzers[field.Name] = BuildCustomAnalyzer(customDef, schema);
            }
            else
            {
                // Use built-in analyzer factory
                fieldAnalyzers[field.Name] = AnalyzerFactory.Create(analyzerName);
            }
        }

        if (fieldAnalyzers.Count == 0)
        {
            return defaultAnalyzer;
        }

        return new PerFieldAnalyzerWrapper(defaultAnalyzer, fieldAnalyzers);
    }

    /// <summary>
    /// Builds a Lucene Analyzer from a custom analyzer definition.
    /// </summary>
    public static Analyzer BuildCustomAnalyzer(CustomAnalyzer definition, SearchIndex schema)
    {
        return Analyzer.NewAnonymous(createComponents: (fieldName, reader) =>
        {
            var tokenizer = CreateTokenizer(definition.Tokenizer, reader);
            TokenStream stream = tokenizer;

            if (definition.TokenFilters != null)
            {
                foreach (var filterName in definition.TokenFilters)
                {
                    stream = CreateTokenFilter(filterName, stream, schema);
                }
            }

            return new TokenStreamComponents(tokenizer, stream);
        });
    }

    private static Tokenizer CreateTokenizer(string name, TextReader reader)
    {
        return name?.ToLowerInvariant() switch
        {
            "whitespace" => new WhitespaceTokenizer(Version, reader),
            "keyword" or "keyword_v2" => new KeywordTokenizer(reader),
            "letter" => new LetterTokenizer(Version, reader),
            "lowercase" => new LowerCaseTokenizer(Version, reader),
            "classic" => new Lucene.Net.Analysis.Standard.ClassicTokenizer(Version, reader),
            "nGram" or "ngram" => new NGramTokenizer(Version, reader),
            _ => new StandardTokenizer(Version, reader),
        };
    }

    private static TokenStream CreateTokenFilter(string filterName, TokenStream input, SearchIndex schema)
    {
        // Check built-in filters first
        switch (filterName.ToLowerInvariant())
        {
            case "lowercase":
                return new LowerCaseFilter(Version, input);
            case "word_delimiter":
                return new WordDelimiterFilter(Version, input,
                    WordDelimiterFlags.GENERATE_WORD_PARTS | WordDelimiterFlags.GENERATE_NUMBER_PARTS |
                    WordDelimiterFlags.SPLIT_ON_CASE_CHANGE | WordDelimiterFlags.SPLIT_ON_NUMERICS,
                    null);
            case "asciifolding" or "asciifolding_lucene":
                return new ASCIIFoldingFilter(input);
            case "trim":
                return new TrimFilter(Version, input);
            case "length":
                return new LengthFilter(Version, input, 0, 300);
            case "classic":
                return new Lucene.Net.Analysis.Standard.ClassicFilter(input);
            case "ngram" or "ngram_v2":
                return new NGramTokenFilter(Version, input);
            case "edgengram" or "edgengram_v2" or "edge_ngram":
                return new EdgeNGramTokenFilter(Version, input, 1, 2);
            case "stop":
                return new StopFilter(Version, input, StopAnalyzer.ENGLISH_STOP_WORDS_SET);
            case "snowball":
                return new SnowballFilter(input, "English");
            case "porter_stem":
                return new Lucene.Net.Analysis.En.PorterStemFilter(input);
            case "english_possessive":
                return new Lucene.Net.Analysis.En.EnglishPossessiveFilter(Version, input);
            case "reverse":
                return new Lucene.Net.Analysis.Reverse.ReverseStringFilter(Version, input);
        }

        // Look up custom token filter in index definition
        var customFilter = schema.TokenFilters?.FirstOrDefault(f =>
            f.Name.Equals(filterName, StringComparison.OrdinalIgnoreCase));

        if (customFilter != null)
        {
            return CreateCustomTokenFilter(customFilter, input);
        }

        // Unknown filter — pass through unchanged
        return input;
    }

    private static TokenStream CreateCustomTokenFilter(CustomTokenFilter filter, TokenStream input)
    {
        return filter.ODataType switch
        {
            "#Microsoft.Azure.Search.StemmerTokenFilter" => CreateStemmerFilter(filter, input),
            "#Microsoft.Azure.Search.StopwordsTokenFilter" => CreateStopwordsFilter(filter, input),
            "#Microsoft.Azure.Search.LengthTokenFilter" => CreateLengthFilter(filter, input),
            "#Microsoft.Azure.Search.NGramTokenFilterV2" or
            "#Microsoft.Azure.Search.NGramTokenFilter" => CreateNGramFilter(filter, input),
            "#Microsoft.Azure.Search.EdgeNGramTokenFilterV2" or
            "#Microsoft.Azure.Search.EdgeNGramTokenFilter" => CreateEdgeNGramFilter(filter, input),
            "#Microsoft.Azure.Search.TruncateTokenFilter" => CreateTruncateFilter(filter, input),
            _ => input // Unknown type — pass through
        };
    }

    private static TokenStream CreateStemmerFilter(CustomTokenFilter filter, TokenStream input)
    {
        var language = GetStringProperty(filter, "language") ?? "english";
        var snowballName = MapStemmerLanguageToSnowball(language);
        return new SnowballFilter(input, snowballName);
    }

    private static TokenStream CreateStopwordsFilter(CustomTokenFilter filter, TokenStream input)
    {
        // Default to English stop words
        return new StopFilter(Version, input, StopAnalyzer.ENGLISH_STOP_WORDS_SET);
    }

    private static TokenStream CreateLengthFilter(CustomTokenFilter filter, TokenStream input)
    {
        var min = GetIntProperty(filter, "min") ?? 0;
        var max = GetIntProperty(filter, "max") ?? 300;
        return new LengthFilter(Version, input, min, max);
    }

    private static TokenStream CreateNGramFilter(CustomTokenFilter filter, TokenStream input)
    {
        var minGram = GetIntProperty(filter, "minGram") ?? 1;
        var maxGram = GetIntProperty(filter, "maxGram") ?? 2;
        return new NGramTokenFilter(Version, input, minGram, maxGram);
    }

    private static TokenStream CreateEdgeNGramFilter(CustomTokenFilter filter, TokenStream input)
    {
        var minGram = GetIntProperty(filter, "minGram") ?? 1;
        var maxGram = GetIntProperty(filter, "maxGram") ?? 2;
        return new EdgeNGramTokenFilter(Version, input, minGram, maxGram);
    }

    private static TokenStream CreateTruncateFilter(CustomTokenFilter filter, TokenStream input)
    {
        var length = GetIntProperty(filter, "length") ?? 300;
        return new TruncateTokenFilter(input, length);
    }

    /// <summary>
    /// Maps Azure AI Search stemmer language names to Snowball stemmer names.
    /// </summary>
    private static string MapStemmerLanguageToSnowball(string language)
    {
        return language.ToLowerInvariant() switch
        {
            "arabic" => "Arabic",
            "armenian" => "Armenian",
            "basque" => "Basque",
            "catalan" => "Catalan",
            "danish" => "Danish",
            "dutch" => "Dutch",
            "dutchKp" or "dutchkp" => "Kp",
            "english" => "English",
            "finnishLight" or "finnishlight" or "finnish" => "Finnish",
            "french" => "French",
            "frenchLight" or "frenchlight" => "French",
            "frenchMinimal" or "frenchminimal" => "French",
            "galician" => "Galician",
            "german" => "German",
            "german2" => "German2",
            "germanLight" or "germanlight" => "German",
            "germanMinimal" or "germanminimal" => "German",
            "greek" => "Greek",
            "hindi" => "Hindi",
            "hungarian" => "Hungarian",
            "hungarianLight" or "hungarianlight" => "Hungarian",
            "indonesian" => "Indonesian",
            "irish" => "Irish",
            "italian" => "Italian",
            "italianLight" or "italianlight" => "Italian",
            "latvian" => "Latvian",
            "norwegianLight" or "norwegianlight" or "norwegian" or "norwegianMinimal" or "norwegianminimal" => "Norwegian",
            "portuguese" => "Portuguese",
            "portugueseLight" or "portugueselight" => "Portuguese",
            "portugueseMinimal" or "portugueseminimal" => "Portuguese",
            "romanian" => "Romanian",
            "russian" => "Russian",
            "russianLight" or "russianlight" => "Russian",
            "spanish" => "Spanish",
            "spanishLight" or "spanishlight" => "Spanish",
            "swedish" => "Swedish",
            "swedishLight" or "swedishlight" => "Swedish",
            "turkish" => "Turkish",
            _ => "English" // Default fallback
        };
    }

    private static string? GetStringProperty(CustomTokenFilter filter, string key)
    {
        if (filter.Properties != null && filter.Properties.TryGetValue(key, out var element))
        {
            return element.GetString();
        }
        return null;
    }

    private static int? GetIntProperty(CustomTokenFilter filter, string key)
    {
        if (filter.Properties != null && filter.Properties.TryGetValue(key, out var element))
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                return element.GetInt32();
            }
        }
        return null;
    }
}
