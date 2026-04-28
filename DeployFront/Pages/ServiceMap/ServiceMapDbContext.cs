using Microsoft.EntityFrameworkCore;

namespace DeployFront.Pages.ServiceMap
{
    public class ServiceMapDbContext : DbContext
    {
        public ServiceMapDbContext(DbContextOptions<ServiceMapDbContext> options) : base(options) { }
        public DbSet<ServiceMap> ServiceMaps { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<ServiceMap>().ToTable("ServiceMap");
        }
    }
}
