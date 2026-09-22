using System.ComponentModel.DataAnnotations;

namespace TestFunction.Data;

public sealed class EmailDispatch
{
    [MaxLength(64)] public string Id { get; set; } = "";
    public DateTimeOffset LeaseUntil { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = [];
}