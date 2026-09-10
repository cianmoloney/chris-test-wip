-- Documents backing the Staff Dashboard page. A Staff can hold many
-- documents (e.g. replacement certificates). StaffId is nullable because
-- files arrive via anonymous upload links and may be matched to a Staff later.
-- Status: 0 = PendingReview, 1 = Validated, 2 = Rejected, 3 = ParseFailed.
CREATE TABLE [dbo].[Documents]
(
    [Id]             INT IDENTITY (1, 1) NOT NULL CONSTRAINT [PK_Documents] PRIMARY KEY,
    [BlobName]       NVARCHAR (512)      NULL,
    [DocumentType]   NVARCHAR (128)      NULL,
    [DocumentNumber] NVARCHAR (128)      NULL,
    [ExtractedName]  NVARCHAR (256)      NULL,
    [Email]          NVARCHAR (256)      NULL,
    [Phone]          NVARCHAR (64)       NULL,
    [StartDate]      DATETIMEOFFSET      NULL,
    [ExpiryDate]     DATETIMEOFFSET      NULL,
    [Status]         INT                 NOT NULL CONSTRAINT [DF_Documents_Status] DEFAULT (0),
    [IsValid]        BIT                 NOT NULL CONSTRAINT [DF_Documents_IsValid] DEFAULT (0),
    [Timestamp]      DATETIMEOFFSET      NOT NULL CONSTRAINT [DF_Documents_Timestamp] DEFAULT (SYSDATETIMEOFFSET()),
    [StaffId]       INT                 NULL
        CONSTRAINT [FK_Documents_Staff_StaffId]
        REFERENCES [dbo].[Staff] ([Id]) ON DELETE SET NULL -- keep the document record if a Staff is removed
);
GO

-- Supports the expiry-check query used by the timer-triggered function.
CREATE INDEX [IX_Documents_ExpiryDate] ON [dbo].[Documents] ([ExpiryDate]);
GO

CREATE INDEX [IX_Documents_BlobName] ON [dbo].[Documents] ([BlobName]);
GO

CREATE INDEX [IX_Documents_StaffId] ON [dbo].[Documents] ([StaffId]);
