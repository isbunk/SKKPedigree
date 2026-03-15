using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using SKKPedigree.Data;
using SKKPedigree.Data.Models;
using SKKPedigree.Scraper;

namespace SKKPedigree.App.ViewModels
{
    public class SearchViewModel : ViewModelBase
    {
        private readonly DogRepository _dogRepo;
        private readonly DogScraper _scraper;
        private readonly AppSettings _settings;

        private string _searchText = "";
        private bool _isBusy;
        private string _status = "";
        private DogRecord? _selectedDog;

        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public DogRecord? SelectedDog
        {
            get => _selectedDog;
            set
            {
                SetProperty(ref _selectedDog, value);
                DogSelected?.Invoke(this, value);
            }
        }

        public ObservableCollection<DogRecord> Results { get; } = new();

        public ICommand SearchLocalCommand { get; }
        public ICommand FetchFromSkkCommand { get; }

        public event EventHandler<DogRecord?>? DogSelected;

        public void RaiseDogSelected(DogRecord? dog) => DogSelected?.Invoke(this, dog);

        public SearchViewModel(DogRepository dogRepo, DogScraper scraper, AppSettings settings)
        {
            _dogRepo = dogRepo;
            _scraper = scraper;
            _settings = settings;

            SearchLocalCommand = new RelayCommand(async () => await SearchLocalAsync(),
                () => !IsBusy && !string.IsNullOrWhiteSpace(SearchText));

            FetchFromSkkCommand = new RelayCommand(async () => await FetchFromSkkAsync(),
                () => !IsBusy && !string.IsNullOrWhiteSpace(SearchText));
        }

        private async Task SearchLocalAsync()
        {
            IsBusy = true;
            Status = "Searching local database…";
            Results.Clear();
            try
            {
                var dogs = await _dogRepo.SearchByNameAsync(SearchText.Trim());
                foreach (var d in dogs) Results.Add(d);
                Status = $"{dogs.Count} result(s) found locally.";
            }
            catch (Exception ex)
            {
                Status = $"Error: {ex.Message}";
            }
            finally { IsBusy = false; }
        }

        private async Task FetchFromSkkAsync()
        {
            IsBusy = true;
            Status = "Fetching from SKK…";
            Results.Clear();
            try
            {
                var dog = await _scraper.ScrapeByRegNumberAsync(SearchText.Trim());
                await _dogRepo.UpsertAsync(dog);
                Results.Add(dog);
                Status = $"Fetched '{dog.Name}' from SKK.";
            }
            catch (Exception ex)
            {
                Status = $"Fetch error: {ex.Message}";
            }
            finally { IsBusy = false; }
        }
    }
}
