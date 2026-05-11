using Microsoft.EntityFrameworkCore;

namespace DeployFunc
{
    public class ServiceMapDbContext : DbContext
    {
        public ServiceMapDbContext(DbContextOptions<ServiceMapDbContext> options) : base(options) { }
        public DbSet<ServiceMap> ServiceMaps { get; set; }
        public DbSet<ServiceVersion> ServiceVersions { get; set; }
        public DbSet<ServiceOutage> Outages { get; set; }
        public DbSet<ServiceHealthCheckLog> ServiceHealthCheckLogs { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<ServiceMap>().ToTable("ServiceMap");
            modelBuilder.Entity<ServiceVersion>().ToTable("ServiceVersion");
            modelBuilder.Entity<ServiceOutage>().ToTable("ServiceOutages");
            modelBuilder.Entity<ServiceOutage>()
                .HasOne(o => o.ServiceMap)
                .WithMany()
                .HasForeignKey(o => o.serviceId);
            modelBuilder.Entity<ServiceHealthCheckLog>().ToTable("ServiceHealthCheckLogs");
            modelBuilder.Entity<ServiceHealthCheckLog>()
                .HasOne(o => o.ServiceMap)
                .WithMany()
                .HasForeignKey(o => o.serviceId);
        }
    }
}
