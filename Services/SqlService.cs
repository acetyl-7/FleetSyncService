using FleetSyncService.Config;
using FleetSyncService.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;

namespace FleetSyncService.Services;

public interface ISqlService
{
    Task ExecuteSqlAsync(string sql);
    Task<IEnumerable<DriverSqlModel>> GetActiveDriversAsync();
    Task UpdateTaskStatusAsync(Guid taskId, string firebaseStatus, DateTime statusDate);
    Task<IEnumerable<TaskSqlModel>> GetActiveTasksAsync();
    Task<int> UpsertMotoristaFromFirebaseAsync(DriverFirebaseModel firebaseDriver);
    Task<bool> CheckIfEmailExistsAsync(string email);
    Task LinkFirebaseUserToSqlAsync(string email, string firebaseUid);
    Task CreateNewMotoristaFromFirebaseAsync(string email, string nickname, string firebaseUid);
    Task<MotoristaValidationResult?> ValidateCompanyPhoneAsync(string phoneNumber);
    Task RegisterDriverAsync(string firebaseUid, string email, string phoneNumber, string nickName, int fleetcomDriverId);
}

public class SqlService : ISqlService
{
    private readonly string _connectionString;

    public SqlService(IOptions<DatabaseConfig> config)
    {
        _connectionString = config.Value.ConnectionString 
            ?? throw new InvalidOperationException("SQL Connection String is not configured in appsettings.json.");
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
                id, fleetcomTaskOrder, fleetcomTaskId, fleetcomDriverId, 
                fleetcomTractorId, fleetcomTrailerId, fleetcomTaskTypeId,
                tractorPlate, trailerPlate, city, address, country, lat, lon, 
                CAST(date AS DATETIME2) AS date, 
                CAST(status AS VARCHAR(10)) AS status, 
                CAST(lastUpdated AS DATETIME2) AS lastUpdated, 
                CAST(CASE WHEN deleted IS NULL THEN 0 ELSE 1 END AS BIT) AS deleted 
            FROM dbo.task 
            WHERE deleted IS NULL AND status != 3";
        return await connection.QueryAsync<TaskSqlModel>(query);
    }

    public async Task UpdateTaskStatusAsync(Guid taskId, string firebaseStatus, DateTime statusDate)
    {
        // Mapeamento de String (Firebase) para Int (SQL Server)
        int sqlStatusId = firebaseStatus.ToLower() switch
        {
            "pending" => 1,
            "in_progress" => 2,
            "completed" => 3,
            _ => 1 // Valor por defeito para evitar crashes
        };

        using var connection = new SqlConnection(_connectionString);
        
        var query = @"UPDATE dbo.task 
                      SET status = @StatusId, 
                          statusDate = @StatusDate, 
                          lastUpdated = @LastUpdated 
                      WHERE id = @Id";
        
        await connection.ExecuteAsync(query, new { 
            Id = taskId, 
            StatusId = sqlStatusId, 
            StatusDate = statusDate, 
            LastUpdated = DateTime.Now 
        });
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

    public async Task LinkFirebaseUserToSqlAsync(string email, string firebaseUid)
    {
        using var connection = new SqlConnection(_connectionString);
        var query = @"
            UPDATE dbo.v_motorista_todos 
            SET FIREBASE_UID = @FirebaseUid
            WHERE EMAIL = @Email";
            
       // await connection.ExecuteAsync(query, new { Email = email, FirebaseUid = firebaseUid });
    }

     public async Task CreateNewMotoristaFromFirebaseAsync(string email, string nickname, string firebaseUid)
    {
        using var connection = new SqlConnection(_connectionString);
        var insertQuery = @"
            INSERT INTO dbo.v_motorista_todos (ID_EMPRESA, NOME, ALCUNHA, EMAIL, FIREBASE_UID, ID_CENTRO_CUSTO) 
            VALUES (1, @Nickname, @Nickname, @Email, @FirebaseUid, 3)";
            
        /*await connection.ExecuteAsync(insertQuery, new { 
            Nickname = nickname, 
            Email = email, 
            FirebaseUid = firebaseUid 
        });*/
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

    /// <summary>
    /// Insere um novo driver na tabela dbo.driver após registo bem-sucedido
    /// </summary>
    public async Task RegisterDriverAsync(string firebaseUid, string email, string phoneNumber, string nickName, int fleetcomDriverId)
    {
        Console.WriteLine($"[DEBUG] A registar driver no SQL: {email} (Firebase UID: {firebaseUid})");
        
        using var connection = new SqlConnection(_connectionString);
        
        // Usamos MERGE baseado no firebaseUid. Se não existir, inserimos com um NEWID() para a coluna id.
        var query = @"
            MERGE INTO dbo.driver AS target
            USING (SELECT @FirebaseUid AS firebaseUid) AS source
            ON target.firebaseUid = source.firebaseUid
            WHEN MATCHED THEN
                UPDATE SET 
                    loginCode = '1234',
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
                VALUES (NEWID(), @FirebaseUid, '1234', 1, @NickName, @Email, NULL, NULL, @PhoneNumber, @FleetcomDriverId, 'motorista');";

        try 
        {
            await connection.ExecuteAsync(query, new {
                FirebaseUid = firebaseUid,
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
            throw; // Re-throw para o controller apanhar
        }
    }
}
