using System;
using System.Collections.Generic;

namespace FleetSyncService.Models;

public class IncidentFirestoreModel
{
    public string Id { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public string Plate { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> ImageUrls { get; set; } = new();
    public decimal Lat { get; set; }
    public decimal Lon { get; set; }
    public int Kms { get; set; }
    public DateTime Timestamp { get; set; }
    public string Type { get; set; } = string.Empty;
    public string CustomReason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool SqlSynced { get; set; }
}
