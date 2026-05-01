using Microsoft.EntityFrameworkCore;

namespace DeployFunc
{
    public class ServiceMapDbContext : DbContext
    {
        public ServiceMapDbContext(DbContextOptions<ServiceMapDbContext> options) : base(options) { }
        public DbSet<ServiceMap> ServiceMaps { get; set; }
        public DbSet<ServiceVersion> ServiceVersions { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<ServiceMap>().ToTable("ServiceMap");
            modelBuilder.Entity<ServiceVersion>().ToTable("ServiceVersion");
        }
    }
}
