using System;
using System.Windows;
using System.Windows.Controls;
using SKKPedigree.App.ViewModels;
using SKKPedigree.Data.Models;

namespace SKKPedigree.App
{
    public partial class MainWindow : Window
    {
        private readonly SearchViewModel _searchVm;
        private readonly PedigreeViewModel _pedigreeVm;
        private readonly LitterViewModel _litterVm;
        private readonly RelationViewModel _relationVm;
        private readonly FullScrapeViewModel _fullScrapeVm;

        public MainWindow(
            SearchViewModel searchVm,
            PedigreeViewModel pedigreeVm,
            LitterViewModel litterVm,
            RelationViewModel relationVm,
            FullScrapeViewModel fullScrapeVm)
        {
            InitializeComponent();

            _searchVm = searchVm;
            _pedigreeVm = pedigreeVm;
            _litterVm = litterVm;
            _relationVm = relationVm;
            _fullScrapeVm = fullScrapeVm;

            SearchViewControl.DataContext = searchVm;
            PedigreeViewControl.DataContext = pedigreeVm;
            LitterViewControl.DataContext = litterVm;
            RelationViewControl.DataContext = relationVm;
            FullScrapeViewControl.DataContext = fullScrapeVm;

            // When a dog is selected in Search, navigate to Pedigree tab
            searchVm.DogSelected += OnDogSelected;
        }

        private void OnDogSelected(object? sender, DogRecord? dog)
        {
            if (dog == null) return;
            _pedigreeVm.RootDog = dog;
            _litterVm.SelectedDog = dog;
            MainTabs.SelectedItem = TabPedigree;
            LastUpdatedText.Text = $"Loaded: {dog.Name}  ({DateTime.Now:HH:mm:ss})";
        }

        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Optionally sync status from active view's VM
        }
    }
}

