using System;

namespace FleetSyncService.Models;

public class RelViagemModel
{
    public DateTime DtRealInicio { get; set; }
    public DateTime DtUserInicio { get; set; }
    public DateTime DtRealFim { get; set; }
    public DateTime DtUserFim { get; set; }
    public string MobileDriverId { get; set; } = string.Empty;
    public decimal LatInicio { get; set; }
    public decimal LatFim { get; set; }
    public decimal LonInicio { get; set; }
    public decimal LonFim { get; set; }
    public int KmsInicio { get; set; }
    public int KmsFim { get; set; }
    public string MatTractor { get; set; } = string.Empty;
    public string MatReboque { get; set; } = string.Empty;
}
