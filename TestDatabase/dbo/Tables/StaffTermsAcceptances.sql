-- Many:many between Staffs and terms versions, with a timestamped
-- history row for every acceptance.
CREATE TABLE [dbo].[StaffTermsAcceptances]
(
    [Id]                     INT IDENTITY (1, 1) NOT NULL CONSTRAINT [PK_StaffTermsAcceptances] PRIMARY KEY,
    [StaffId]               INT                 NOT NULL
        CONSTRAINT [FK_StaffTermsAcceptances_Staffs]
        REFERENCES [dbo].[Staff] ([Id]) ON DELETE CASCADE,
    [TermsDocumentVersionId] INT                 NOT NULL
        CONSTRAINT [FK_StaffTermsAcceptances_TermsDocumentVersions]
        REFERENCES [dbo].[TermsDocumentVersions] ([Id]),
    [AcceptedAt]             DATETIMEOFFSET      NOT NULL CONSTRAINT [DF_StaffTermsAcceptances_AcceptedAt] DEFAULT (SYSDATETIMEOFFSET())
);
GO

CREATE INDEX [IX_StaffTermsAcceptances_Staff_Version]
    ON [dbo].[StaffTermsAcceptances] ([StaffId], [TermsDocumentVersionId]);
