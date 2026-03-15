using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using SKKPedigree.Data;
using SKKPedigree.Data.Models;
using SKKPedigree.Scraper;

namespace SKKPedigree.App.ViewModels
{
    public class LitterViewModel : ViewModelBase
    {
        private readonly DogRepository _dogRepo;
        private readonly DogScraper _scraper;

        private DogRecord? _selectedDog;
        private DogRecord? _father;
        private DogRecord? _mother;
        private bool _isBusy;
        private string _status = "";

        public DogRecord? SelectedDog
        {
            get => _selectedDog;
            set
            {
                SetProperty(ref _selectedDog, value);
                if (value != null) _ = LoadLitterAsync(value);
            }
        }

        public DogRecord? Father { get => _father; set => SetProperty(ref _father, value); }
        public DogRecord? Mother { get => _mother; set => SetProperty(ref _mother, value); }
        public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
        public string Status { get => _status; set => SetProperty(ref _status, value); }

        public ObservableCollection<DogRecord> Siblings { get; } = new();

        public ICommand LoadSiblingsFromSkkCommand { get; }

        public LitterViewModel(DogRepository dogRepo, DogScraper scraper)
        {
            _dogRepo = dogRepo;
            _scraper = scraper;
            LoadSiblingsFromSkkCommand = new RelayCommand(
                async () => await LoadSiblingsFromSkkAsync(),
                () => !IsBusy && SelectedDog != null);
        }

        private async Task LoadLitterAsync(DogRecord dog)
        {
            IsBusy = true;
            Status = "Loading litter data…";
            Siblings.Clear();
            Father = null;
            Mother = null;
            try
            {
                if (!string.IsNullOrEmpty(dog.FatherId))
                    Father = await _dogRepo.GetByIdAsync(dog.FatherId);
                if (!string.IsNullOrEmpty(dog.MotherId))
                    Mother = await _dogRepo.GetByIdAsync(dog.MotherId);

                var siblings = await _dogRepo.GetSiblingsAsync(dog.Id);
                foreach (var s in siblings) Siblings.Add(s);
                Status = $"{Siblings.Count} sibling(s) found.";
            }
            catch (Exception ex) { Status = $"Error: {ex.Message}"; }
            finally { IsBusy = false; }
        }

        private async Task LoadSiblingsFromSkkAsync()
        {
            if (SelectedDog == null) return;
            IsBusy = true;
            Status = "Fetching siblings from SKK…";
            try
            {
                var fresh = await _scraper.ScrapeByRegNumberAsync(SelectedDog.Id);
                foreach (var url in fresh.SiblingUrls)
                {
                    var sibling = await _scraper.ScrapeByUrlAsync(url);
                    await _dogRepo.UpsertAsync(sibling);
                    if (!ContainsDog(sibling.Id)) Siblings.Add(sibling);
                }
                Status = $"Siblings loaded. Total: {Siblings.Count}.";
            }
            catch (Exception ex) { Status = $"Error: {ex.Message}"; }
            finally { IsBusy = false; }
        }

        private bool ContainsDog(string id)
        {
            foreach (var s in Siblings)
                if (s.Id == id) return true;
            return false;
        }
    }
}
