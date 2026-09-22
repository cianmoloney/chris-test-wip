using TestFunction.Data;

namespace TestFunction.Services;

public static class DocumentTypeMatcher
{
    public static (int? TypeId, string? Issue) Match(string text, string? extractedType, int? selectedTypeId,
        IReadOnlyCollection<DocumentType> types)
    {
        var normalizedText = Normalize(text);
        var matches = types.Where(type => !string.IsNullOrWhiteSpace(type.TextIdentifier)
            && normalizedText.Contains(Normalize(type.TextIdentifier), StringComparison.OrdinalIgnoreCase)).ToList();
        var selected = types.FirstOrDefault(type => type.Id == selectedTypeId);
        if (selected is not null)
        {
            if (string.IsNullOrWhiteSpace(selected.TextIdentifier))
                return (selected.Id, "No text identifier is configured for the selected document type; manual review required.");
            if (!matches.Any(type => type.Id == selected.Id))
                return (selected.Id, "The document text does not contain the selected type's text identifier; manual review required.");
            return (selected.Id, matches.Count > 1 ? "The text matches multiple document types; confirm the selected type." : null);
        }
        if (matches.Count == 1) return (matches[0].Id, null);
        if (matches.Count > 1) return (null, "The text matches multiple document types; select the correct type manually.");
        var legacy = types.FirstOrDefault(type => string.IsNullOrWhiteSpace(type.TextIdentifier)
            && string.Equals(type.Name, extractedType?.Trim(), StringComparison.OrdinalIgnoreCase));
        return legacy is not null
            ? (legacy.Id, "The document type was identified by name only; no text identifier is configured.")
            : (null, "The document type could not be identified; select the correct type manually.");
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}