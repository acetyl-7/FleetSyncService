using System;

namespace FleetSyncService.Models;

public class IncidentModel
{
    public DateTime DtIncidente { get; set; }
    public DateTime DtUser { get; set; }
    public string MobileDriverId { get; set; } = string.Empty;
    public decimal Lat { get; set; }
    public decimal Lon { get; set; }
    public int Kms { get; set; }
    public string MatTractor { get; set; } = string.Empty;
    public string MatReboque { get; set; } = string.Empty;
    public string ImageIds { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string RazaoCustom { get; set; } = string.Empty;
}
