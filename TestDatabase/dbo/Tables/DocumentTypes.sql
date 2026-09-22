CREATE TABLE [dbo].[DocumentTypes]
(
    [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_DocumentTypes] PRIMARY KEY,
    [Name] NVARCHAR(128) NOT NULL CONSTRAINT [UQ_DocumentTypes_Name] UNIQUE
);