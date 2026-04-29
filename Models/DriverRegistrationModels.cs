namespace FleetSyncService.Models;

/// <summary>
/// Dados do motorista retornados pela validação do telemóvel (vem da view v_MOTORISTA_TODOS)
/// </summary>
public class MotoristaValidationResult
{
    public int Id { get; set; }
    public string Alcunha { get; set; } = string.Empty;
    public string Telemovel { get; set; } = string.Empty;
}

/// <summary>
/// Pedido de registo de um novo driver na tabela dbo.driver
/// </summary>
public class RegisterDriverRequest
{
    public string FirebaseUid { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
}
