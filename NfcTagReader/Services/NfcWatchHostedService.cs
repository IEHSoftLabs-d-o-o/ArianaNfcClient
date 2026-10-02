using System.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NfcTagReader.Services.Nfc;
using NfcTagReader.ViewModels;

namespace NfcTagReader.Services;

public sealed class NfcWatchHostedService : BackgroundService
{
    private readonly INfcReaderService _nfcReaderService;
    private readonly MainViewModel _viewModel;
    private readonly ILogger<NfcWatchHostedService> _logger;

    public NfcWatchHostedService(
        INfcReaderService nfcReaderService,
        MainViewModel viewModel,
        ILogger<NfcWatchHostedService> logger)
    {
        _nfcReaderService = nfcReaderService;
        _viewModel = viewModel;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var evt in _nfcReaderService.WatchAsync(stoppingToken))
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null)
                {
                    await _viewModel.HandleEventAsync(evt, stoppingToken);
                    continue;
                }

                await dispatcher.Invoke(() => _viewModel.HandleEventAsync(evt, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NFC watch loop failed");
        }
    }
}
