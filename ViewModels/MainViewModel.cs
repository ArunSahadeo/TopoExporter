using System.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using TopoExporter.Models;
using TopoExporter.Services;

namespace TopoExporter.ViewModels
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute    = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add    => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
        public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;
        public void Execute(object? p)    => _execute(p);
    }

    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly TopoService _svc = new();

        // ── Backing fields ────────────────────────────────────────────────────
        private string _searchText  = string.Empty;
        private int    _currentPage = 0;
        private const int PageSize  = 5;
        private bool   _isDarkMode  = false;
        private string _statusText  = "Loading…";
        private bool   _isLoading   = false;

        private CountryItem _selectedCountryForPopup = null;

        public CountryItem SelectedCountryForPopup
        {
            get => _selectedCountryForPopup;
            set { _selectedCountryForPopup = value; OnPropertyChanged(); }
        }

        // ── Country remapping fields ────────────────────────────────────────────────────

        private static readonly string[] CountryNamesToExclude = {
            "Cyprus U.N. Buffer Zone",
            "Siachen Glacier",
            "Vatican",
            "Dhekelia"
        };

        private static readonly Dictionary<string, string> CountryNamesToStandardise = new Dictionary<string, string> {
            {"Republic of Korea", "South Korea"},
            {"Dem. Rep. Korea", "North Korea"},
            {"Lao PDR", "Laos"},
            {"Russian Federation", "Russia"},
            {"Côte d'Ivoire", "Ivory Coast"},
            {"Brunei Darussalam", "Brunei"},
            {"Kingdom of eSwatini", "Eswatini"},
            {"The Gambia", "Gambia"},
            {"Akrotiri", "Akrotiri and Dhekelia"},
            {"Republic of Cabo Verde", "Cabo Verde"},
            {"Federated States of Micronesia", "Micronesia"},
            {"Falkland Islands / Malvinas", "Falkland Islands"},
            {"Macao", "Macau"}
        };

        // ── Country data ──────────────────────────────────────────────────────
        // Full list, sorted by name, populated after GeoJSON is ready
        public ObservableCollection<CountryItem> AllCountries  { get; } = new();
        // Current page slice shown in the list box
        public ObservableCollection<CountryItem> PagedCountries { get; } = new();

        // ── Properties ────────────────────────────────────────────────────────
        public string SearchText
        {
            get => _searchText;
            set { _searchText = value; OnPropertyChanged(); _currentPage = 0; RefreshPage(); }
        }

        public bool IsDarkMode
        {
            get => _isDarkMode;
            set { _isDarkMode = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        private IEnumerable<CountryItem> Filtered =>
            string.IsNullOrWhiteSpace(_searchText)
                ? AllCountries
                : AllCountries.Where(c =>
                    c.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

        public int TotalPages => Math.Max(1,
            (int)Math.Ceiling(Filtered.Count() / (double)PageSize));

        public string PageInfo => $"Page {_currentPage + 1} of {TotalPages}";

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand PrevPageCommand      { get; }
        public ICommand NextPageCommand      { get; }
        public ICommand AddAllCommand        { get; }
        public ICommand ClearAllCommand      { get; }
        public ICommand SavePresetCommand    { get; }
        public ICommand LoadPresetCommand    { get; }
        public ICommand ExportCommand        { get; }
        public ICommand ToggleDarkModeCommand{ get; }

        // ── Events ────────────────────────────────────────────────────────────
        /// Fired whenever the selected-countries set changes. Sends the full list.
        public event Action<IEnumerable<CountryItem>>? SelectionChanged;
        public event Action<bool>? DarkModeChanged;
        public event Func<IEnumerable<CountryItem>, Task>? ExportRequested;
        public event Action<string>? ZoomToCountryRequested;

        // ── Constructor ───────────────────────────────────────────────────────
        public MainViewModel()
        {
            PrevPageCommand       = new RelayCommand(_ => ChangePage(-1));
            NextPageCommand       = new RelayCommand(_ => ChangePage(+1));
            AddAllCommand         = new RelayCommand(_ => SetFiltered(true));
            ClearAllCommand       = new RelayCommand(_ => SetFiltered(false));
            SavePresetCommand     = new RelayCommand(_ => SavePreset());
            LoadPresetCommand     = new RelayCommand(_ => LoadPreset());
            ExportCommand         = new RelayCommand(async _ => await Export());
            ToggleDarkModeCommand = new RelayCommand(_ => ToggleDark());
        }

        // ── Called by MainWindow once GeoJSON is ready ────────────────────────
        public async Task LoadCountriesFromGeoJsonAsync()
        {
            var entries = await _svc.ExtractCountriesAsync();

            Application.Current.Dispatcher.Invoke(() =>
            {
                AllCountries.Clear();
                foreach (var e in entries)
                {
                    var name = e.Name;

                    if (CountryNamesToExclude.Contains(name) || e.Type == "Lease") {
                        continue;
                    }

                    if (CountryNamesToStandardise.ContainsKey(name)) {
                        name = CountryNamesToStandardise[name];
                    }

                    var item = new CountryItem
                    {
                        Name        = name,
                        Code        = e.Code,
                        IsTerritory = e.Type != "Sovereign country" &&
                                      e.Type != "Country",
                        IsSelected  = true   // default: all selected
                    };
                    // Subscribe without boxing lambdas per item — use a shared handler
                    item.PropertyChanged += OnItemPropertyChanged;
                    AllCountries.Add(item);
                }
                RefreshPage();
                StatusText = $"{AllCountries.Count} territories loaded.";
            });
        }

        // Throttled: collect rapid checkbox changes and fire SelectionChanged once
        private System.Threading.Timer? _selectionDebounce;

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(CountryItem.IsSelected)) return;

            // Debounce: wait 80 ms after the last change before firing the event.
            // This makes "Select All" fire SelectionChanged just once instead of
            // once per country (~250 times).
            _selectionDebounce?.Dispose();
            _selectionDebounce = new System.Threading.Timer(_ =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                    SelectionChanged?.Invoke(AllCountries.Where(c => c.IsSelected)));
            }, null, 80, System.Threading.Timeout.Infinite);
        }

        // ── Page management ───────────────────────────────────────────────────
        private void ChangePage(int delta)
        {
            var next = _currentPage + delta;
            if (next < 0 || next >= TotalPages) return;
            _currentPage = next;
            RefreshPage();
        }

        public void RefreshPage()
        {
            PagedCountries.Clear();
            foreach (var item in Filtered.Skip(_currentPage * PageSize).Take(PageSize))
                PagedCountries.Add(item);
            OnPropertyChanged(nameof(PageInfo));
            OnPropertyChanged(nameof(TotalPages));
        }

        // ── Selection helpers ─────────────────────────────────────────────────
        private void SetFiltered(bool selected)
        {
            // Batch: suppress individual PropertyChanged events during the loop,
            // then fire one SelectionChanged at the end via the debounce.
            foreach (var c in Filtered.ToList())
                c.IsSelected = selected;
        }

        public void ToggleCountryByCode(string code)
        {
            var item = AllCountries.FirstOrDefault(c =>
                c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (item != null) item.IsSelected = !item.IsSelected;
        }

        // ── Preset ────────────────────────────────────────────────────────────
        private void SavePreset()
        {
            var codes = AllCountries.Where(c => c.IsSelected).Select(c => c.Code);
            _svc.SavePreset(codes);
            StatusText = "Preset saved.";
        }

        private void LoadPreset()
        {
            var codes = new HashSet<string>(_svc.LoadPreset(),
                StringComparer.OrdinalIgnoreCase);
            if (codes.Count == 0) return;
            foreach (var c in AllCountries)
                c.IsSelected = codes.Contains(c.Code);
            StatusText = $"Preset loaded ({codes.Count} countries).";
        }

        // ── Export ────────────────────────────────────────────────────────────
        private async Task Export()
        {
            var selected = AllCountries.Where(c => c.IsSelected).ToList();
            if (!selected.Any())
            {
                MessageBox.Show("Select at least one country first.",
                    "No selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (ExportRequested != null)
            {
                IsLoading  = true;
                StatusText = "Exporting…";
                try   { await ExportRequested.Invoke(selected); }
                catch (Exception ex)
                {
                    var st = new StackTrace(true);
                    var frame = st.GetFrame(0);
                    TopoService.Log($"Export exception at line {frame?.GetFileLineNumber()} of {frame?.GetFileName()}: {ex.Message}\n{ex}");
                    MessageBox.Show($"Export failed:\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText = "Export failed.";
                }
                finally { IsLoading = false; }
            }
        }

        // ── Dark mode ─────────────────────────────────────────────────────────
        private void ToggleDark()
        {
            IsDarkMode = !IsDarkMode;
            DarkModeChanged?.Invoke(IsDarkMode);
        }

        // ── Request zoom to country ─────────────────────────────────────────────────────────
        public void RequestZoomToCountry(string code)
        {
            ZoomToCountryRequested?.Invoke(code);
        }

        // ── INotifyPropertyChanged ────────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
