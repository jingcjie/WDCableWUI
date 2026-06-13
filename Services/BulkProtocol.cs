using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services;

public static class BulkProtocol
{
    public const int ChunkSize = 64 * 1024;

    public const string KindFile = "file";
    public const string KindSpeedRequest = "speed-request";
    public const string KindSpeedData = "speed-data";

    public static string BuildMetadata(IDictionary<string, object?> values)
    {
        return JsonSerializer.Serialize(values);
    }

    public static Dictionary<string, JsonElement> ParseMetadata(string metadataJson)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return result;
        }

        using var document = JsonDocument.Parse(metadataJson);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    public static string GetString(IReadOnlyDictionary<string, JsonElement> metadata, string key, string fallback = "")
    {
        return metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    public static long GetInt64(IReadOnlyDictionary<string, JsonElement> metadata, string key, long fallback = 0)
    {
        return metadata.TryGetValue(key, out var value) && value.TryGetInt64(out var number)
            ? number
            : fallback;
    }

    public static string SafeFileName(string fileName)
    {
        var candidate = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "unknown_file" : fileName.Trim());
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = "unknown_file";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(invalid, '_');
        }

        return candidate;
    }

    public static string DuplicateSafePath(string directoryPath, string fileName)
    {
        Directory.CreateDirectory(directoryPath);

        var safeName = SafeFileName(fileName);
        var candidate = Path.Combine(directoryPath, safeName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var index = 1; ; index++)
        {
            candidate = Path.Combine(directoryPath, $"{nameWithoutExtension} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    public static string Sha256Hex(byte[] hash)
    {
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString();
    }

    public static long NextStreamId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToInt64(bytes);
        return value == long.MinValue ? 1 : Math.Abs(value);
    }

    public static Guid GuidFromStringOrNew(string value)
    {
        return Guid.TryParse(value, out var guid) ? guid : Guid.NewGuid();
    }

    public static double CalculateMbps(long bytes, TimeSpan duration)
    {
        var seconds = Math.Max(duration.TotalSeconds, 0.001);
        return bytes * 8.0 / 1024 / 1024 / seconds;
    }
}
