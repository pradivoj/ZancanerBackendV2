using System;

namespace BackendV2.Models
{
    public class Orden
    {
        // Fields returned by stored procedure CSP_ZANCANER_ORDERS_GET_ALL_RECORDS
        public int ProductionOrder { get; set; }
        public string SLITTER { get; set; } = string.Empty;
        public int CREATORUSER { get; set; }
        public DateTime CreateDateTime { get; set; }
        public int LastModificatorUser { get; set; }
        public DateTime ModificationDatetime { get; set; }
        public string STATUS { get; set; } = string.Empty;
    }
}
