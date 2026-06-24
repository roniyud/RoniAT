using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Data;

public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<TradingSignalRecord> Signals => Set<TradingSignalRecord>();
    public DbSet<OrderRecord> Orders => Set<OrderRecord>();
    public DbSet<ExecutionRecord> Executions => Set<ExecutionRecord>();
    public DbSet<PositionRecord> Positions => Set<PositionRecord>();
    public DbSet<ClosedPositionRecord> ClosedPositions => Set<ClosedPositionRecord>();
    public DbSet<BrokerEventRecord> BrokerEvents => Set<BrokerEventRecord>();
    public DbSet<AuditLogRecord> AuditLogs => Set<AuditLogRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TradingSignalRecord>(entity =>
        {
            entity.ToTable("signals");
            entity.HasKey(signal => signal.Id);
            entity.Property(signal => signal.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(signal => signal.Direction).HasMaxLength(8).IsRequired();
            entity.Property(signal => signal.Type).HasMaxLength(16).IsRequired();
            entity.Property(signal => signal.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(signal => signal.CreatedAt);
        });

        modelBuilder.Entity<OrderRecord>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(order => order.Direction).HasMaxLength(8).IsRequired();
            entity.Property(order => order.OrderType).HasMaxLength(32).IsRequired();
            entity.Property(order => order.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(order => order.SignalId);
        });

        modelBuilder.Entity<ExecutionRecord>(entity =>
        {
            entity.ToTable("executions");
            entity.HasKey(execution => execution.Id);
            entity.Property(execution => execution.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(execution => execution.Direction).HasMaxLength(8).IsRequired();
            entity.HasIndex(execution => execution.OrderId);
        });

        modelBuilder.Entity<PositionRecord>(entity =>
        {
            entity.ToTable("positions");
            entity.HasKey(position => position.Id);
            entity.Property(position => position.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(position => position.Direction).HasMaxLength(8).IsRequired();
            entity.HasIndex(position => position.Symbol).IsUnique();
        });

        modelBuilder.Entity<ClosedPositionRecord>(entity =>
        {
            entity.ToTable("closed_positions");
            entity.HasKey(position => position.Id);
            entity.Property(position => position.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(position => position.Direction).HasMaxLength(8).IsRequired();
            entity.Property(position => position.CloseReason).HasMaxLength(64).IsRequired();
            entity.HasIndex(position => position.ClosedAt);
            entity.HasIndex(position => position.Symbol);
        });

        modelBuilder.Entity<BrokerEventRecord>(entity =>
        {
            entity.ToTable("broker_events");
            entity.HasKey(brokerEvent => brokerEvent.Id);
            entity.Property(brokerEvent => brokerEvent.EventType).HasMaxLength(64).IsRequired();
            entity.HasIndex(brokerEvent => brokerEvent.CreatedAt);
        });

        modelBuilder.Entity<AuditLogRecord>(entity =>
        {
            entity.ToTable("audit_logs");
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.Action).HasMaxLength(64).IsRequired();
            entity.HasIndex(audit => audit.CreatedAt);
        });
    }
}
