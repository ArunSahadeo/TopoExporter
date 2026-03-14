using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using TopoExporter.Models;
using TopoExporter.Services;
using TopoExporter.ViewModels;

namespace TopoExporter
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public static readonly BoolToVisibilityConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility.Visible;
    }

    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm          = new();
        private readonly TopoService   _topoService  = new();
        private bool _mapReady = false;

        private const string VirtualHost = "topoexporter.local";

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;

            _vm.SelectionChanged  += OnSelectionChanged;
            _vm.DarkModeChanged   += OnDarkModeChanged;
            _vm.ExportRequested   += OnExportRequested;

            Loaded += MainWindow_Loaded;
        }

        // ── Startup ───────────────────────────────────────────────────────────
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await InitialiseWebViewAsync();

            // Download GeoJSON if needed, then:
            //  1. Tell the map to fetch and render it
            //  2. Build the left-panel country list from the actual GeoJSON features
            //  3. Apply saved preset
            await EnsureGeoJsonAndInitAsync();
        }

        // ── WebView2 ──────────────────────────────────────────────────────────
        private async Task InitialiseWebViewAsync()
        {
            try
            {
                await MapWebView.EnsureCoreWebView2Async();

                var wv = MapWebView.CoreWebView2;
                wv.Settings.IsStatusBarEnabled            = false;
                wv.Settings.AreDefaultContextMenusEnabled = false;
                wv.Settings.IsZoomControlEnabled          = false;

                // Serve the AppData folder under a virtual hostname so JS can
                // fetch() the GeoJSON without string injection size limits.
                wv.SetVirtualHostNameToFolderMapping(
                    VirtualHost,
                    TopoService.AppDataDir,
                    CoreWebView2HostResourceAccessKind.Allow);

                wv.WebMessageReceived     += WebView_MessageReceived;
                wv.NavigationCompleted    += WebView_NavigationCompleted;

                var htmlPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Assets", "map.html");

                if (File.Exists(htmlPath))
                    wv.Navigate(new Uri(htmlPath).AbsoluteUri);
                else
                    UpdateMapStatus("Assets/map.html not found.");
            }
            catch (Exception ex)
            {
                var st = new StackTrace(true);
                var frame = st.GetFrame(0);

                using (StreamWriter sw = File.AppendText(Path.Combine(TopoService.AppDataDir, "debug.log"))) {
                    sw.WriteLine($"The error from line {frame.GetFileLineNumber()} of {frame.GetFileName()}: {ex.Message}");
                    sw.WriteLine(ex.ToString());
                }

                UpdateMapStatus($"WebView2 init error: {ex.Message}");
            }
        }

        private async void WebView_NavigationCompleted(
            object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _mapReady = true;

            // If GeoJSON already on disk from a previous run, load it immediately
            if (File.Exists(TopoService.GeoJsonPath))
                await TellMapToLoadAsync();

            Dispatcher.Invoke(() =>
                MapLoadingOverlay.Visibility = Visibility.Collapsed);
        }

        // ── GeoJSON pipeline ──────────────────────────────────────────────────
        private async Task EnsureGeoJsonAndInitAsync()
        {
            var progress = new Progress<string>(msg => Dispatcher.Invoke(() =>
            {
                _vm.StatusText = msg;
                UpdateMapStatus(msg);
            }));

            try
            {
                await _topoService.EnsureGeoJsonExistsAsync(progress);

                // Tell map to (re)load in case NavigationCompleted fired before
                // the download finished.
                if (_mapReady)
                    await TellMapToLoadAsync();

                // Build country list from real GeoJSON features
                Dispatcher.Invoke(() => _vm.StatusText = "Building country list…");
                await _vm.LoadCountriesFromGeoJsonAsync();

                // Now apply preset (country list must exist first)
                _vm.LoadPresetCommand.Execute(null);

                // Push initial selection state to map
                await PushCountryMetaAsync();
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    var st = new StackTrace(true);
                    var frame = st.GetFrame(0);

                    using (StreamWriter sw = File.AppendText(Path.Combine(TopoService.AppDataDir, "debug.log"))) {
                        sw.WriteLine($"The error from line {frame.GetFileLineNumber()} of {frame.GetFileName()}: {ex.Message}");
                        sw.WriteLine(ex.ToString());
                    }

                    _vm.StatusText = $"Error: {ex.Message}";
                    UpdateMapStatus($"Failed:\n{ex.Message}");
                });
            }
        }

        /// <summary>
        /// Instructs the JS map to fetch the GeoJSON via the virtual host URL.
        /// </summary>
        private async Task TellMapToLoadAsync()
        {
            if (!_mapReady) return;
            var url    = $"http://{VirtualHost}/ne_10m_admin_0_countries.geojson";
            var script = $"window.loadGeoJSONFromUrl({JsonConvert.SerializeObject(url)});";
            await Dispatcher.InvokeAsync(async () =>
            {
                try { await MapWebView.CoreWebView2.ExecuteScriptAsync(script); }
                catch (Exception ex) {
                    var st = new StackTrace(true);
                    var frame = st.GetFrame(0);

                    using (StreamWriter sw = File.AppendText(Path.Combine(TopoService.AppDataDir, "debug.log"))) {
                        sw.WriteLine($"The error from line {frame.GetFileLineNumber()} of {frame.GetFileName()}: {ex.Message}");
                        sw.WriteLine(ex.ToString());
                    }

                    _vm.StatusText = "Map load error: " + ex.Message;
                }
            });
        }

        // ── Country sync C# → JS ──────────────────────────────────────────────
        private async Task PushCountryMetaAsync()
        {
            if (!_mapReady) return;

            var payload = _vm.AllCountries.Select(c => new
            {
                code       = c.Code,
                name       = c.Name,
                isSelected = c.IsSelected
            });

            var json   = JsonConvert.SerializeObject(payload);
            // updateCountries receives a JSON-encoded string (double-encoded)
            // so the JS side does JSON.parse() on it safely.
            var script = $"window.updateCountries({JsonConvert.SerializeObject(json)});";

            await Dispatcher.InvokeAsync(async () =>
            {
                try { await MapWebView.CoreWebView2.ExecuteScriptAsync(script); }
                catch { }
            });
        }

        private async void OnSelectionChanged(IEnumerable<CountryItem> _)
            => await PushCountryMetaAsync();

        // ── Dark mode ─────────────────────────────────────────────────────────
        private async void OnDarkModeChanged(bool enabled)
        {
            var bc    = new System.Windows.Media.BrushConverter();
            var bg    = enabled ? "#1A2030" : "#F5F5F5";
            var panel = enabled ? "#1E2B3A" : "White";
            var bord  = enabled ? "#2A3A50" : "#E0E0E0";

            Background            = (System.Windows.Media.Brush)bc.ConvertFromString(bg)!;
            LeftPanel.Background  = (System.Windows.Media.Brush)bc.ConvertFromString(panel)!;
            LeftPanel.BorderBrush = (System.Windows.Media.Brush)bc.ConvertFromString(bord)!;

            if (_mapReady)
            {
                try
                {
                    await MapWebView.CoreWebView2.ExecuteScriptAsync(
                        $"window.setDarkMode({(enabled ? "true" : "false")});");
                }
                catch { }
            }
        }

        // ── Export ────────────────────────────────────────────────────────────
        private async Task OnExportRequested(IEnumerable<CountryItem> selected)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName   = "globe",
                DefaultExt = ".json",
                Filter     = "JSON (*.json)|*.json|TopoJSON (*.topojson)|*.topojson"
            };
            if (dlg.ShowDialog() != true) return;

            _vm.IsLoading  = true;
            _vm.StatusText = "Exporting…";
            var exportProgress = new Progress<string>(msg =>
                Dispatcher.Invoke(() => _vm.StatusText = msg));
            try
            {
                await _topoService.ExportTopoJsonAsync(
                    selected.Select(c => c.Code), dlg.FileName, exportProgress);
                _vm.StatusText =
                    $"Exported {selected.Count()} countries → " +
                    $"{Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                var st = new StackTrace(true);
                var frame = st.GetFrame(0);

                using (StreamWriter sw = File.AppendText(Path.Combine(TopoService.AppDataDir, "debug.log"))) {
                    sw.WriteLine($"The error from line {frame.GetFileLineNumber()} of {frame.GetFileName()}: {ex.Message}");
                    sw.WriteLine(ex.ToString());
                }

                MessageBox.Show($"Export failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                _vm.StatusText = "Export failed.";
            }
            finally { _vm.IsLoading = false; }
        }

        // ── JS → WPF messages ─────────────────────────────────────────────────
        private void WebView_MessageReceived(
            object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                dynamic? msg = JsonConvert.DeserializeObject<dynamic>(e.WebMessageAsJson);
                if (msg == null) return;
                string type = msg.type?.ToString() ?? "";
                if (type == "countryToggled")
                {
                    string code = msg.code?.ToString() ?? "";
                    Dispatcher.Invoke(() => _vm.ToggleCountryByCode(code));
                }
            }
            catch { }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void UpdateMapStatus(string msg) =>
            Dispatcher.Invoke(() => MapStatusText.Text = msg);

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            _vm.SearchText = string.Empty;
        }
    }
}
