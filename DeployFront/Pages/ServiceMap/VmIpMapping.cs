using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeployFront.Pages.ServiceMap
{
    [Table("VmIpMapping")]
    public class VmIpMapping
    {
        [Key]
        [Column("Id")]
        public int id { get; set; }

        [Required]
        [MaxLength(45)]
        [Column("IpAddress")]
        public string ipAddress { get; set; } = string.Empty;

        [Required]
        [MaxLength(255)]
        [Column("VmName")]
        public string vmName { get; set; } = string.Empty;

        [Column("Active")]
        public bool active { get; set; } = true;
    }
}
