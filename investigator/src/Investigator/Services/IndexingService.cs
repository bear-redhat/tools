using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class IndexingService : BackgroundService
{
    private readonly CasebookIndexer _indexer;
    private readonly IndexingOptions _options;
    private readonly ILogger<IndexingService> _logger;

    public IndexingService(
        CasebookIndexer indexer,
        IOptions<CasebookOptions> casebookOptions,
        ILogger<IndexingService> logger)
    {
        _indexer = indexer;
        _options = casebookOptions.Value.Indexing;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Casebook indexing service is disabled");
            return;
        }

        _logger.LogInformation("Casebook indexing service started, interval={Interval}", _options.Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _logger.LogInformation("Casebook indexing cycle starting");
            try
            {
                var result = await _indexer.RunAsync(stoppingToken);
                _logger.LogInformation("Casebook indexing cycle completed: {Result}", result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Casebook indexing cycle failed");
            }
        }

        _logger.LogInformation("Casebook indexing service stopped");
    }
}
