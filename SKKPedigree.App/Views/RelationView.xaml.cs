using System.Windows.Controls;
using SKKPedigree.App.ViewModels;
using SKKPedigree.Data.Models;

namespace SKKPedigree.App.Views
{
    public partial class RelationView : UserControl
    {
        public RelationView() => InitializeComponent();

        private void DogAList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox lb && lb.SelectedItem is DogRecord dog &&
                DataContext is RelationViewModel vm)
                vm.SelectDogACommand.Execute(dog);
        }

        private void DogBList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox lb && lb.SelectedItem is DogRecord dog &&
                DataContext is RelationViewModel vm)
                vm.SelectDogBCommand.Execute(dog);
        }
    }
}
