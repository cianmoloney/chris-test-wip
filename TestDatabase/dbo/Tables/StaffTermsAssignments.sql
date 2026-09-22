CREATE TABLE [dbo].[StaffTermsAssignments]
(
    [StaffId] INT NOT NULL REFERENCES [dbo].[Staff]([Id]) ON DELETE CASCADE,
    [TermsDocumentId] INT NOT NULL REFERENCES [dbo].[TermsDocuments]([Id]),
    [Version] INT NOT NULL,
    [AssignedAt] DATETIMEOFFSET NOT NULL,
    CONSTRAINT [PK_StaffTermsAssignments] PRIMARY KEY ([StaffId], [TermsDocumentId])
);