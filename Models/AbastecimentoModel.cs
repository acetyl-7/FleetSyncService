using System;

namespace FleetSyncService.Models;

public class AbastecimentoModel
{
    public DateTime DtReal { get; set; }
    public DateTime DtUser { get; set; }
    public string MobileDriverId { get; set; } = string.Empty;
    public decimal Lat { get; set; }
    public decimal Lon { get; set; }
    public int Kms { get; set; }
    public decimal Litros { get; set; }
    public string MatTractor { get; set; } = string.Empty;
    public string MatReboque { get; set; } = string.Empty;
    public string TipoCartao { get; set; } = string.Empty;
    public string Nota { get; set; } = string.Empty;
    public byte[]? Imagem { get; set; } // Tipo IMAGE no SQL mapeado para byte[]
    public string TipoProd { get; set; } = string.Empty;
    public bool Atesto { get; set; }
}