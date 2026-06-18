using Microsoft.EntityFrameworkCore;

namespace DeployFront.Pages.ServiceMap
{
    public class ServiceMapDbContext : DbContext
    {
        public ServiceMapDbContext(DbContextOptions<ServiceMapDbContext> options) : base(options) { }
        public DbSet<ServiceMap> ServiceMaps { get; set; }
        public DbSet<VmIpMapping> VmIpMappings { get; set; }
        public DbSet<ServiceVersion> ServiceVersions { get; set; }
        public DbSet<ServiceOutage> Outages { get; set; }
        public DbSet<ServiceHealthCheckLog> ServiceHealthCheckLogs { get; set; }
        public DbSet<UpgradeActionLog> UpgradeActionLogs { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<ServiceMap>().ToTable("ServiceMap");
            modelBuilder.Entity<VmIpMapping>().ToTable("VmIpMapping");
            modelBuilder.Entity<ServiceVersion>().ToTable("ServiceVersion");
            modelBuilder.Entity<ServiceOutage>().ToTable("ServiceOutages");
            modelBuilder.Entity<UpgradeActionLog>().ToTable("UpgradeActionLogs");
            modelBuilder.Entity<ServiceOutage>()
                .HasOne(o => o.ServiceMap)
                .WithMany()
                .HasForeignKey(o => o.serviceId);
        }
    }
}
