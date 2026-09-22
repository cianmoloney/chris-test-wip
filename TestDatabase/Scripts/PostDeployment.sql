-- Post-deployment seed data. Runs after every publish, so all inserts are
-- guarded to stay idempotent.

-- Staff types used to build the human-readable StaffId prefix.
IF NOT EXISTS (SELECT 1 FROM dbo.StaffTypes)
BEGIN
    SET IDENTITY_INSERT dbo.StaffTypes ON;
    INSERT INTO dbo.StaffTypes (Id, Name, Prefix)
    VALUES (1, N'Permanent', N'P'),
           (2, N'External', N'E');
    SET IDENTITY_INSERT dbo.StaffTypes OFF;
END;

-- Application roles.
IF NOT EXISTS (SELECT 1 FROM dbo.Roles)
BEGIN
    SET IDENTITY_INSERT dbo.Roles ON;
    INSERT INTO dbo.Roles (Id, Name)
    VALUES (1, N'HR'),
           (2, N'Admin'),
           (3, N'Foreman');
    SET IDENTITY_INSERT dbo.Roles OFF;
END;


-- Seed a default terms document with one version per language if none exist yet.
IF NOT EXISTS (SELECT 1 FROM dbo.TermsDocuments)
BEGIN
    DECLARE @TermsId INT;

    INSERT INTO dbo.TermsDocuments (Title)
    VALUES (N'General Staff terms');

    SET @TermsId = SCOPE_IDENTITY();

    INSERT INTO dbo.TermsDocumentVersions (TermsDocumentId, Content, Language, Version, IsActive)
    VALUES
        (@TermsId, N'By agreeing, you confirm that the information you provided is accurate, that any certificates you upload are genuine, and that you will comply with all workplace safety and conduct requirements applicable to your role.', N'en', 1, 1),
        (@TermsId, N'Wyrażając zgodę, potwierdzasz, że podane informacje są dokładne, że wszelkie przesłane certyfikaty są autentyczne oraz że będziesz przestrzegać wszystkich wymogów bezpieczeństwa i zasad postępowania obowiązujących na Twoim stanowisku.', N'pl', 1, 1),
        (@TermsId, N'Погоджуючись, ви підтверджуєте, що надана вами інформація є точною, що всі завантажені сертифікати є справжніми, і що ви дотримуватиметеся всіх вимог безпеки та правил поведінки, які застосовуються до вашої ролі.', N'uk', 1, 1);
END;


INSERT INTO dbo.StaffRoles (Name)
SELECT source.Name FROM (VALUES (N'Driver'), (N'Carpenter'), (N'Electrician')) source(Name)
WHERE NOT EXISTS (SELECT 1 FROM dbo.StaffRoles target WHERE target.Name = source.Name);

INSERT INTO dbo.DocumentTypes (Name)
SELECT source.Name FROM (VALUES (N'Safe Pass'), (N'Forklift')) source(Name)
WHERE NOT EXISTS (SELECT 1 FROM dbo.DocumentTypes target WHERE target.Name = source.Name);

INSERT INTO dbo.StaffRoleDocumentTypes (StaffRoleId, DocumentTypeId)
SELECT role.Id, documentType.Id FROM dbo.StaffRoles role CROSS JOIN dbo.DocumentTypes documentType
WHERE role.Name = N'Driver' AND documentType.Name IN (N'Safe Pass', N'Forklift')
AND NOT EXISTS (SELECT 1 FROM dbo.StaffRoleDocumentTypes target
                WHERE target.StaffRoleId = role.Id AND target.DocumentTypeId = documentType.Id);

UPDATE dbo.Documents SET Name = COALESCE(BlobName, N'Document') WHERE Name = N'';
UPDATE dbo.Documents SET ContainerName = N'$(LegacyUploadsContainer)' WHERE ContainerName IS NULL AND BlobName IS NOT NULL;
INSERT INTO dbo.StaffRoles (Name)
SELECT DISTINCT staff.Role FROM dbo.Staff staff
WHERE staff.Role IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.StaffRoles role WHERE role.Name = staff.Role);
UPDATE staff SET StaffRoleId = role.Id FROM dbo.Staff staff JOIN dbo.StaffRoles role ON role.Name = staff.Role
WHERE staff.StaffRoleId IS NULL;
UPDATE dbo.StaffTypes SET Name = N'Contract' WHERE Name = N'External';

INSERT INTO dbo.Responsibilities (RoleId, Name, IsEnabled)
SELECT role.Id, permission.Name, 1
FROM dbo.Roles role
CROSS JOIN (VALUES (N'Staff.Read'), (N'Staff.Write'), (N'Documents.Write'), (N'Documents.Validate'),
                   (N'Links.Write'), (N'Users.Write'), (N'Terms.Write')) permission(Name)
WHERE (role.Name IN (N'HR', N'Admin') OR (role.Name = N'Foreman' AND permission.Name IN (N'Staff.Read', N'Documents.Validate', N'Links.Write')))
AND NOT EXISTS (SELECT 1 FROM dbo.Responsibilities target WHERE target.RoleId = role.Id AND target.Name = permission.Name);
