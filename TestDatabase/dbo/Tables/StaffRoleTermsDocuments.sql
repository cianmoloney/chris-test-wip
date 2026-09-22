CREATE TABLE [dbo].[StaffRoleTermsDocuments]
(
    [StaffRoleId] INT NOT NULL,
    [TermsDocumentId] INT NOT NULL,
    CONSTRAINT [PK_StaffRoleTermsDocuments] PRIMARY KEY ([StaffRoleId], [TermsDocumentId]),
    CONSTRAINT [FK_StaffRoleTermsDocuments_StaffRoles] FOREIGN KEY ([StaffRoleId])
        REFERENCES [dbo].[StaffRoles] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_StaffRoleTermsDocuments_TermsDocuments] FOREIGN KEY ([TermsDocumentId])
        REFERENCES [dbo].[TermsDocuments] ([Id]) ON DELETE CASCADE
);
GO
CREATE INDEX [IX_StaffRoleTermsDocuments_TermsDocumentId] ON [dbo].[StaffRoleTermsDocuments] ([TermsDocumentId]);