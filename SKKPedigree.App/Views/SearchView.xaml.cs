using System.Windows;
using System.Windows.Controls;
using SKKPedigree.App.ViewModels;
using SKKPedigree.Data.Models;

namespace SKKPedigree.App.Views
{
    public partial class SearchView : UserControl
    {
        public static readonly RoutedEvent DogDoubleClickedEvent =
            EventManager.RegisterRoutedEvent(nameof(DogDoubleClicked), RoutingStrategy.Bubble,
                typeof(RoutedEventHandler<DogRecord>), typeof(SearchView));

        public event RoutedEventHandler DogDoubleClicked
        {
            add => AddHandler(DogDoubleClickedEvent, value);
            remove => RemoveHandler(DogDoubleClickedEvent, value);
        }

        public SearchView()
        {
            InitializeComponent();
        }

        private void DataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (DataContext is SearchViewModel vm && vm.SelectedDog != null)
                vm.RaiseDogSelected(vm.SelectedDog);
        }
    }

    // Generic RoutedEventHandler alias
    public delegate void RoutedEventHandler<T>(object sender, T e);
}
