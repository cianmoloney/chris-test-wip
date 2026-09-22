-- Core terms concept; the actual text lives in TermsDocumentVersions (1:many).
CREATE TABLE [dbo].[TermsDocuments]
(
    [Id]        INT IDENTITY (1, 1) NOT NULL CONSTRAINT [PK_TermsDocuments] PRIMARY KEY,
    [Title]     NVARCHAR (256)      NOT NULL,
    [CreatedAt] DATETIMEOFFSET      NOT NULL CONSTRAINT [DF_TermsDocuments_CreatedAt] DEFAULT (SYSDATETIMEOFFSET())
);
