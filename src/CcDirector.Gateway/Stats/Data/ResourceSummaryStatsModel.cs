using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// The model configuration for the resource summary's four tables (Mission: Resource Monitor, section 6):
/// <c>resource_window</c> and <c>resource_session_window</c> (five-minute rows, kept 90 days) and
/// <c>resource_hour</c> and <c>resource_session_hour</c> (hourly roll-ups, kept two years).
///
/// In its own file as one call, like <see cref="ConcurrencyStatsModel"/>, so the statistics context declares the
/// four sets and calls <see cref="Configure"/>. Every name is snake_case and pinned explicitly, never left to a
/// convention. The migration that creates these tables ONLY ADDS TABLES: no existing table is touched.
///
/// All four are keyed by tenant first. There is no foreign key and no navigation, matching every other table in this
/// store.
/// </summary>
public static class ResourceSummaryStatsModel
{
    /// <summary>Store UTC and read back UTC, so a window start read from either provider is a UTC instant.</summary>
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter = new(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    /// <summary>Configure the four resource summary tables on <paramref name="modelBuilder"/>.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ResourceWindowEntity>(b =>
        {
            b.ToTable("resource_window");
            b.HasKey(e => new { e.Tenant, e.DirectorId, e.WindowStartUtc });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.DirectorId).HasColumnName("director_id").IsRequired();
            b.Property(e => e.WindowStartUtc).HasColumnName("window_start_utc").HasConversion(UtcConverter);
            b.Property(e => e.HourStartUtc).HasColumnName("hour_start_utc").HasConversion(UtcConverter);
            b.Property(e => e.MachineName).HasColumnName("machine_name").IsRequired();
            b.Property(e => e.WindowSeconds).HasColumnName("window_seconds");
            b.Property(e => e.SampleCount).HasColumnName("sample_count");
            b.Property(e => e.ReceivedUtc).HasColumnName("received_utc").HasConversion(UtcConverter);
            b.Property(e => e.RefusedReason).HasColumnName("refused_reason");
            // Added 7 November 2026 (Disk Space on the Meter). The default fills every row written before the column
            // existed, and every row from a Director that reports no drives, with an empty list.
            b.Property(e => e.DrivesJson).HasColumnName("drives_json").IsRequired().HasDefaultValue("[]");
            Figures(b);
            // The Cockpit's reads: a tenant's machines over a time range.
            b.HasIndex(e => new { e.Tenant, e.WindowStartUtc }).HasDatabaseName("ix_resource_window_tenant_window");
            // The roll-up's read: one hour of one Director's windows.
            b.HasIndex(e => new { e.Tenant, e.DirectorId, e.HourStartUtc }).HasDatabaseName("ix_resource_window_tenant_director_hour");
        });

        modelBuilder.Entity<ResourceSessionWindowEntity>(b =>
        {
            b.ToTable("resource_session_window");
            b.HasKey(e => new { e.Tenant, e.DirectorId, e.WindowStartUtc, e.SessionId });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.DirectorId).HasColumnName("director_id").IsRequired();
            b.Property(e => e.WindowStartUtc).HasColumnName("window_start_utc").HasConversion(UtcConverter);
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
            b.Property(e => e.HourStartUtc).HasColumnName("hour_start_utc").HasConversion(UtcConverter);
            b.Property(e => e.MachineName).HasColumnName("machine_name").IsRequired();
            b.Property(e => e.Factory).HasColumnName("factory");
            b.Property(e => e.ProcessesWithUnknownFigures).HasColumnName("processes_with_unknown_figures");
            b.Property(e => e.SampleCount).HasColumnName("sample_count");
            b.Property(e => e.ReceivedUtc).HasColumnName("received_utc").HasConversion(UtcConverter);
            Figures(b);
            b.HasIndex(e => new { e.Tenant, e.WindowStartUtc }).HasDatabaseName("ix_resource_session_window_tenant_window");
            b.HasIndex(e => new { e.Tenant, e.DirectorId, e.HourStartUtc }).HasDatabaseName("ix_resource_session_window_tenant_director_hour");
        });

        modelBuilder.Entity<ResourceHourEntity>(b =>
        {
            b.ToTable("resource_hour");
            b.HasKey(e => new { e.Tenant, e.DirectorId, e.HourStartUtc });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.DirectorId).HasColumnName("director_id").IsRequired();
            b.Property(e => e.HourStartUtc).HasColumnName("hour_start_utc").HasConversion(UtcConverter);
            b.Property(e => e.MachineName).HasColumnName("machine_name").IsRequired();
            b.Property(e => e.WindowCount).HasColumnName("window_count");
            b.Property(e => e.RefusedWindows).HasColumnName("refused_windows");
            b.Property(e => e.SampleCount).HasColumnName("sample_count");
            b.Property(e => e.RolledAtUtc).HasColumnName("rolled_at_utc").HasConversion(UtcConverter);
            Figures(b);
            b.HasIndex(e => new { e.Tenant, e.HourStartUtc }).HasDatabaseName("ix_resource_hour_tenant_hour");
        });

        modelBuilder.Entity<ResourceSessionHourEntity>(b =>
        {
            b.ToTable("resource_session_hour");
            b.HasKey(e => new { e.Tenant, e.DirectorId, e.HourStartUtc, e.SessionId });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.DirectorId).HasColumnName("director_id").IsRequired();
            b.Property(e => e.HourStartUtc).HasColumnName("hour_start_utc").HasConversion(UtcConverter);
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
            b.Property(e => e.MachineName).HasColumnName("machine_name").IsRequired();
            b.Property(e => e.Factory).HasColumnName("factory");
            b.Property(e => e.ProcessesWithUnknownFigures).HasColumnName("processes_with_unknown_figures");
            b.Property(e => e.WindowCount).HasColumnName("window_count");
            b.Property(e => e.SampleCount).HasColumnName("sample_count");
            b.Property(e => e.RolledAtUtc).HasColumnName("rolled_at_utc").HasConversion(UtcConverter);
            Figures(b);
            b.HasIndex(e => new { e.Tenant, e.HourStartUtc }).HasDatabaseName("ix_resource_session_hour_tenant_hour");
        });

        // Disk Space on the Meter (7 November 2026): where each drive stands for the owner's disk space notices.
        modelBuilder.Entity<ResourceDiskNoticeEntity>(b =>
        {
            b.ToTable("resource_disk_notice");
            b.HasKey(e => new { e.Tenant, e.MachineName, e.Drive });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.MachineName).HasColumnName("machine_name").IsRequired();
            b.Property(e => e.Drive).HasColumnName("drive").IsRequired();
            b.Property(e => e.IsRed).HasColumnName("is_red");
            b.Property(e => e.LastRedNoticeUtc).HasColumnName("last_red_notice_utc").HasConversion(NullableUtcConverter);
            b.Property(e => e.RedSpellNoticed).HasColumnName("red_spell_noticed");
            b.Property(e => e.LastWindowStartUtc).HasColumnName("last_window_start_utc").HasConversion(UtcConverter);
        });
    }

    /// <summary>The nullable form of <see cref="UtcConverter"/>.</summary>
    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter = new(
        v => v == null ? null : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
        v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

    /// <summary>The figure columns every resource row shares, named once.</summary>
    private static void Figures<T>(EntityTypeBuilder<T> b) where T : class, IResourceFigureColumns
    {
        b.Property<int>(nameof(IResourceFigureColumns.ProcessorSamples)).HasColumnName("processor_samples");
        b.Property<double?>(nameof(IResourceFigureColumns.ProcessorAveragePercent)).HasColumnName("processor_average_percent");
        b.Property<double?>(nameof(IResourceFigureColumns.ProcessorPeakPercent)).HasColumnName("processor_peak_percent");
        b.Property<string?>(nameof(IResourceFigureColumns.ProcessorUnreadableReason)).HasColumnName("processor_unreadable_reason");
        b.Property<int>(nameof(IResourceFigureColumns.MemorySamples)).HasColumnName("memory_samples");
        b.Property<long?>(nameof(IResourceFigureColumns.MemoryAverageUsedBytes)).HasColumnName("memory_average_used_bytes");
        b.Property<long?>(nameof(IResourceFigureColumns.MemoryPeakUsedBytes)).HasColumnName("memory_peak_used_bytes");
        b.Property<long?>(nameof(IResourceFigureColumns.MemoryTotalBytes)).HasColumnName("memory_total_bytes");
        b.Property<string?>(nameof(IResourceFigureColumns.MemoryUnreadableReason)).HasColumnName("memory_unreadable_reason");
        b.Property<string>(nameof(IResourceFigureColumns.CrossingsJson)).HasColumnName("crossings_json").IsRequired();
    }
}
