using FleetSyncService;
using FleetSyncService.Config;
using FleetSyncService.Services;

var builder = WebApplication.CreateBuilder(args);

// Configures the service to run as a Windows Service
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "FleetSyncService";
});

// Register Configurations
builder.Services.Configure<FirebaseConfig>(builder.Configuration.GetSection("Firebase"));
builder.Services.Configure<DatabaseConfig>(builder.Configuration.GetSection("Database"));

// Register Services
builder.Services.AddSingleton<IFirebaseService, FirebaseService>();
builder.Services.AddSingleton<ISqlService, SqlService>();

// Register the background Worker
builder.Services.AddHostedService<Worker>();

// CORS para a app Flutter poder chamar a API
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();

// ─── Endpoint: Validação de Telemóvel de Empresa ───────────────────────────
// Devolve os dados do motorista (ALCUNHA, ID) se o telemóvel existir na view
app.MapGet("/api/validate-phone", async (string phone, ISqlService sqlService) =>
{
    if (string.IsNullOrWhiteSpace(phone))
    {
        return Results.BadRequest("O parâmetro 'phone' é obrigatório.");
    }

    var motorista = await sqlService.ValidateCompanyPhoneAsync(phone);
    return motorista != null 
        ? Results.Ok(new { 
            valid = true, 
            alcunha = motorista.Alcunha, 
            fleetcomDriverId = motorista.Id,
            telemovel = motorista.Telemovel
          }) 
        : Results.NotFound(new { valid = false, message = "Telemóvel não encontrado na base de dados da empresa." });
});

// ─── Endpoint: Registo de Driver na tabela dbo.driver ──────────────────────
// Chamado pelo Flutter após criar a conta Firebase com sucesso
app.MapPost("/api/drivers/register", async (FleetSyncService.Models.RegisterDriverRequest request, ISqlService sqlService) =>
{
    if (string.IsNullOrWhiteSpace(request.FirebaseUid) || string.IsNullOrWhiteSpace(request.PhoneNumber))
    {
        return Results.BadRequest("FirebaseUid e PhoneNumber são obrigatórios.");
    }

    // Buscar os dados do motorista pela view usando o telemóvel
    var motorista = await sqlService.ValidateCompanyPhoneAsync(request.PhoneNumber);
    if (motorista == null)
    {
        return Results.NotFound(new { message = "Telemóvel não encontrado." });
    }

    try
    {
        await sqlService.RegisterDriverAsync(
            request.FirebaseUid,
            request.Email,
            motorista.Telemovel,
            motorista.Alcunha,
            motorista.Id
        );
        return Results.Ok(new { message = "Driver registado com sucesso na base de dados." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Erro ao registar driver: {ex.Message}");
    }
});

app.Run();
