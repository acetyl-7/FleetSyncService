using System.Linq;
using FleetSyncService.Services;

namespace FleetSyncService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IFirebaseService _firebaseService;
    private readonly ISqlService _sqlService;

    public Worker(ILogger<Worker> logger, IFirebaseService firebaseService, ISqlService sqlService)
    {
        _logger = logger;
        _firebaseService = firebaseService;
        _sqlService = sqlService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        /*
        _logger.LogInformation("A iniciar Listener de Tarefas do Firebase...");
        _firebaseService.StartTasksListener(async (taskId, status, statusDate) =>
        {
            await _sqlService.UpdateTaskStatusAsync(taskId, status, statusDate);
            _logger.LogInformation("SQL atualizado com sucesso para a tarefa {TaskId}", taskId);
        }, _logger);
        */

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Sync Service a correr");

                // 0. Sincronização: Firebase (Firestore) -> SQL (dbo.driver)
                _logger.LogInformation("A verificar novos utilizadores no Firebase...");
                var fbUsers = await _firebaseService.GetAllFirebaseUsersAsync();
                int syncedCount = 0;

                foreach (var fbUser in fbUsers)
                {
                    if (!string.IsNullOrEmpty(fbUser.Phone))
                    {
                        var motorista = await _sqlService.ValidateCompanyPhoneAsync(fbUser.Phone);
                        if (motorista != null)
                        {
                            await _sqlService.RegisterDriverAsync(
                                fbUser.Uid, 
                                fbUser.Email, 
                                motorista.Telemovel, 
                                motorista.Alcunha, 
                                motorista.Id
                            );
                            // Garante que o documento do utilizador no Firebase tem o driverId para a App Móvel conseguir ler as tarefas
                            await _firebaseService.UpdateDriverIdAsync(fbUser.Uid, motorista.Id.ToString());
                            
                            syncedCount++;
                        }
                    }
                }
                _logger.LogInformation("Sincronização concluída: {Count} motoristas processados para SQL.", syncedCount);

                // Sincronização de Tarefas (SQL -> Firebase)
                var activeTasks = await _sqlService.GetActiveTasksAsync();
                foreach (var task in activeTasks)
                {
                    await _firebaseService.UpsertTaskAsync(task);
                }
                _logger.LogInformation("Sincronizadas {Count} tarefas reais às {Time}", activeTasks.Count(), DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante o ciclo de sincronização.");
            }

            // Sync every minute as requested
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
