using Microsoft.EntityFrameworkCore;

namespace TestFunction.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        public DbSet<DocumentEntry> Documents => Set<DocumentEntry>();

        public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();

        public DbSet<Staff> Staff => Set<Staff>();

        public DbSet<StaffType> StaffTypes => Set<StaffType>();

        public DbSet<StaffRole> StaffRoles => Set<StaffRole>();

        public DbSet<StaffRoleDocumentType> StaffRoleDocumentTypes => Set<StaffRoleDocumentType>();

        public DbSet<TermsDocument> TermsDocuments => Set<TermsDocument>();

        public DbSet<TermsDocumentVersion> TermsDocumentVersions => Set<TermsDocumentVersion>();

        public DbSet<StaffTermsAcceptance> StaffTermsAcceptances => Set<StaffTermsAcceptance>();

        public DbSet<User> Users => Set<User>();
        public DbSet<UserSession> UserSessions => Set<UserSession>();
        public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
        public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
        public DbSet<EmailDispatch> EmailDispatches => Set<EmailDispatch>();
        public DbSet<StaffTermsAssignment> StaffTermsAssignments => Set<StaffTermsAssignment>();

        public DbSet<Role> Roles => Set<Role>();

        public DbSet<Responsibility> Responsibilities => Set<Responsibility>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StaffTermsAssignment>().HasKey(assignment => new { assignment.StaffId, assignment.TermsDocumentId });
            modelBuilder.Entity<StaffTermsAssignment>().HasOne(assignment => assignment.TermsDocument).WithMany().OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<DocumentEntry>(entity =>
            {
                entity.ToTable("Documents");
                entity.HasIndex(e => e.ExpiryDate);
                entity.HasIndex(e => e.Name);
                entity.HasIndex(e => e.BlobName);
                entity.HasIndex(e => new { e.ContainerName, e.BlobName }).IsUnique().HasFilter("[ContainerName] IS NOT NULL AND [BlobName] IS NOT NULL");
                entity.HasQueryFilter(document => !document.IsArchived);

                // A staff member can hold many documents (e.g. replacement certificates).
                // StaffId is nullable because files arrive via anonymous upload
                // links and may be matched to a staff member later.
                entity.HasOne(e => e.Staff)
                    .WithMany(w => w.Documents)
                    .HasForeignKey(e => e.StaffId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasOne(e => e.Type)
                    .WithMany(t => t.Documents)
                    .HasForeignKey(e => e.DocumentTypeId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            // Each staff member gets a sequential number from a dedicated SQL sequence,
            // used as the numeric part of the human-readable StaffId.
            modelBuilder.HasSequence<int>("StaffNumbers", "dbo")
                .StartsAt(1)
                .IncrementsBy(1);

            modelBuilder.Entity<Staff>(entity =>
            {
                entity.ToTable("Staff");
                entity.HasQueryFilter(staff => !staff.IsArchived);
                entity.HasIndex(e => e.Email).IsUnique();
                entity.HasIndex(e => e.StaffId).IsUnique();
                entity.HasIndex(e => e.StaffNumber).IsUnique();

                entity.Property(e => e.StaffNumber)
                    .HasDefaultValueSql("NEXT VALUE FOR dbo.StaffNumbers");

                entity.HasOne(e => e.StaffType)
                    .WithMany(t => t.StaffMembers)
                    .HasForeignKey(e => e.StaffTypeId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasOne(e => e.StaffRole)
                    .WithMany(r => r.StaffMembers)
                    .HasForeignKey(e => e.StaffRoleId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<StaffType>(entity =>
            {
                entity.ToTable("StaffTypes");
                entity.HasIndex(e => e.Name).IsUnique();
                entity.HasIndex(e => e.Prefix).IsUnique();

                entity.HasData(
                    new StaffType { Id = 1, Name = "Permanent", Prefix = "P" },
                    new StaffType { Id = 2, Name = "Contract", Prefix = "E" });
            });

            modelBuilder.Entity<StaffRole>(entity =>
            {
                entity.ToTable("StaffRoles");
                entity.HasIndex(e => e.Name).IsUnique();

                entity.HasData(
                    new StaffRole { Id = 1, Name = "Driver" },
                    new StaffRole { Id = 2, Name = "Carpenter" },
                    new StaffRole { Id = 3, Name = "Electrician" });
            });

            modelBuilder.Entity<DocumentType>(entity =>
            {
                entity.ToTable("DocumentTypes");
                entity.HasIndex(e => e.Name).IsUnique();

                entity.HasData(
                    new DocumentType { Id = 1, Name = "Safe Pass" },
                    new DocumentType { Id = 2, Name = "Forklift" });
            });

            // Many:many between staff roles and the document types they require.
            // A staff member is expected to upload ALL documents for their role.
            modelBuilder.Entity<StaffRoleDocumentType>(entity =>
            {
                entity.ToTable("StaffRoleDocumentTypes");
                entity.HasKey(e => new { e.StaffRoleId, e.DocumentTypeId });

                entity.HasOne(e => e.StaffRole)
                    .WithMany(r => r.RequiredDocumentTypes)
                    .HasForeignKey(e => e.StaffRoleId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.DocumentType)
                    .WithMany(t => t.RequiredByRoles)
                    .HasForeignKey(e => e.DocumentTypeId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<TermsDocument>(entity =>
            {
                entity.ToTable("TermsDocuments");
            });

            // Terms text is versioned per language (1 document : many versions).
            modelBuilder.Entity<TermsDocumentVersion>(entity =>
            {
                entity.ToTable("TermsDocumentVersions");
                entity.HasIndex(e => new { e.TermsDocumentId, e.Language, e.IsActive });

                entity.HasOne(e => e.TermsDocument)
                    .WithMany(t => t.Versions)
                    .HasForeignKey(e => e.TermsDocumentId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // Many:many between staff and terms versions, with a timestamped
            // history row for every acceptance.
            modelBuilder.Entity<StaffTermsAcceptance>(entity =>
            {
                entity.ToTable("StaffTermsAcceptances");
                entity.HasIndex(e => new { e.StaffId, e.TermsDocumentVersionId });

                entity.HasOne(e => e.Staff)
                    .WithMany(w => w.TermsAcceptances)
                    .HasForeignKey(e => e.StaffId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.TermsDocumentVersion)
                    .WithMany(t => t.Acceptances)
                    .HasForeignKey(e => e.TermsDocumentVersionId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<Role>(entity =>
            {
                entity.ToTable("Roles");
                entity.HasIndex(e => e.Name).IsUnique();

                entity.HasData(
                    new Role { Id = 1, Name = "HR" },
                    new Role { Id = 2, Name = "Admin" },
                    new Role { Id = 3, Name = "Foreman" });
            });

            // Responsibilities toggle feature visibility per role
            // (e.g. "Generate links").
            modelBuilder.Entity<Responsibility>(entity =>
            {
                entity.ToTable("Responsibilities");
                entity.HasIndex(e => new { e.RoleId, e.Name }).IsUnique();

                entity.HasOne(e => e.Role)
                    .WithMany(r => r.Responsibilities)
                    .HasForeignKey(e => e.RoleId)
                    .OnDelete(DeleteBehavior.Cascade);
                var responsibilityId = 1;
                foreach (var roleId in new[] { 1, 2, 3 })
                    foreach (var permission in TestShared.Permissions.All)
                        if (roleId != 3 || permission is TestShared.Permissions.StaffRead or TestShared.Permissions.DocumentsValidate or TestShared.Permissions.LinksWrite)
                            entity.HasData(new Responsibility { Id = responsibilityId++, RoleId = roleId, Name = permission, IsEnabled = true });
            });

            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("Users");
                entity.HasIndex(e => e.Email).IsUnique();

                entity.HasOne(e => e.Role)
                    .WithMany(r => r.Users)
                    .HasForeignKey(e => e.RoleId)
                    .OnDelete(DeleteBehavior.Restrict);
            });
        }
    }
}
