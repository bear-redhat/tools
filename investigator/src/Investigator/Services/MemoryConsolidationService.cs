using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class MemoryConsolidationService : BackgroundService
{
    private readonly MemoryConsolidator _consolidator;
    private readonly DreamingOptions _options;
    private readonly ILogger<MemoryConsolidationService> _logger;

    public MemoryConsolidationService(
        MemoryConsolidator consolidator,
        IOptions<MemoryOptions> memoryOptions,
        ILogger<MemoryConsolidationService> logger)
    {
        _consolidator = consolidator;
        _options = memoryOptions.Value.Dreaming;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Memory dreaming service is disabled");
            return;
        }

        _logger.LogInformation("Memory dreaming service started, interval={Interval}", _options.Interval);

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

            _logger.LogInformation("Memory dreaming cycle starting");
            try
            {
                var result = await _consolidator.RunAsync(stoppingToken);
                _logger.LogInformation("Memory dreaming cycle completed: {Result}", result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory dreaming cycle failed");
            }
        }

        _logger.LogInformation("Memory dreaming service stopped");
    }
}
