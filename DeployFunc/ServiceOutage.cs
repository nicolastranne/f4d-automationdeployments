using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeployFunc
{
    [Table("ServiceOutages")]
    public class ServiceOutage
    {
        [Key]
        [Column("Id")]
        public long id { get; set; }

        [Column("ServiceId")]
        public int serviceId { get; set; }

        [Column("StartTime")]
        public DateTime startTime { get; set; }

        [Column("EndTime")]
        public DateTime? endTime { get; set; }

        [Column("DurationSeconds")]
        public int? durationSeconds { get; set; }

        [Column("FailureCount")]
        public int failureCount { get; set; } = 1;

        [Column("IsOngoing")]
        public bool isOngoing { get; set; } = true;

        [Column("LastUpdated")]
        public DateTime lastUpdated { get; set; }

        [ForeignKey(nameof(serviceId))]
        public ServiceMap? ServiceMap { get; set; }
    }
}
