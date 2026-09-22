using System.Security.Cryptography;
using System.Text.Json;

namespace TestShared;

public static class AssignmentRevision
{
    public static string Documents(IEnumerable<int> values) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values.Distinct().Order())));

    public static string Responsibilities(IEnumerable<string> values) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))));
}