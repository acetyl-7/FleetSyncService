using FleetSyncService.Config;
using FleetSyncService.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;
using System.Data;
using Microsoft.Extensions.Logging;

namespace FleetSyncService.Services;

public interface ISqlService
{
    Task ExecuteSqlAsync(string sql);
    Task<IEnumerable<DriverSqlModel>> GetActiveDriversAsync();
    Task<IEnumerable<TaskSqlModel>> GetActiveTasksAsync();
    Task<int> UpsertMotoristaFromFirebaseAsync(DriverFirebaseModel firebaseDriver);
    Task<bool> CheckIfEmailExistsAsync(string email);
    Task LinkFirebaseUserToSqlAsync(string email, string firebaseUid);
    Task CreateNewMotoristaFromFirebaseAsync(string email, string nickname, string firebaseUid);
    Task<MotoristaValidationResult?> ValidateCompanyPhoneAsync(string phoneNumber);
    Task RegisterDriverAsync(string firebaseUid, string email, string phoneNumber, string nickName, int fleetcomDriverId);
    Task<bool> ExecuteTaskStatusProcedureAsync(TaskModel firebaseTask);
}

public class SqlService : ISqlService
{
    private readonly string _connectionString;
    private readonly ILogger<SqlService> _logger;

    public SqlService(IOptions<DatabaseConfig> config, ILogger<SqlService> logger)
    {
        _connectionString = config.Value.ConnectionString 
            ?? throw new InvalidOperationException("SQL Connection String is not configured in appsettings.json.");
        _logger = logger;
    }

    public async Task ExecuteSqlAsync(string sql)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IEnumerable<DriverSqlModel>> GetActiveDriversAsync()
    {
        using var connection = new SqlConnection(_connectionString);
        return await connection.QueryAsync<DriverSqlModel>("SELECT ID, ID_EMPRESA as IdEmpresa, NOME, ALCUNHA, TELEMOVEL, EMAIL as Email FROM dbo.v_motorista_todos");
    }

    public async Task<IEnumerable<TaskSqlModel>> GetActiveTasksAsync()
    {
        using var connection = new SqlConnection(_connectionString);
        // Vamos buscar as tarefas que não estejam apagadas e que não estejam concluídas
        var query = @"
            SELECT 
                t.id, t.fleetcomTaskOrder, t.fleetcomTaskId, t.fleetcomDriverId, 
                t.fleetcomTractorId, t.fleetcomTrailerId, t.fleetcomTaskTypeId,
                t.tractorPlate, t.trailerPlate, t.city, t.address, t.country, t.lat, t.lon, 
                t.[ref], t.obs,
                CAST(t.date AS DATETIME2) AS date, 
                CAST(t.status AS VARCHAR(10)) AS status, 
                CAST(t.lastUpdated AS DATETIME2) AS lastUpdated, 
                CAST(CASE WHEN t.deleted IS NULL THEN 0 ELSE 1 END AS BIT) AS deleted,
                ISNULL(tt.fleetcomName, '') AS taskTypeName
            FROM dbo.task t
            LEFT JOIN dbo.task_type tt ON t.fleetcomTaskTypeId = tt.fleetcomId
            WHERE t.deleted IS NULL AND t.status < 80";
        return await connection.QueryAsync<TaskSqlModel>(query);
    }

    public async Task<int> UpsertMotoristaFromFirebaseAsync(DriverFirebaseModel firebaseDriver)
    {
        using var connection = new SqlConnection(_connectionString);
        
        bool hasSqlId = int.TryParse(firebaseDriver.SqlId, out int sqlId) && sqlId > 0;
        bool hasEmail = !string.IsNullOrWhiteSpace(firebaseDriver.Email);

        if (hasSqlId)
        {
            var query = @"
                UPDATE dbo.v_motorista_todos 
                SET NOME = @Nome, 
                    ALCUNHA = @Alcunha, 
                    TELEMOVEL = @Telemovel,
                    FIREBASE_UID = @FirebaseUid
                WHERE ID = @Id";
            
            await connection.ExecuteAsync(query, new {
                Id = sqlId,
                Nome = firebaseDriver.Name,
                Alcunha = firebaseDriver.Nickname,
                Telemovel = firebaseDriver.Phone,
                FirebaseUid = firebaseDriver.Uid
            });
            
            return sqlId;
        }
        else if (hasEmail)
        {
            // Primeiro tentamos ver se já existe alguém com este email
            var existingId = await connection.QueryFirstOrDefaultAsync<int?>(
                "SELECT ID FROM dbo.v_motorista_todos WHERE EMAIL = @Email", 
                new { Email = firebaseDriver.Email });

            if (existingId.HasValue)
            {
                var updateByEmailQuery = @"
                    UPDATE dbo.v_motorista_todos 
                    SET FIREBASE_UID = @FirebaseUid
                    WHERE ID = @Id";
                    
                await connection.ExecuteAsync(updateByEmailQuery, new {
                    Id = existingId.Value,
                    FirebaseUid = firebaseDriver.Uid
                });
                
                return existingId.Value;
            }
        }

        var insertQuery = @"
            INSERT INTO dbo.v_motorista_todos (ID_EMPRESA, NOME, ALCUNHA, TELEMOVEL, EMAIL, FIREBASE_UID, ID_CENTRO_CUSTO) 
            VALUES (1, @Nome, @Alcunha, @Telemovel, @Email, @FirebaseUid, 3);
            SELECT CAST(SCOPE_IDENTITY() as int);";
            
        return await connection.ExecuteScalarAsync<int>(insertQuery, new {
            Nome = firebaseDriver.Name,
            Alcunha = firebaseDriver.Nickname,
            Telemovel = firebaseDriver.Phone,
            Email = firebaseDriver.Email,
            FirebaseUid = firebaseDriver.Uid
        });
    }

    public async Task<bool> CheckIfEmailExistsAsync(string email)
    {
        using var connection = new SqlConnection(_connectionString);
        var id = await connection.QueryFirstOrDefaultAsync<int?>("SELECT ID FROM dbo.v_motorista_todos WHERE EMAIL = @Email", new { Email = email });
        return id.HasValue;
    }

    public Task LinkFirebaseUserToSqlAsync(string email, string firebaseUid)
    {
        // using var connection = new SqlConnection(_connectionString);
        // var query = @"
        //     UPDATE dbo.v_motorista_todos 
        //     SET FIREBASE_UID = @FirebaseUid
        //     WHERE EMAIL = @Email";
            
        // await connection.ExecuteAsync(query, new { Email = email, FirebaseUid = firebaseUid });
        return Task.CompletedTask;
    }

    public Task CreateNewMotoristaFromFirebaseAsync(string email, string nickname, string firebaseUid)
    {
        // using var connection = new SqlConnection(_connectionString);
        // var insertQuery = @"
        //     INSERT INTO dbo.v_motorista_todos (ID_EMPRESA, NOME, ALCUNHA, EMAIL, FIREBASE_UID, ID_CENTRO_CUSTO) 
        //     VALUES (1, @Nickname, @Nickname, @Email, @FirebaseUid, 3)";
            
        /*await connection.ExecuteAsync(insertQuery, new { 
            Nickname = nickname, 
            Email = email, 
            FirebaseUid = firebaseUid 
        });*/
        return Task.CompletedTask;
    }

    /// <summary>
    /// Valida se o telemóvel existe na view v_MOTORISTA_TODOS e devolve os dados (ID, ALCUNHA)
    /// </summary>
    public async Task<MotoristaValidationResult?> ValidateCompanyPhoneAsync(string phoneNumber)
    {
        Console.WriteLine($"[DEBUG] Validar telemóvel: '{phoneNumber}'");
        
        // Normalizar: remover espaços, traços e prefixo +351
        var normalized = new string(phoneNumber.Where(char.IsDigit).ToArray());
        if (normalized.StartsWith("351") && normalized.Length > 9)
        {
            normalized = normalized.Substring(3);
        }
        
        Console.WriteLine($"[DEBUG] Telemóvel normalizado: '{normalized}'");

        using var connection = new SqlConnection(_connectionString);
        
        // Vamos buscar todos para ver se há espaços ou caracteres invisíveis na BD
        var result = await connection.QueryFirstOrDefaultAsync<MotoristaValidationResult>(
            "SELECT ID as Id, ALCUNHA as Alcunha, TELEMOVEL_EMPRESA as Telemovel FROM dbo.v_MOTORISTA_TODOS WHERE TELEMOVEL_EMPRESA LIKE '%' + @Phone + '%'",
            new { Phone = normalized });

        if (result != null)
        {
            Console.WriteLine($"[DEBUG] Motorista encontrado: {result.Alcunha} (ID: {result.Id})");
        }
        else
        {
            Console.WriteLine($"[DEBUG] Nenhum motorista encontrado para o número {normalized}");
        }

        return result;
    }

    private int TraduzirEstadoParaSql(string firebaseStatus)
    {
        return firebaseStatus?.ToLower() switch
        {
            "pending" or "por_enviar" => 1,
            "enviada" => 10,
            "recebida" => 20,
            "vista" => 30,
            "in_progress" or "iniciada" => 40,
            "terminada" or "completed" => 80,
            "anulada" => 90,
            _ => 1 // Default para Por Enviar
        };
    }


    /// <summary>
    /// Insere um novo driver na tabela dbo.driver após registo bem-sucedido
    /// </summary>
    public async Task RegisterDriverAsync(string firebaseUid, string email, string phoneNumber, string nickName, int fleetcomDriverId)
    {
        // Gerar loginCode único baseado no fleetcomDriverId
        var loginCode = fleetcomDriverId.ToString().PadLeft(4, '0');
        
        Console.WriteLine($"[DEBUG] A registar driver no SQL: {email} (Firebase UID: {firebaseUid}, LoginCode: {loginCode})");
        
        using var connection = new SqlConnection(_connectionString);
        
        // Usamos MERGE baseado no firebaseUid. Se não existir, inserimos com um NEWID() para a coluna id.
        var query = @"
            MERGE INTO dbo.driver AS target
            USING (SELECT @FirebaseUid AS firebaseUid) AS source
            ON target.firebaseUid = source.firebaseUid
            WHEN MATCHED THEN
                UPDATE SET 
                    loginCode = @LoginCode,
                    active = 1,
                    nickName = @NickName,
                    email = @Email,
                    login = NULL,
                    password = NULL,
                    phoneNumber = @PhoneNumber,
                    fleetcomDriverId = @FleetcomDriverId,
                    role = 'motorista'
            WHEN NOT MATCHED THEN
                INSERT (id, firebaseUid, loginCode, active, nickName, email, login, password, phoneNumber, fleetcomDriverId, role)
                VALUES (NEWID(), @FirebaseUid, @LoginCode, 1, @NickName, @Email, NULL, NULL, @PhoneNumber, @FleetcomDriverId, 'motorista');";

        try 
        {
            await connection.ExecuteAsync(query, new {
                FirebaseUid = firebaseUid,
                LoginCode = loginCode,
                NickName = nickName,
                Email = email,
                PhoneNumber = phoneNumber,
                FleetcomDriverId = fleetcomDriverId
            });
            Console.WriteLine($"[DEBUG] Driver {email} registado/atualizado com sucesso no SQL!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO SQL] Falha ao registar/atualizar driver: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> ExecuteTaskStatusProcedureAsync(TaskModel firebaseTask)
    {
        if (string.IsNullOrEmpty(firebaseTask.SqlId) || !Guid.TryParse(firebaseTask.SqlId, out var taskGuid))
        {
            _logger.LogWarning("Tarefa {Id} tem SqlId inválido: {SqlId}", firebaseTask.Id, firebaseTask.SqlId);
            return false;
        }

        int progressStatus = TraduzirEstadoParaSql(firebaseTask.Status);
        var agora = DateTime.UtcNow;
        var dataUser = firebaseTask.StatusDate ?? agora;

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        try
        {
            await connection.ExecuteAsync(
                "dbo.PROCESSA_TASK_STATUS",
                new {
                    UID_TASK = taskGuid,
                    STATUS = progressStatus,
                    DATA_USER = dataUser,
                    DATA_SISTEMA = agora,
                    LAT = firebaseTask.Lat ?? 0.0,
                    LON = firebaseTask.Lon ?? 0.0,
                    CITY = (string)null,
                    FREE_SPACE = 0,
                    TEMPERATURA = (string)null,
                    DURATION = 0,
                    LOCATION = (string)null
                },
                commandType: CommandType.StoredProcedure
            );
            
            _logger.LogInformation("Procedure dbo.PROCESSA_TASK_STATUS executada com sucesso: {SqlId} → status={ProgressStatus}", firebaseTask.SqlId, progressStatus);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao executar procedure dbo.PROCESSA_TASK_STATUS para a tarefa {SqlId}.", firebaseTask.SqlId);
            return false;
        }
    }
}
