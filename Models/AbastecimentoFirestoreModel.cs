using System;

namespace FleetSyncService.Models;

public class AbastecimentoFirestoreModel
{
    public string Id { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public string Plate { get; set; } = string.Empty;
    public string TrailerPlate { get; set; } = string.Empty;
    public decimal Liters { get; set; }
    public string FuelType { get; set; } = string.Empty;
    public bool FullTank { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string ReceiptUrl { get; set; } = string.Empty;
    public decimal Lat { get; set; }
    public decimal Lon { get; set; }
    public int Kms { get; set; }
    public DateTime Timestamp { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool SqlSynced { get; set; }
}
