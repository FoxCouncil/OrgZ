// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgZ.Models;

/// <summary>
/// One audiobook in the store lists - a row from archive.org's advancedsearch scoped to the
/// LibriVox collection. The identifier is the archive.org item id; everything downloadable hangs
/// off it (metadata, files, cover image).
/// </summary>
public class AudiobookListing
{
    public string Identifier { get; set; } = "";

    [JsonConverter(typeof(StringOrFirstElementConverter))]
    public string? Title { get; set; }

    /// <summary>The author. archive.org serializes creator as a string OR an array depending on the item.</summary>
    [JsonConverter(typeof(StringOrFirstElementConverter))]
    public string? Creator { get; set; }

    /// <summary>Total runtime as the item reports it - "10:56:13" style.</summary>
    public string? Runtime { get; set; }

    /// <summary>All-time download count - the collection's popularity signal.</summary>
    public long Downloads { get; set; }

    public string? PublicDate { get; set; }

    /// <summary>archive.org's item image service - every item has one, no metadata call needed.</summary>
    public string CoverUrl => $"https://archive.org/services/img/{Identifier}";
}

public class ArchiveSearchResponse
{
    public ArchiveSearchBody? Response { get; set; }
}

public class ArchiveSearchBody
{
    public long NumFound { get; set; }
    public List<AudiobookListing> Docs { get; set; } = [];
}

/// <summary>An item's file entry from the archive.org metadata API.</summary>
public class ArchiveItemFile
{
    public string? Name { get; set; }
    public string? Format { get; set; }

    /// <summary>Duration - "55:12" / "1:02:03" on originals, decimal seconds ("3068.71") on derivatives.</summary>
    public string? Length { get; set; }

    public string? Size { get; set; }
}

public class ArchiveItemMetadataFields
{
    [JsonConverter(typeof(StringOrFirstElementConverter))]
    public string? Title { get; set; }

    [JsonConverter(typeof(StringOrFirstElementConverter))]
    public string? Creator { get; set; }

    [JsonConverter(typeof(StringOrFirstElementConverter))]
    public string? Description { get; set; }

    /// <summary>The source text's publication year where the item carries one ("1902").</summary>
    [JsonConverter(typeof(StringOrFirstElementConverter))]
    public string? Year { get; set; }
}

public class ArchiveItemResponse
{
    public List<ArchiveItemFile> Files { get; set; } = [];
    public ArchiveItemMetadataFields? Metadata { get; set; }
}

/// <summary>
/// archive.org metadata serializes several fields as a bare string on most items and as an array
/// of strings on multi-valued ones (multiple creators, split descriptions). Reads either shape and
/// keeps the first value.
/// </summary>
public sealed class StringOrFirstElementConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            string? first = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (first == null && reader.TokenType == JsonTokenType.String)
                {
                    first = reader.GetString();
                }
            }
            return first;
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
