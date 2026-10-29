using Vndb.Library;

/// <summary>One consumer inside the existing Web container; the queue and pacing live in SQLite.</summary>
public sealed class CatalogJobWorker(CatalogMaintenance maintenance, TimeProvider time, ILogger<CatalogJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await maintenance.StepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Background catalog step failed, type={Type}; saved queue retained.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
        }
    }
}
