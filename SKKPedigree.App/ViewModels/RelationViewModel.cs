using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using SKKPedigree.Data;
using SKKPedigree.Data.Models;

namespace SKKPedigree.App.ViewModels
{
    public class CommonAncestorEntry
    {
        public DogRecord Dog { get; set; } = null!;
        public string Display => $"{Dog.Name} ({Dog.Id})";
    }

    public class RelationViewModel : ViewModelBase
    {
        private readonly DogRepository _dogRepo;
        private readonly RelationRepository _relationRepo;

        private string _dogASearch = "";
        private string _dogBSearch = "";
        private DogRecord? _dogA;
        private DogRecord? _dogB;
        private bool _isBusy;
        private string _status = "";
        private double _inbreedingCoefficient;

        public string DogASearch { get => _dogASearch; set => SetProperty(ref _dogASearch, value); }
        public string DogBSearch { get => _dogBSearch; set => SetProperty(ref _dogBSearch, value); }

        public DogRecord? DogA
        {
            get => _dogA;
            set { SetProperty(ref _dogA, value); OnPropertyChanged(nameof(DogADisplay)); }
        }

        public DogRecord? DogB
        {
            get => _dogB;
            set { SetProperty(ref _dogB, value); OnPropertyChanged(nameof(DogBDisplay)); }
        }

        public string DogADisplay => _dogA != null ? $"{_dogA.Name} ({_dogA.Id})" : "Not selected";
        public string DogBDisplay => _dogB != null ? $"{_dogB.Name} ({_dogB.Id})" : "Not selected";

        public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
        public string Status { get => _status; set => SetProperty(ref _status, value); }

        public double InbreedingCoefficient
        {
            get => _inbreedingCoefficient;
            set
            {
                SetProperty(ref _inbreedingCoefficient, value);
                OnPropertyChanged(nameof(InbreedingPercent));
            }
        }

        public string InbreedingPercent => $"{InbreedingCoefficient * 100:F2}%";

        public ObservableCollection<DogRecord> DogAResults { get; } = new();
        public ObservableCollection<DogRecord> DogBResults { get; } = new();
        public ObservableCollection<CommonAncestorEntry> CommonAncestors { get; } = new();

        public ICommand SearchDogACommand { get; }
        public ICommand SearchDogBCommand { get; }
        public ICommand FindRelationCommand { get; }
        public ICommand SelectDogACommand { get; }
        public ICommand SelectDogBCommand { get; }

        public RelationViewModel(DogRepository dogRepo, RelationRepository relationRepo)
        {
            _dogRepo = dogRepo;
            _relationRepo = relationRepo;

            SearchDogACommand = new RelayCommand(async () => await SearchAsync(DogASearch, DogAResults));
            SearchDogBCommand = new RelayCommand(async () => await SearchAsync(DogBSearch, DogBResults));

            SelectDogACommand = new RelayCommand(p => { if (p is DogRecord d) DogA = d; });
            SelectDogBCommand = new RelayCommand(p => { if (p is DogRecord d) DogB = d; });

            FindRelationCommand = new RelayCommand(
                async () => await FindRelationAsync(),
                () => !IsBusy && DogA != null && DogB != null);
        }

        private async Task SearchAsync(string query, ObservableCollection<DogRecord> results)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            results.Clear();
            var dogs = await _dogRepo.SearchByNameAsync(query.Trim());
            foreach (var d in dogs) results.Add(d);
        }

        private async Task FindRelationAsync()
        {
            if (DogA == null || DogB == null) return;
            IsBusy = true;
            Status = "Finding common ancestors…";
            CommonAncestors.Clear();
            try
            {
                var ancestors = await _relationRepo.FindCommonAncestorsAsync(DogA.Id, DogB.Id);
                foreach (var a in ancestors)
                    CommonAncestors.Add(new CommonAncestorEntry { Dog = a });

                // Calculate inbreeding coefficient for DogA if DogB is one of its parents,
                // otherwise calculate for a hypothetical offspring (if they were mated).
                InbreedingCoefficient = await _relationRepo.CalculateInbreedingCoefficientAsync(DogA.Id);

                Status = $"Found {CommonAncestors.Count} common ancestor(s).";
            }
            catch (Exception ex) { Status = $"Error: {ex.Message}"; }
            finally { IsBusy = false; }
        }
    }
}
