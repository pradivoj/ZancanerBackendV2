using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BackendV2.DTOs
{

    public class ReelEventDetailDto
    {
        [Required]
        public Guid MessageId { get; set; }

        [Required]
        public int Eje { get; set; }

        [Required]
        public int Pos { get; set; }

        [Required]
        public string ProductCode { get; set; }

        [Required]
        public bool ManualExit { get; set; }

        [Required]
        public int EdgeTrim { get; set; }
    }


    public class CreateReelEventDto
    {
        // Este campo se generará en el servidor, no se envía en el request.
        public Guid MessageId { get; set; }

        [Required]
        public int ProductionOrder { get; set; }

        [Required]
        public int UserID { get; set; }

        [Required]
        public int CantReelsEjeSup { get; set; }

        [Required]
        public int CantReelsEjeInf { get; set; }

        [Required]
        public int ReelLength { get; set; }

        [Required]
        public bool EndLot { get; set; }

        // Campos para borrado lógico
        public bool Deleted { get; set; }
        public DateTime? DeletedTimestamp { get; set; }


        [Required]
        [MinLength(1, ErrorMessage = "At least one reel must be provided.")]
        public List<ReelEventDetailDto> Reels { get; set; }
    }
}