using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SKKPedigree.App.ViewModels;

namespace SKKPedigree.App.Views
{
    public partial class PedigreeView : UserControl
    {
        private const double NodeW = 160;
        private const double NodeH = 70;

        public PedigreeView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is PedigreeViewModel oldVm)
            {
                oldVm.Nodes.CollectionChanged -= OnNodesChanged;
                oldVm.Edges.CollectionChanged -= OnEdgesChanged;
            }
            if (e.NewValue is PedigreeViewModel vm)
            {
                vm.Nodes.CollectionChanged += OnNodesChanged;
                vm.Edges.CollectionChanged += OnEdgesChanged;
                NodesControl.ItemsSource = vm.Nodes;
                RedrawEdges();
            }
        }

        private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => Dispatcher.Invoke(RedrawEdges);

        private void OnEdgesChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => Dispatcher.Invoke(RedrawEdges);

        private void RedrawEdges()
        {
            EdgesControl.Items.Clear();
            if (DataContext is not PedigreeViewModel vm) return;

            foreach (var (from, to) in vm.Edges)
            {
                // Source (left-centre) and target (right-centre)
                double x1 = from.X + NodeW;
                double y1 = from.Y + NodeH / 2;
                double x2 = to.X;
                double y2 = to.Y + NodeH / 2;

                var path = new Path
                {
                    Stroke = Brushes.SlateGray,
                    StrokeThickness = 1.5,
                    Data = BuildBezier(x1, y1, x2, y2)
                };
                EdgesControl.Items.Add(path);
            }
        }

        private static Geometry BuildBezier(double x1, double y1, double x2, double y2)
        {
            double cx = (x1 + x2) / 2;
            var figure = new PathFigure { StartPoint = new Point(x1, y1) };
            figure.Segments.Add(new BezierSegment(
                new Point(cx, y1),
                new Point(cx, y2),
                new Point(x2, y2),
                isStroked: true));
            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            return geo;
        }

        private void DogNode_Selected(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is Controls.DogNodeControl control && control.Dog != null)
            {
                if (DataContext is PedigreeViewModel vm)
                    vm.SetRootCommand.Execute(control.Dog);
            }
        }

        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) { }

        // Basic zoom with Ctrl+scroll
        private ScaleTransform? _scale;
        private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl)) return;
            if (_scale == null)
            {
                _scale = new ScaleTransform(1, 1);
                PedigreeCanvas.LayoutTransform = _scale;
            }
            double factor = e.Delta > 0 ? 1.1 : 0.9;
            _scale.ScaleX = System.Math.Clamp(_scale.ScaleX * factor, 0.2, 3.0);
            _scale.ScaleY = _scale.ScaleX;
            e.Handled = true;
        }
    }
}
