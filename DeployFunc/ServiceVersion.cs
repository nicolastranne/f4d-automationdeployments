using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeployFunc
{
    [Table("ServiceVersion")]
    public class ServiceVersion
    {
        [Key]
        [Column("Id")]
        public int id { get; set; }

        [MaxLength(50)]
        public string? appversion { get; set; }

        [MaxLength(50)]
        public string? servicetype { get; set; }

        [Required]
        public bool active { get; set; }

        [Required]
        public DateTime modified { get; set; }

        [MaxLength(1000)]
        public string? notes { get; set; }
    }
}
