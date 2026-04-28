using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeployFront.Pages.ServiceMap
{
    [Table("ServiceMap")]
    public class ServiceMap
    {
        [Key]
        public int id { get; set; }
        [Required]
        [MaxLength(5)]
        public string protocol { get; set; }
        [Required]
        [MaxLength(255)]
        public string hostname { get; set; }
        [Required]
        [MaxLength(45)]
        public string ipaddr { get; set; }
        [Required]
        public int port { get; set; }
        [Required]
        [MaxLength(100)]
        public string appname { get; set; }
        [MaxLength(50)]
        public string? appversion { get; set; }
        [MaxLength(100)]
        public string? customer { get; set; }
        [MaxLength(50)]
        public string? environment { get; set; }
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
