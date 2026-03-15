using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SKKPedigree.App.ViewModels;
using SKKPedigree.Data;
using SKKPedigree.Scraper;

namespace SKKPedigree.App
{
    public partial class App : Application
    {
        private IServiceProvider? _services;

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Write any crash to a log file so we can diagnose it
            var crashLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SKKPedigree", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(crashLog)!);

            DispatcherUnhandledException += (s, ex) =>
            {
                File.AppendAllText(crashLog, $"[{DateTime.Now:u}] DISPATCHER: {ex.Exception}\n\n");
                ex.Handled = true;
                MessageBox.Show(ex.Exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                File.AppendAllText(crashLog, $"[{DateTime.Now:u}] UNHANDLED: {ex.ExceptionObject}\n\n");
            };

            base.OnStartup(e);

            var settings = AppSettings.Load();
            settings.Save(); // Ensure settings file exists

            var services = new ServiceCollection();

            // Infrastructure
            services.AddSingleton(settings);
            services.AddSingleton(new Database(settings.DatabasePath));
            services.AddSingleton<SkkSession>(sp =>
                new SkkSession(settings.RequestDelayMs));

            // Repositories
            services.AddScoped<DogRepository>();
            services.AddScoped<RelationRepository>();

            // Scraper
            services.AddTransient<DogScraper>();
            services.AddTransient<ScrapeJob>();
            services.AddTransient<FullScrapeJob>();

            // ViewModels
            services.AddTransient<SearchViewModel>();
            services.AddTransient<PedigreeViewModel>();
            services.AddTransient<LitterViewModel>();
            services.AddTransient<RelationViewModel>();
            services.AddTransient<FullScrapeViewModel>();

            // Logging
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));

            _services = services.BuildServiceProvider();

            // Run DB migrations
            var db = _services.GetRequiredService<Database>();
            await db.RunMigrationsAsync();

            // Show first-launch disclaimer
            await ShowDisclaimerIfNeededAsync();

            // Create and show main window
            try
            {
                var scope = _services.CreateScope();
                var window = new MainWindow(
                    scope.ServiceProvider.GetRequiredService<SearchViewModel>(),
                    scope.ServiceProvider.GetRequiredService<PedigreeViewModel>(),
                    scope.ServiceProvider.GetRequiredService<LitterViewModel>(),
                    scope.ServiceProvider.GetRequiredService<RelationViewModel>(),
                    scope.ServiceProvider.GetRequiredService<FullScrapeViewModel>());
                window.Show();
            }
            catch (Exception ex)
            {
                File.AppendAllText(crashLog, $"[{DateTime.Now:u}] STARTUP: {ex}\n\n");
                MessageBox.Show($"Startup error:\n{ex.Message}\n\nSee: {crashLog}",
                    "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_services is IDisposable d) d.Dispose();
            base.OnExit(e);
            await Task.CompletedTask;
        }

        private static Task ShowDisclaimerIfNeededAsync()
        {
            var flagFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SKKPedigree", "disclaimer_accepted");

            if (File.Exists(flagFile)) return Task.CompletedTask;

            var msg = "SKK Hunddata — Pedigree Browser\n\n" +
                      "IMPORTANT — Please read before continuing:\n\n" +
                      "All material on hundar.skk.se is copyright-protected by SKK " +
                      "(Svenska Kennelklubben).\n\n" +
                      "This application is intended for PERSONAL / RESEARCH USE ONLY.\n" +
                      "• Do not redistribute scraped data.\n" +
                      "• Do not build public-facing services with it.\n" +
                      "• The application enforces a minimum 1.5 second delay between requests " +
                      "to avoid overloading the SKK servers.\n\n" +
                      "By clicking OK you acknowledge these terms.";

            MessageBox.Show(msg, "Legal Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            Directory.CreateDirectory(Path.GetDirectoryName(flagFile)!);
            File.WriteAllText(flagFile, DateTime.UtcNow.ToString("o"));
            return Task.CompletedTask;
        }
    }
}

