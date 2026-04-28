namespace FleetSyncService.Models;
using System;

public class DriverSqlModel
{
    public int Id { get; set; }
    public int IdEmpresa { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string Alcunha { get; set; } = string.Empty;
    public string Telemovel { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? FirebaseUid { get; set; }
}