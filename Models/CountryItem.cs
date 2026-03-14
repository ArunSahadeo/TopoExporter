using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TopoExporter.Models
{
    public class CountryItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// ISO 3166-1 alpha-3 or custom code used to match TopoJSON features.
        /// </summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// Numeric ID matching world-110m.json feature IDs (where applicable).
        /// </summary>
        public int? NumericId { get; set; }

        /// <summary>
        /// True for non-sovereign or special territories not in standard TopoJSON.
        /// </summary>
        public bool IsTerritory { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public override string ToString() => Name;
    }
}
