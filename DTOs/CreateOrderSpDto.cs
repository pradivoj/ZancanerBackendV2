using System;
using System.ComponentModel.DataAnnotations;

namespace BackendV2.DTOs
{
    public class CreateOrderSpDto
    {
        [Required]
        public int USERID { get; set; }

        // ORDER must be provided and be > 50000 and < 1000000
        [Required(ErrorMessage = "ORDER is required and must be a number between 50001 and 999999.")]
        [Range(50001, 999999, ErrorMessage = "ORDER must be greater than 50000 and less than 1000000.")]
        public int? ORDER { get; set; }
    }
}
