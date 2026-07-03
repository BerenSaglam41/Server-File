using Microsoft.EntityFrameworkCore;
using FileServiceApi.Models;

namespace FileServiceApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<FileObject> Objects => Set<FileObject>();
    public DbSet<FileReference> References => Set<FileReference>();
    public DbSet<AppPolicy> AppPolicies => Set<AppPolicy>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<RelationTypeConfig> RelationTypeConfigs => Set<RelationTypeConfig>();
    public DbSet<DownloadTicket> DownloadTickets => Set<DownloadTicket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("files");

        modelBuilder.Entity<FileObject>(entity =>
        {
            entity.ToTable("objects");
            entity.HasKey(e => e.FileId);
            entity.Property(e => e.FileId).HasColumnName("file_id");
            entity.Property(e => e.Domain).HasColumnName("domain");
            entity.Property(e => e.RelativePath).HasColumnName("relative_path");
            entity.Property(e => e.ContentType).HasColumnName("content_type");
            entity.Property(e => e.Extension).HasColumnName("extension");
            entity.Property(e => e.OriginalFileName).HasColumnName("original_file_name");
            entity.Property(e => e.SizeBytes).HasColumnName("size_bytes");
            entity.Property(e => e.Sha256).HasColumnName("sha256");
            entity.Property(e => e.Classification).HasColumnName("classification");
            entity.Property(e => e.StorageZone).HasColumnName("storage_zone");
            entity.Property(e => e.RetentionPolicy).HasColumnName("retention_policy");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CreatedByApp).HasColumnName("created_by_app");
            entity.Property(e => e.CreatedByUser).HasColumnName("created_by_user");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<FileReference>(entity =>
        {
            entity.ToTable("references");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.FileId).HasColumnName("file_id");
            entity.Property(e => e.AppCode).HasColumnName("app_code");
            entity.Property(e => e.EntityType).HasColumnName("entity_type");
            entity.Property(e => e.EntityId).HasColumnName("entity_id");
            entity.Property(e => e.RelationType).HasColumnName("relation_type");
            entity.Property(e => e.IsPrimary).HasColumnName("is_primary");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<AppPolicy>(entity =>
        {
            entity.ToTable("app_policies");
            entity.HasKey(e => e.AppCode);
            entity.Property(e => e.AppCode).HasColumnName("app_code");
            entity.Property(e => e.AllowedDomains).HasColumnName("allowed_domains");
            entity.Property(e => e.AllowedFileTypes).HasColumnName("allowed_file_types");
            entity.Property(e => e.CanCreate).HasColumnName("can_create");
            entity.Property(e => e.CanRead).HasColumnName("can_read");
            entity.Property(e => e.CanArchive).HasColumnName("can_archive");
            entity.Property(e => e.MaxFileSizeBytes).HasColumnName("max_file_size_bytes");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.FileId).HasColumnName("file_id");
            entity.Property(e => e.AppCode).HasColumnName("app_code");
            entity.Property(e => e.Actor).HasColumnName("actor");
            entity.Property(e => e.Action).HasColumnName("action");
            entity.Property(e => e.Result).HasColumnName("result");
            entity.Property(e => e.ReasonCode).HasColumnName("reason_code");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.ActorIp).HasColumnName("actor_ip");
            entity.Property(e => e.UserAgent).HasColumnName("user_agent");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<RelationTypeConfig>(entity =>
        {
            entity.ToTable("relation_type_config");
            entity.HasKey(e => e.RelationType);
            entity.Property(e => e.RelationType).HasColumnName("relation_type");
            entity.Property(e => e.Cardinality).HasColumnName("cardinality");
            entity.Property(e => e.Description).HasColumnName("description");
        });

        modelBuilder.Entity<DownloadTicket>(entity =>
        {
            entity.ToTable("download_tickets");
            entity.HasKey(e => e.TicketHash);
            entity.Property(e => e.TicketHash).HasColumnName("ticket_hash");
            entity.Property(e => e.FileId).HasColumnName("file_id");
            entity.Property(e => e.AppCode).HasColumnName("app_code");
            entity.Property(e => e.Actor).HasColumnName("actor");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.UsedAt).HasColumnName("used_at");
            entity.Property(e => e.UseCount).HasColumnName("use_count");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        });
    }
}