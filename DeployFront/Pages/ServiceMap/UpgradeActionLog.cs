using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeployFront.Pages.ServiceMap
{
    [Table("UpgradeActionLogs")]
    public class UpgradeActionLog
    {
        [Key]
        [Column("Id")]
        public long id { get; set; }

        [Column("RequestUrl")]
        [MaxLength(1000)]
        public string? requestUrl { get; set; }

        [Column("RequestType")]
        [MaxLength(20)]
        public string requestType { get; set; } = "POST";

        [Column("RequestBody", TypeName = "nvarchar(max)")]
        public string? requestBody { get; set; }

        [Column("Result", TypeName = "nvarchar(max)")]
        public string? result { get; set; }

        [Column("StatusCode")]
        public int? statusCode { get; set; }

        [Column("CreatedAt")]
        public DateTime createdAt { get; set; } = DateTime.UtcNow;
    }
}
