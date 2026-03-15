using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using SKKPedigree.Data;
using SKKPedigree.Scraper;

namespace SKKPedigree.App.ViewModels
{
    public class FullScrapeViewModel : ViewModelBase
    {
        private readonly FullScrapeJob _job;
        private readonly DogRepository _dogRepo;
        private readonly AppSettings _settings;

        private CancellationTokenSource? _cts;

        private bool _isScraping;
        private int _scrapedTotal;
        private int _queuedCount;
        private int _skippedCount;
        private int _errorCount;
        private string _currentDog = "";
        private string _status = "Ready";
        private int _batchSize = 10;
        private string _logFilePath;

        public bool IsScraping
        {
            get => _isScraping;
            set
            {
                SetProperty(ref _isScraping, value);
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
            }
        }

        public bool CanStart => !IsScraping;
        public bool CanStop => IsScraping;

        public int ScrapedTotal { get => _scrapedTotal; set => SetProperty(ref _scrapedTotal, value); }
        public int QueuedCount  { get => _queuedCount;  set => SetProperty(ref _queuedCount,  value); }
        public int SkippedCount { get => _skippedCount; set => SetProperty(ref _skippedCount, value); }
        public int ErrorCount   { get => _errorCount;   set => SetProperty(ref _errorCount,   value); }
        public string CurrentDog { get => _currentDog; set => SetProperty(ref _currentDog, value); }
        public string Status { get => _status; set => SetProperty(ref _status, value); }

        public int BatchSize
        {
            get => _batchSize;
            set => SetProperty(ref _batchSize, Math.Max(1, value));
        }

        public string LogFilePath
        {
            get => _logFilePath;
            set => SetProperty(ref _logFilePath, value);
        }

        /// <summary>Rolling log lines shown in the UI (newest at top).</summary>
        public ObservableCollection<string> LogLines { get; } = new();

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand OpenLogFolderCommand { get; }

        public FullScrapeViewModel(FullScrapeJob job, DogRepository dogRepo, AppSettings settings)
        {
            _job = job;
            _dogRepo = dogRepo;
            _settings = settings;
            _logFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SKKPedigree", "scrape_log.txt");

            StartCommand = new RelayCommand(async () => await StartAsync(), () => CanStart);
            StopCommand  = new RelayCommand(() => _cts?.Cancel(), () => CanStop);
            OpenLogFolderCommand = new RelayCommand(() =>
            {
                var dir = Path.GetDirectoryName(LogFilePath) ?? "";
                if (Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", dir);
            });
        }

        private async Task StartAsync()
        {
            IsScraping = true;
            LogLines.Clear();

            _cts = new CancellationTokenSource();

            var opts = new FullScrapeOptions
            {
                BatchSize = BatchSize,
                RequestDelayMs = _settings.RequestDelayMs,
                Headless = _settings.HeadlessBrowser,
                LogFilePath = LogFilePath,
                SkipIfScrapedWithin = TimeSpan.FromDays(30)
            };

            var progress = new Progress<FullScrapeProgress>(p =>
            {
                // All callbacks arrive on the UI thread via Progress<T>
                ScrapedTotal = p.ScrapedTotal;
                QueuedCount  = p.QueuedCount;
                SkippedCount = p.SkippedCount;
                ErrorCount   = p.ErrorCount;
                CurrentDog   = string.IsNullOrEmpty(p.CurrentDogName) ? p.CurrentUrl : p.CurrentDogName;
                Status       = p.Status;

                if (!string.IsNullOrEmpty(p.LogLine))
                {
                    var line = $"[{DateTime.Now:HH:mm:ss}] {p.LogLine}";
                    LogLines.Insert(0, line);        // newest at top

                    // Cap the visible list to avoid memory growth
                    while (LogLines.Count > 500)
                        LogLines.RemoveAt(LogLines.Count - 1);
                }
            });

            try
            {
                await _job.RunAsync(opts, progress, _cts.Token);
            }
            finally
            {
                IsScraping = false;
                _cts?.Dispose();
                _cts = null;

                var total = await _dogRepo.GetCountAsync();
                AddLocalLog($"Session ended. Total dogs in DB: {total}");
            }
        }

        private void AddLocalLog(string msg)
        {
            LogLines.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
            while (LogLines.Count > 500) LogLines.RemoveAt(LogLines.Count - 1);
        }
    }
}
