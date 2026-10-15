using ArianaNfcClient.Services.Nfc;
using ArianaNfcClient.ViewModels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows;

namespace ArianaNfcClient.Services;

public sealed class NfcWatchHostedService(
    INfcReaderService nfcReaderService,
    MainViewModel viewModel,
    ILogger<NfcWatchHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("NFC watch started");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await foreach (var evt in nfcReaderService.WatchAsync(stoppingToken))
                    {
                        var dispatcher = Application.Current?.Dispatcher;
                        if (dispatcher is null)
                        {
                            await viewModel.HandleEventAsync(evt, stoppingToken);
                            continue;
                        }

                        await dispatcher.Invoke(() => viewModel.HandleEventAsync(evt, stoppingToken));
                    }

                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "NFC watch loop failed");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            logger.LogInformation("NFC watch stopped");
        }
    }
}
