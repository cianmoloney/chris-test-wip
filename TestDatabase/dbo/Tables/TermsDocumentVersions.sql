-- Terms text versioned per language (1 document : many versions).
CREATE TABLE [dbo].[TermsDocumentVersions]
(
    [Id]              INT IDENTITY (1, 1) NOT NULL CONSTRAINT [PK_TermsDocumentVersions] PRIMARY KEY,
    [TermsDocumentId] INT                 NOT NULL
        CONSTRAINT [FK_TermsDocumentVersions_TermsDocuments]
        REFERENCES [dbo].[TermsDocuments] ([Id]) ON DELETE CASCADE,
    [Content]         NVARCHAR (MAX)      NOT NULL,
    [Language]        NVARCHAR (8)        NOT NULL CONSTRAINT [DF_TermsDocumentVersions_Language] DEFAULT (N'en'),
    [Version]         INT                 NOT NULL CONSTRAINT [DF_TermsDocumentVersions_Version] DEFAULT (1),
    [IsActive]        BIT                 NOT NULL CONSTRAINT [DF_TermsDocumentVersions_IsActive] DEFAULT (1),
    [CreatedAt]       DATETIMEOFFSET      NOT NULL CONSTRAINT [DF_TermsDocumentVersions_CreatedAt] DEFAULT (SYSDATETIMEOFFSET())
);
GO

CREATE INDEX [IX_TermsDocumentVersions_Document_Language_IsActive]
    ON [dbo].[TermsDocumentVersions] ([TermsDocumentId], [Language], [IsActive]);
