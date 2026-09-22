using System.Globalization;
using System.Text.RegularExpressions;

namespace TestShared;

public static class UploadNaming
{
    public static UploadResponse Create(DateTimeOffset now, Guid uploadId, string filename, int? staffId, int? documentTypeId, string containerName = "uploads")
    {
        var basename = Path.GetFileName(filename.Replace('\\', '/'));
        var extension = Path.GetExtension(basename).ToLowerInvariant();
        var stem = Regex.Replace(Path.GetFileNameWithoutExtension(basename), "[^a-zA-Z0-9.-]", "_");
        if (stem.Length > 100) stem = stem[..100];
        var staff = staffId is null ? "" : $"SID{staffId}_";
        var type = documentTypeId is null ? "" : $"DTID_{documentTypeId}_";
        var datePath = now.ToUniversalTime().ToString("yyyy/MM", CultureInfo.InvariantCulture);
        return new(containerName, $"{datePath}/{staff}{type}{uploadId:N}_{stem}{extension}");
    }

    public static (int? StaffId, int? DocumentTypeId) Parse(string name)
    {
        var match = Regex.Match(name, @"^(?:\d{4}/)?\d{2}/(?:SID(?<staff>\d+)_)?(?:DTID_(?<type>\d+)_)?[a-fA-F0-9]{32}_");
        if (!match.Success) return (null, null);
        return (int.TryParse(match.Groups["staff"].Value, out var staff) ? staff : null,
            int.TryParse(match.Groups["type"].Value, out var type) ? type : null);
    }
}