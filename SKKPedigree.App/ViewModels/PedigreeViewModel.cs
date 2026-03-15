using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using SKKPedigree.Data;
using SKKPedigree.Data.Models;
using SKKPedigree.Scraper;

namespace SKKPedigree.App.ViewModels
{
    /// <summary>
    /// Represents a single node in the pedigree tree.
    /// </summary>
    public class DogNode : ViewModelBase
    {
        private bool _isRoot;
        private bool _isHighlighted;

        public DogRecord Dog { get; }
        public int Column { get; set; }     // 0 = root, positive = ancestors, negative = descendants
        public int Row { get; set; }        // vertical slot
        public double X { get; set; }
        public double Y { get; set; }

        public bool IsRoot
        {
            get => _isRoot;
            set => SetProperty(ref _isRoot, value);
        }

        public bool IsHighlighted
        {
            get => _isHighlighted;
            set => SetProperty(ref _isHighlighted, value);
        }

        public List<DogNode> Children { get; } = new(); // ancestor nodes (right-side)

        public DogNode(DogRecord dog) => Dog = dog;
    }

    public class PedigreeViewModel : ViewModelBase
    {
        private readonly DogRepository _dogRepo;
        private readonly DogScraper _scraper;
        private readonly AppSettings _settings;

        private DogRecord? _rootDog;
        private bool _isBusy;
        private string _status = "";

        public DogRecord? RootDog
        {
            get => _rootDog;
            set
            {
                SetProperty(ref _rootDog, value);
                if (value != null) _ = BuildTreeAsync(value);
            }
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

        public ObservableCollection<DogNode> Nodes { get; } = new();
        public ObservableCollection<(DogNode From, DogNode To)> Edges { get; } = new();

        public ICommand SetRootCommand { get; }
        public ICommand ExpandNodeCommand { get; }

        public PedigreeViewModel(DogRepository dogRepo, DogScraper scraper, AppSettings settings)
        {
            _dogRepo = dogRepo;
            _scraper = scraper;
            _settings = settings;

            SetRootCommand = new RelayCommand(async (p) =>
            {
                if (p is DogRecord dog) RootDog = dog;
                else if (p is string id)
                {
                    var d = await _dogRepo.GetByIdAsync(id);
                    if (d != null) RootDog = d;
                }
            });

            ExpandNodeCommand = new RelayCommand(async (p) =>
            {
                if (p is DogNode node) await ExpandNodeAsync(node);
            });
        }

        private async Task BuildTreeAsync(DogRecord root)
        {
            IsBusy = true;
            Status = "Building pedigree tree…";
            Nodes.Clear();
            Edges.Clear();

            try
            {
                // BFS-style layout: column 0 = root, each column to the right = one generation back
                var rootNode = new DogNode(root) { IsRoot = true, Column = 0, Row = 0 };
                Nodes.Add(rootNode);

                await ExpandAncestorsAsync(rootNode, _settings.DefaultGenerations);

                // Layout descendants to the left
                var children = await _dogRepo.GetChildrenAsync(root.Id);
                int childRow = 1;
                foreach (var child in children)
                {
                    var childNode = new DogNode(child) { Column = -1, Row = childRow++ };
                    Nodes.Add(childNode);
                    Edges.Add((childNode, rootNode));
                }

                ApplyLayout();
                Status = $"Pedigree loaded — {Nodes.Count} dog(s).";
            }
            catch (Exception ex)
            {
                Status = $"Error building tree: {ex.Message}";
            }
            finally { IsBusy = false; }
        }

        private async Task ExpandAncestorsAsync(DogNode node, int remainingDepth)
        {
            if (remainingDepth <= 0) return;
            var dog = node.Dog;

            if (!string.IsNullOrEmpty(dog.FatherId))
            {
                var father = await _dogRepo.GetByIdAsync(dog.FatherId);
                if (father != null)
                {
                    var fatherNode = new DogNode(father)
                    {
                        Column = node.Column + 1,
                        Row = node.Row * 2
                    };
                    Nodes.Add(fatherNode);
                    Edges.Add((node, fatherNode));
                    node.Children.Add(fatherNode);
                    await ExpandAncestorsAsync(fatherNode, remainingDepth - 1);
                }
            }

            if (!string.IsNullOrEmpty(dog.MotherId))
            {
                var mother = await _dogRepo.GetByIdAsync(dog.MotherId);
                if (mother != null)
                {
                    var motherNode = new DogNode(mother)
                    {
                        Column = node.Column + 1,
                        Row = node.Row * 2 + 1
                    };
                    Nodes.Add(motherNode);
                    Edges.Add((node, motherNode));
                    node.Children.Add(motherNode);
                    await ExpandAncestorsAsync(motherNode, remainingDepth - 1);
                }
            }
        }

        private async Task ExpandNodeAsync(DogNode node)
        {
            IsBusy = true;
            Status = $"Expanding {node.Dog.Name}…";
            try
            {
                // Try to fetch fresh data from SKK
                if (!string.IsNullOrEmpty(node.Dog.FatherUrl) || !string.IsNullOrEmpty(node.Dog.MotherUrl))
                {
                    var freshDog = string.IsNullOrEmpty(node.Dog.Id)
                        ? node.Dog
                        : await _scraper.ScrapeByRegNumberAsync(node.Dog.Id);
                    await _dogRepo.UpsertAsync(freshDog);
                }

                await ExpandAncestorsAsync(node, 1);
                ApplyLayout();
                Status = $"Expanded {node.Dog.Name}.";
            }
            catch (Exception ex)
            {
                Status = $"Expand error: {ex.Message}";
            }
            finally { IsBusy = false; }
        }

        private void ApplyLayout()
        {
            const double nodeWidth = 180;
            const double nodeHeight = 90;
            const double colGap = 30;
            const double rowGap = 15;

            foreach (var node in Nodes)
            {
                node.X = node.Column * (nodeWidth + colGap);
                node.Y = node.Row * (nodeHeight + rowGap);
                OnPropertyChanged(nameof(Nodes)); // trigger canvas refresh
            }
        }

        /// <summary>
        /// Highlights nodes that are common ancestors (for relation view integration).
        /// </summary>
        public void HighlightCommonAncestors(IEnumerable<string> ids)
        {
            var set = new HashSet<string>(ids);
            foreach (var node in Nodes)
                node.IsHighlighted = set.Contains(node.Dog.Id);
        }
    }
}
