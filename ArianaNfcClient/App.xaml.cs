using System.IO;
using System.Reflection;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NLog;
using NLog.Extensions.Hosting;
using ArianaNfcClient.Models;
using ArianaNfcClient.Services;
using ArianaNfcClient.Services.ArianaLab;
using ArianaNfcClient.Services.Connection;
using ArianaNfcClient.Services.Nfc;
using ArianaNfcClient.Services.Validation;
using ArianaNfcClient.ViewModels;
using ArianaNfcClient.Views;

namespace ArianaNfcClient;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var nlogConfig = Path.Combine(AppContext.BaseDirectory, "nlog.config");
        LogManager.Setup().LoadConfigurationFromFile(nlogConfig);
        var bootstrap = LogManager.GetCurrentClassLogger();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            bootstrap.Info("Application starting. Version {0}", version);
            ApplyInstalledIcon();

            _host = Host.CreateDefaultBuilder()
                .UseContentRoot(AppContext.BaseDirectory)
                .ConfigureAppConfiguration((_, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory);
                    config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                })
                .UseNLog()
                .ConfigureServices((context, services) =>
                {
                    services.Configure<ArianaLabOptions>(context.Configuration.GetSection(ArianaLabOptions.SectionName));
                    services.AddSingleton<IConnectionSettingsStore, ConnectionSettingsStore>();
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

            ApplyLocalConnection(_host.Services);

            await _host.StartAsync();

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            var options = _host.Services.GetRequiredService<IOptions<ArianaLabOptions>>().Value;
            logger.LogInformation(
                "Application started. Version {Version}. ArianaLab {BaseUrl}",
                version,
                string.IsNullOrWhiteSpace(options.BaseUrl) ? "(not configured)" : options.BaseUrl);

            var window = _host.Services.GetRequiredService<MainWindow>();
            window.Show();
        }
        catch (Exception ex)
        {
            bootstrap.Error(ex, "Application startup failed");
            LogManager.Shutdown();
            throw;
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            var logger = _host?.Services.GetService<ILogger<App>>();
            logger?.LogInformation("Application shutting down");

            if (_host is not null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(2));
                _host.Dispose();
                _host = null;
            }
        }
        catch (Exception ex)
        {
            LogManager.GetCurrentClassLogger().Error(ex, "Application shutdown failed");
        }
        finally
        {
            LogManager.Shutdown();
        }

        base.OnExit(e);
    }

    private const string ProductName = "Ariana NFC Client";

    private static void ApplyInstalledIcon()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ClickOnce_IsNetworkDeployed"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "nfc.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        try
        {
            using var uninstall = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                writable: true);
            if (uninstall is null)
            {
                return;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var appKey = uninstall.OpenSubKey(name, writable: true);
                if (appKey?.GetValue("DisplayName") as string != ProductName)
                {
                    continue;
                }

                appKey.SetValue("DisplayIcon", iconPath);
            }
        }
        catch (Exception ex)
        {
            LogManager.GetCurrentClassLogger().Warn(ex, "Programs and Features icon could not be set");
        }
    }

    private static void ApplyLocalConnection(IServiceProvider services)
    {
        if (!File.Exists(ConnectionSettingsStore.FilePath))
        {
            return;
        }

        var options = services.GetRequiredService<IOptions<ArianaLabOptions>>().Value;
        var connection = services.GetRequiredService<IConnectionSettingsStore>().Load();
        options.BaseUrl = connection.BaseUrl;
        options.Username = connection.Username;
        options.Password = connection.Password;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogManager.GetCurrentClassLogger().Error(e.Exception, "Unhandled UI exception");
        LogManager.Shutdown();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogManager.GetCurrentClassLogger().Error(ex, "Unhandled exception");
        }

        LogManager.Shutdown();
    }
}
