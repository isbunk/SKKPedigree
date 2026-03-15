using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SKKPedigree.Data.Models;

namespace SKKPedigree.App.Controls
{
    public partial class DogNodeControl : UserControl
    {
        // ── Dependency Properties ─────────────────────────────────────────

        public static readonly DependencyProperty DogProperty =
            DependencyProperty.Register(nameof(Dog), typeof(DogRecord), typeof(DogNodeControl),
                new PropertyMetadata(null, OnDogChanged));

        public static readonly DependencyProperty IsRootProperty =
            DependencyProperty.Register(nameof(IsRoot), typeof(bool), typeof(DogNodeControl),
                new PropertyMetadata(false, OnIsRootChanged));

        public static readonly RoutedEvent DogSelectedEvent =
            EventManager.RegisterRoutedEvent(nameof(DogSelected), RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(DogNodeControl));

        // ── CLR wrappers ──────────────────────────────────────────────────

        public DogRecord? Dog
        {
            get => (DogRecord?)GetValue(DogProperty);
            set => SetValue(DogProperty, value);
        }

        public bool IsRoot
        {
            get => (bool)GetValue(IsRootProperty);
            set => SetValue(IsRootProperty, value);
        }

        public event RoutedEventHandler DogSelected
        {
            add => AddHandler(DogSelectedEvent, value);
            remove => RemoveHandler(DogSelectedEvent, value);
        }

        // ── View-facing binding properties (set by DP callbacks) ──────────

        public string DogName { get; private set; } = "";
        public string Breed { get; private set; } = "";
        public string RegNumber { get; private set; } = "";
        public string SexSymbol { get; private set; } = "";
        public Brush SexColor { get; private set; } = Brushes.Gray;
        public new Brush BorderBrush { get; private set; } = Brushes.Gray;

        // ── Constructor ──────────────────────────────────────────────────

        public DogNodeControl()
        {
            InitializeComponent();
            DataContext = this;
            MouseLeftButtonUp += (_, _) =>
                RaiseEvent(new RoutedEventArgs(DogSelectedEvent, this));
        }

        // ── Callbacks ────────────────────────────────────────────────────

        private static void OnDogChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DogNodeControl ctrl) ctrl.Refresh();
        }

        private static void OnIsRootChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DogNodeControl ctrl) ctrl.Refresh();
        }

        private void Refresh()
        {
            if (Dog == null) return;

            DogName = Dog.Name;
            Breed = Dog.Breed ?? "";
            RegNumber = Dog.Id;

            if (Dog.Sex == "M")
            {
                SexSymbol = "♂";
                SexColor = new SolidColorBrush(Color.FromRgb(91, 155, 213));
                BorderBrush = IsRoot
                    ? new SolidColorBrush(Color.FromRgb(255, 192, 0))  // amber for root
                    : new SolidColorBrush(Color.FromRgb(91, 155, 213)); // blue for male
            }
            else if (Dog.Sex == "F")
            {
                SexSymbol = "♀";
                SexColor = new SolidColorBrush(Color.FromRgb(220, 120, 155));
                BorderBrush = IsRoot
                    ? new SolidColorBrush(Color.FromRgb(255, 192, 0))
                    : new SolidColorBrush(Color.FromRgb(220, 120, 155)); // pink for female
            }
            else
            {
                SexSymbol = "";
                SexColor = Brushes.Gray;
                BorderBrush = IsRoot
                    ? new SolidColorBrush(Color.FromRgb(255, 192, 0))
                    : Brushes.Gray;
            }

            // Force DataContext refresh
            DataContext = null;
            DataContext = this;
        }
    }
}
