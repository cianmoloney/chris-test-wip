using TestFunction.Data;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class ExpiryTests
{
    [Fact]
    public void ReplacementMustBeReviewedSameTypeAndCoverTheExpiry()
    {
        var end = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var old = new DocumentEntry { Id = 1, StaffId = 7, DocumentTypeId = 2, ExpiryDate = end };
        var replacement = new DocumentEntry { Id = 2, StaffId = 7, DocumentTypeId = 2, ExpiryDate = end.AddYears(1),
            StartDate = end.AddDays(1), IsValid = true, Status = DocumentStatus.Validated, ScanPassed = true };
        Assert.True(BackgroundWorker.IsReplacement(old, replacement));
        replacement.StartDate = end.AddDays(2);
        Assert.False(BackgroundWorker.IsReplacement(old, replacement));
        replacement.StartDate = end;
        replacement.IsValid = false;
        Assert.False(BackgroundWorker.IsReplacement(old, replacement));
        replacement.IsValid = true;
        replacement.DocumentTypeId = 3;
        Assert.False(BackgroundWorker.IsReplacement(old, replacement));
    }
}