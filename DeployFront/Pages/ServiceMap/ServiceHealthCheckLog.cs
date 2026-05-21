using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeployFront.Pages.ServiceMap
{
    [Table("ServiceHealthCheckLogs")]
    public class ServiceHealthCheckLog
    {
        [Key]
        [Column("Id")]
        public long id { get; set; }

        [Column("ServiceId")]
        public int serviceId { get; set; }

        [Column("CheckTime")]
        public DateTime checkTime { get; set; }

        [Column("IsHealthy")]
        public bool isHealthy { get; set; }

        [Column("StatusCode")]
        public int? statusCode { get; set; }

        [Column("ResponseTimeMs")]
        public int? responseTimeMs { get; set; }

        [Column("ErrorMessage")]
        [MaxLength(1000)]
        public string? errorMessage { get; set; }

        [ForeignKey(nameof(serviceId))]
        public ServiceMap? ServiceMap { get; set; }
    }
}
