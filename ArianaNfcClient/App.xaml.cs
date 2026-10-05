using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NfcTagReader.Models;
using NfcTagReader.Services;
using NfcTagReader.Services.ArianaLab;
using NfcTagReader.Services.Nfc;
using NfcTagReader.Services.Validation;
using NfcTagReader.ViewModels;
using NfcTagReader.Views;

namespace NfcTagReader;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<ArianaLabOptions>(context.Configuration.GetSection(ArianaLabOptions.SectionName));
                services.AddSingleton<INfcReaderService, Acr1552NfcReaderService>();
                services.AddSingleton<ITagPayloadValidator, TagPayloadValidator>();
                services.AddHttpClient<IArianaLabClient, ArianaLabClient>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                });
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddHostedService<NfcWatchHostedService>();
            })
            .Build();

        await _host.StartAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(2));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
