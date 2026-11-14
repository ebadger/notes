using Microsoft.EntityFrameworkCore;
using Notes.Api.Contracts;

namespace Notes.Api.Data;

public sealed class NotesDbContext(DbContextOptions<NotesDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<NoteGrant> NoteGrants => Set<NoteGrant>();
    public DbSet<SeedState> SeedStates => Set<SeedState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users", table =>
                table.HasCheckConstraint("ck_users_api_key_hash", "api_key_hash ~ '^[0-9A-F]{64}$'"));
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(user => user.DisplayName).HasColumnName("display_name").HasMaxLength(200);
            entity.Property(user => user.ApiKeyHash).HasColumnName("api_key_hash").HasMaxLength(64);
            entity.HasIndex(user => user.ApiKeyHash).IsUnique().HasDatabaseName("ux_users_api_key_hash");
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.ToTable("teams");
            entity.HasKey(team => team.Id);
            entity.Property(team => team.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(team => team.Name).HasColumnName("name").HasMaxLength(200);
        });

        modelBuilder.Entity<TeamMembership>(entity =>
        {
            entity.ToTable("team_memberships");
            entity.HasKey(membership => new { membership.UserId, membership.TeamId });
            entity.Property(membership => membership.UserId).HasColumnName("user_id");
            entity.Property(membership => membership.TeamId).HasColumnName("team_id");
            entity.HasOne(membership => membership.User).WithMany(user => user.Memberships)
                .HasForeignKey(membership => membership.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(membership => membership.Team).WithMany(team => team.Memberships)
                .HasForeignKey(membership => membership.TeamId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(membership => new { membership.TeamId, membership.UserId })
                .HasDatabaseName("ix_team_memberships_team_user");
        });

        modelBuilder.Entity<Note>(entity =>
        {
            entity.ToTable("notes", table =>
            {
                table.HasCheckConstraint("ck_notes_version", "version > 0");
                table.HasCheckConstraint("ck_notes_title", "char_length(title) BETWEEN 1 AND 200");
                table.HasCheckConstraint("ck_notes_body_size", "octet_length(body) <= 65536");
            });
            entity.HasKey(note => note.Id);
            entity.Property(note => note.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(note => note.OwnerId).HasColumnName("owner_id");
            entity.Property(note => note.Title).HasColumnName("title").HasMaxLength(200);
            entity.Property(note => note.Body).HasColumnName("body");
            entity.Property(note => note.CreatedAt).HasColumnName("created_at");
            entity.Property(note => note.UpdatedAt).HasColumnName("updated_at");
            entity.Property(note => note.Version).HasColumnName("version").HasDefaultValue(1L)
                .IsConcurrencyToken();
            entity.HasOne(note => note.Owner).WithMany().HasForeignKey(note => note.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(note => note.OwnerId).HasDatabaseName("ix_notes_owner");
            entity.HasIndex(note => new { note.CreatedAt, note.Id }).IsDescending()
                .HasDatabaseName("ix_notes_listing");
        });

        modelBuilder.Entity<NoteGrant>(entity =>
        {
            entity.ToTable("note_grants", table =>
            {
                table.HasCheckConstraint("ck_note_grants_audience",
                    "(audience = 'team' AND team_id IS NOT NULL) OR " +
                    "(audience = 'organization' AND team_id IS NULL)");
                table.HasCheckConstraint("ck_note_grants_permission", "permission IN ('read', 'edit')");
            });
            entity.HasKey(grant => grant.Id);
            entity.Property(grant => grant.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(grant => grant.NoteId).HasColumnName("note_id");
            entity.Property(grant => grant.Audience).HasColumnName("audience").HasMaxLength(20)
                .HasConversion(
                    audience => audience.ToString().ToLowerInvariant(),
                    audience => Enum.Parse<GrantAudience>(audience, true));
            entity.Property(grant => grant.TeamId).HasColumnName("team_id");
            entity.Property(grant => grant.Permission).HasColumnName("permission").HasMaxLength(10)
                .HasConversion(
                    permission => permission.ToString().ToLowerInvariant(),
                    permission => Enum.Parse<GrantPermission>(permission, true));
            entity.HasOne(grant => grant.Note).WithMany(note => note.Grants)
                .HasForeignKey(grant => grant.NoteId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(grant => grant.Team).WithMany().HasForeignKey(grant => grant.TeamId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(grant => new { grant.NoteId, grant.TeamId }).IsUnique()
                .HasFilter("audience = 'team'").HasDatabaseName("ux_note_grants_team");
            entity.HasIndex(grant => grant.NoteId).IsUnique()
                .HasFilter("audience = 'organization'").HasDatabaseName("ux_note_grants_organization");
            entity.HasIndex(grant => new { grant.NoteId, grant.Permission, grant.TeamId })
                .HasDatabaseName("ix_note_grants_access");
            entity.HasIndex(grant => grant.TeamId).HasDatabaseName("ix_note_grants_team");
        });

        modelBuilder.Entity<SeedState>(entity =>
        {
            entity.ToTable("seed_state", table => table.HasCheckConstraint("ck_seed_state_id", "id = 1"));
            entity.HasKey(state => state.Id);
            entity.Property(state => state.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(state => state.InitializedAt).HasColumnName("initialized_at");
        });
    }
}
