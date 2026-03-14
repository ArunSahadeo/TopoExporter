# TopoExporter

A WPF desktop application for selecting countries and exporting filtered GeoJSON world maps,
built from **Natural Earth 1:10m Admin-0 Countries** data — which includes 258 admin units:
full sovereign states **plus** all territories, SARs, dependencies, and disputed areas such as:
Hong Kong SAR, Macau SAR, Palestinian Authority, Western Sahara, Greenland, Puerto Rico,
Guam, Bermuda, Kosovo, Taiwan, etc.

## Requirements

| Requirement | Notes |
|---|---|
| **Windows 10/11** (x64) | |
| **.NET 8 SDK** | https://dotnet.microsoft.com/download/dotnet/8.0 |
| **Visual Studio 2022** | Community or higher, *.NET Desktop Development* workload |
| **WebView2 Runtime** | Ships with Windows 11; download for Win 10 from Microsoft |

## Getting Started

1. Open `TopoExporter.sln` in Visual Studio 2022.
2. NuGet packages restore automatically on first build.
3. Press **F5**.

On first launch the app downloads the Natural Earth shapefile (~5 MB) from:

```
https://www.naturalearthdata.com/.../ne_10m_admin_0_countries.zip
```

It then converts the `.shp` to GeoJSON and caches it at:

```
%APPDATA%\TopoExporter\ne_10m_admin_0_countries.geojson
```

Subsequent launches use the cache — no internet needed.

## Features

| Feature | Description |
|---|---|
| Country list | 185+ countries & territories with checkboxes |
| Pagination | 10 items per page with live search |
| Add All / Clear All | Works on the current filtered set |
| Interactive map | D3.js rendered in WebView2 (zoom, pan, hover, click) |
| Click-to-select | Click any country directly on the map |
| Territory detail | Hong Kong SAR, Macau SAR, Palestinian Authority, etc. rendered separately |
| Export GeoJSON | Save a filtered `.geojson` containing only selected countries |
| Save / Load Preset | Persists selections to `%APPDATA%\TopoExporter\preset.json` |
| Dark mode | Toggles both WPF pane and D3 map |

## Project Structure

```
TopoExporter/
├── Assets/
│   └── map.html              # D3.js interactive map (GeoJSON rendering)
├── Models/
│   └── CountryItem.cs        # Country data model
├── Services/
│   └── TopoService.cs        # Download, shapefile→GeoJSON conversion, export
├── ViewModels/
│   └── MainViewModel.cs      # MVVM view model
├── MainWindow.xaml           # Two-pane UI
├── MainWindow.xaml.cs        # WebView2 bridge
├── App.xaml / App.xaml.cs    # Styles
└── TopoExporter.csproj       # .NET 8 WPF project
```

## NuGet Packages

| Package | Purpose |
|---|---|
| `Microsoft.Web.WebView2` | Chromium-based browser pane |
| `Newtonsoft.Json` | JSON serialisation |
| `NetTopologySuite` | Geometry engine |
| `NetTopologySuite.IO.ShapeFile` | Reads `.shp`/`.dbf` without GDAL |

## Data Source

**Natural Earth** 1:10m Admin-0 Countries v5.1.1 — public domain.  
https://www.naturalearthdata.com/downloads/10m-cultural-vectors/
