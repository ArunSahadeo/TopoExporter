using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http;
using System.Text;

namespace TopoExporter.Services
{
    public class TopoService
    {
        public static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TopoExporter");

        public static readonly string GeoJsonPath =
            Path.Combine(AppDataDir, "ne_10m_admin_0_countries.geojson");

        public static readonly string PresetPath =
            Path.Combine(AppDataDir, "preset.json");

        private const string GeoJsonUrl =
            "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_10m_admin_0_countries.geojson";

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

        public TopoService() => Directory.CreateDirectory(AppDataDir);

        // ── Download ──────────────────────────────────────────────────────────

        public async Task EnsureGeoJsonExistsAsync(IProgress<string>? progress = null)
        {
            if (File.Exists(GeoJsonPath))
            {
                if (await IsValidGeoJsonAsync(GeoJsonPath))
                {
                    progress?.Report("Map data ready.");
                    return;
                }
                progress?.Report("Cached file is invalid — re-downloading…");
                File.Delete(GeoJsonPath);
            }

            progress?.Report("Downloading Natural Earth 10m GeoJSON (~25 MB)…");
            var tmp = GeoJsonPath + ".tmp";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
            using var response = await client.GetAsync(GeoJsonUrl,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var fs     = File.Create(tmp);
            await using var stream = await response.Content.ReadAsStreamAsync();

            using var streamReader = new StreamReader(stream);
            using var jsonReader = new JsonTextReader(streamReader);

            var serializer = new JsonSerializer();
            var rawJson = serializer.Deserialize<JObject>(jsonReader);

            rawJson["features"] = new JArray(
                rawJson["features"].Where(x => x["properties"]?["POP_EST"]?.ToObject<int>() > 0 && x["properties"]?["TYPE"]?.ToObject<string>() != "Lease" && !CountryNamesToExclude.Contains(x["properties"]?["NAME"]?.ToObject<string>()))
            );

            using (var sw = new StreamWriter(fs))
            using (var jw = new JsonTextWriter(sw))
            {
                jw.Formatting = Formatting.Indented;
                await rawJson.WriteToAsync(jw);
            }

            fs.Close();
            File.Move(tmp, GeoJsonPath, overwrite: true);
            progress?.Report("Map data ready.");
        }

        // ── Country list ──────────────────────────────────────────────────────

        public async Task<List<CountryEntry>> ExtractCountriesAsync()
        {
            var raw    = await File.ReadAllTextAsync(GeoJsonPath);
            var fc     = JObject.Parse(raw);
            var result = new List<CountryEntry>();
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var feature in fc["features"] ?? new JArray())
            {
                var props = feature["properties"] as JObject;
                if (props == null) continue;
                var code = BestCode(props);
                if (string.IsNullOrWhiteSpace(code) || !seen.Add(code)) continue;
                result.Add(new CountryEntry
                {
                    Code      = code,
                    IsoCode   = BestIsoCode(props),
                    Name      = BestName(props),
                    Sovereign = props["SOVEREIGNT"]?.ToString()?.Trim() ?? "",
                    Type      = props["TYPE"]?.ToString()?.Trim() ?? ""
                });
            }

            result.Sort((a, b) =>
                string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        // ── TopoJSON export ───────────────────────────────────────────────────

        /// <summary>
        /// Exports selected countries as a compact TopoJSON file.
        /// Properties kept: "iso" (shortest ISO code) and "name" only.
        /// </summary>
        public async Task ExportTopoJsonAsync(
            IEnumerable<string> selectedCodes, string outputPath,
            IProgress<string>? progress = null)
        {
            progress?.Report("Reading source GeoJSON…");
            var codeSet  = new HashSet<string>(selectedCodes,
                StringComparer.OrdinalIgnoreCase);
            var raw      = await File.ReadAllTextAsync(GeoJsonPath);
            var fc       = JObject.Parse(raw);

            // Group features by code so split countries (e.g. Ecuador mainland +
            // Galápagos) are merged into one geometry rather than the second row
            // being silently dropped.
            var featuresByCode = (fc["features"] as JArray ?? new JArray())
                .Where(f =>
                {
                    var p = f["properties"] as JObject;
                    return p != null && codeSet.Contains(BestCode(p));
                })
                .GroupBy(f => BestCode(f["properties"] as JObject ?? new JObject()),
                         StringComparer.OrdinalIgnoreCase)
                .ToList();

            progress?.Report($"Converting {featuresByCode.Count} countries to TopoJSON…");
            var topoJson = await Task.Run(() => BuildTopoJson(featuresByCode));

            progress?.Report("Writing file…");
            await File.WriteAllTextAsync(outputPath, topoJson, Encoding.UTF8);
            progress?.Report("Export complete.");
        }

        // ── Preset ────────────────────────────────────────────────────────────

        public void SavePreset(IEnumerable<string> codes) {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName   = "preset",
                DefaultExt = ".json",
                InitialDirectory = AppDataDir,
                Filter     = "JSON (*.json)|*.json"
            };

            if (dlg.ShowDialog() != true) return;

            File.WriteAllText(dlg.FileName,
                JsonConvert.SerializeObject(codes.ToList(), Formatting.Indented));
        }

        public List<string> LoadPreset()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                FileName   = "preset",
                DefaultExt = ".json",
                InitialDirectory = AppDataDir,
                Filter     = "JSON (*.json)|*.json"
            };

            if (dlg.ShowDialog() != true) return new();

            return JsonConvert.DeserializeObject<List<string>>(
                File.ReadAllText(dlg.FileName)) ?? new();
        }

        // ── Property helpers ──────────────────────────────────────────────────

        public static string BestCode(JObject props)
        {
            foreach (var key in new[] { "ADM0_A3", "ISO_A3", "GU_A3", "ISO_A3_EH" })
            {
                var v = props[key]?.ToString()?.Replace("\0", "").Trim();
                if (!string.IsNullOrEmpty(v) && v != "-99" && v != "-1") return v;
            }
            return string.Empty;
        }

        /// <summary>Returns ISO_A2 when valid (2 letters), otherwise ISO_A3.</summary>
        public static string BestIsoCode(JObject props)
        {
            var a2 = props["ISO_A2"]?.ToString()?.Replace("\0", "").Trim();
            if (!string.IsNullOrEmpty(a2) && a2 != "-99" && a2 != "-1" && a2.Length == 2)
                return a2;
            return BestCode(props);
        }

        public static string BestName(JObject props)
        {
            foreach (var key in new[] { "NAME_LONG", "GEOUNIT", "NAME", "ADMIN",
                                        "SOVEREIGNT", "NAME_SORT" })
            {
                var v = props[key]?.ToString()?.Replace("\0", "").Trim();

                if (!string.IsNullOrEmpty(v) && v != "-99") {
                    return v;
                };
            }
            return "Unknown";
        }

        private static async Task<bool> IsValidGeoJsonAsync(string path)
        {
            try
            {
                var buf  = new byte[128];
                await using var fs = File.OpenRead(path);
                var read = await fs.ReadAsync(buf);
                return Encoding.UTF8.GetString(buf, 0, read).Contains("FeatureCollection");
            }
            catch { return false; }
        }

        // ── GeoJSON → TopoJSON conversion ─────────────────────────────────────
        //
        // Each ring is stored as its own arc (no arc deduplication). This avoids
        // the hash-collision bug where rings from different countries that happen
        // to share a canonical endpoint key get merged into a single arc, causing
        // one country's geometry to bleed into another country's entry.
        //
        // Arc deduplication is an optional TopoJSON optimisation; Power BI and
        // every other TopoJSON consumer work correctly without it.
        //
        private static string BuildTopoJson(List<IGrouping<string, JToken>> featureGroups)
        {
            const int Q = 50_000; // quantisation grid size — high enough for small territories like HK and Singapore to render smoothly when zoomed

            // Always use the full world extent so Power BI's Mercator projection
            // can correctly position countries on the globe.
            const double minX = -180.0;
            const double maxX =  180.0;
            const double minY =  -90.0;
            const double maxY =   90.0;

            double scaleX = (maxX - minX) / (Q - 1);
            double scaleY = (maxY - minY) / (Q - 1);

            (int qx, int qy) Quantize(double lon, double lat) =>
                ((int)Math.Round((lon - minX) / scaleX),
                 (int)Math.Round((lat - minY) / scaleY));

            // ── 1. Extract rings as lon/lat float lists, merging split features ─
            // Natural Earth splits some countries across multiple rows with the same
            // ADM0_A3 code (e.g. Ecuador mainland + Galápagos). We merge all rows
            // sharing a code into one geometry so nothing is lost.
            //
            // Each entry: (gtype, polys, repFeature) where repFeature supplies metadata.
            var featureRings = new List<(string gtype, List<List<List<(double lon, double lat)>>> polys, JToken repFeature)>();

            foreach (var group in featureGroups)
            {
                var polys = new List<List<List<(double, double)>>>();

                foreach (var f in group)
                {
                    var geom = f["geometry"];
                    if (geom == null || geom.Type == JTokenType.Null) continue;

                    var gtype = geom["type"]?.ToString() ?? "";

                    IEnumerable<JToken> polygons = gtype == "MultiPolygon"
                        ? (geom["coordinates"] ?? new JArray()).Select(p => p)
                        : new[] { geom["coordinates"] ?? new JArray() };

                    foreach (var poly in polygons)
                    {
                        var rings = new List<List<(double, double)>>();
                        foreach (var ring in poly)
                        {
                            var pts = ring
                                .Select(pt => ((double)(pt[0] ?? 0), (double)(pt[1] ?? 0)))
                                .ToList();
                            if (pts.Count >= 3) rings.Add(pts);
                        }
                        if (rings.Count > 0) polys.Add(rings);
                    }
                }

                featureRings.Add((polys.Count > 1 ? "MultiPolygon" : "Polygon", polys, group.First()));
            }

            // ── 2. Inflate small features in lon/lat space ────────────────────
            // A shape map canvas is roughly 800×500 px for a full-world view.
            // Anything spanning less than ~6 px in either axis is invisible.
            // We inflate the raw coordinates (before quantization) so the shape
            // stays geometrically faithful — no jagged integer-scaling artefacts.
            //
            // Min visible span: 6 px → 6×(360/800) = 2.7° lon, 6×(180/500) = 2.16° lat
            const double minLonSpan = 6.0 * 360.0 / 800.0;  // ~2.7°
            const double minLatSpan = 6.0 * 180.0 / 500.0;  // ~2.16°

            foreach (var (_, polys, _) in featureRings)
            {
                if (polys.Count == 0) continue;

                var allPts = polys.SelectMany(p => p).SelectMany(r => r).ToList();
                double mnLon = allPts.Min(p => p.lon);
                double mxLon = allPts.Max(p => p.lon);
                double mnLat = allPts.Min(p => p.lat);
                double mxLat = allPts.Max(p => p.lat);

                double lonSpan = mxLon - mnLon;
                double latSpan = mxLat - mnLat;

                if (lonSpan >= minLonSpan && latSpan >= minLatSpan) continue;

                // Scale uniformly so the smaller axis meets its minimum
                double factor = Math.Max(
                    lonSpan > 0 ? minLonSpan / lonSpan : minLonSpan,
                    latSpan > 0 ? minLatSpan / latSpan : minLatSpan);

                double cx = (mnLon + mxLon) / 2.0;
                double cy = (mnLat + mxLat) / 2.0;

                foreach (var poly in polys)
                    foreach (var ring in poly)
                        for (int k = 0; k < ring.Count; k++)
                            ring[k] = (cx + (ring[k].lon - cx) * factor,
                                       cy + (ring[k].lat - cy) * factor);
            }

            // ── 3. Quantize and build arc list ────────────────────────────────
            var arcs         = new List<List<(int x, int y)>>();
            var featureGeoms = new List<(string gtype, List<List<int>> polyArcLists)>();

            for (int fi = 0; fi < featureRings.Count; fi++)
            {
                var (gtype, polys, _) = featureRings[fi];
                var polyArcLists = new List<List<int>>();

                foreach (var poly in polys)
                {
                    var arcIndicesForPoly = new List<int>();
                    foreach (var ring in poly)
                    {
                        var qpts = ring.Select(pt => Quantize(pt.lon, pt.lat)).ToList();

                        // Remove consecutive duplicates introduced by quantization
                        var deduped = new List<(int x, int y)> { qpts[0] };
                        for (int i = 1; i < qpts.Count; i++)
                            if (qpts[i] != deduped[^1]) deduped.Add(qpts[i]);

                        if (deduped.Count < 3) continue;

                        // Ensure ring is closed
                        if (deduped[0] != deduped[^1]) deduped.Add(deduped[0]);

                        arcIndicesForPoly.Add(arcs.Count);
                        arcs.Add(deduped);
                    }
                    if (arcIndicesForPoly.Count > 0)
                        polyArcLists.Add(arcIndicesForPoly);
                }
                featureGeoms.Add((gtype, polyArcLists));
            }

            // ── 4. Encode arcs as delta sequences and serialise ───────────────
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"Topology\",");
            sb.Append("\"transform\":{");
            sb.Append($"\"scale\":[{scaleX.ToString("R")},{scaleY.ToString("R")}],");
            sb.Append($"\"translate\":[{minX.ToString("R")},{minY.ToString("R")}]");
            sb.Append("},");
            sb.Append("\"arcs\":[");

            for (int a = 0; a < arcs.Count; a++)
            {
                if (a > 0) sb.Append(',');
                var pts = arcs[a];
                sb.Append('[');
                int px = 0, py = 0;
                for (int p = 0; p < pts.Count; p++)
                {
                    if (p > 0) sb.Append(',');
                    int dx = pts[p].x - px;
                    int dy = pts[p].y - py;
                    sb.Append('['); sb.Append(dx); sb.Append(','); sb.Append(dy); sb.Append(']');
                    px = pts[p].x; py = pts[p].y;
                }
                sb.Append(']');
            }

            sb.Append("],\"objects\":{\"countries\":{\"type\":\"GeometryCollection\",\"geometries\":[");

            // ── 5. Write feature geometries ───────────────────────────────────
            for (int fi = 0; fi < featureRings.Count; fi++)
            {
                if (fi > 0) sb.Append(',');
                var (gtype, polyArcLists) = featureGeoms[fi];
                var props = featureRings[fi].repFeature["properties"] as JObject ?? new JObject();
                var iso   = BestIsoCode(props);
                var name  = BestName(props).Replace("\\", "\\\\").Replace("\"", "\\\"");

                if (CountryNamesToStandardise.TryGetValue(name, out var standardName))
                    name = standardName;

                sb.Append("{\"type\":\"");

                if (polyArcLists.Count == 0)
                {
                    sb.Append("GeometryCollection\",\"geometries\":[]");
                }
                else if (gtype == "MultiPolygon")
                {
                    sb.Append("MultiPolygon\",\"arcs\":[");
                    for (int pi = 0; pi < polyArcLists.Count; pi++)
                    {
                        if (pi > 0) sb.Append(',');
                        sb.Append("[[");
                        var arcIndices = polyArcLists[pi];
                        for (int ai = 0; ai < arcIndices.Count; ai++)
                        {
                            if (ai > 0) sb.Append(',');
                            sb.Append(arcIndices[ai]);
                        }
                        sb.Append("]]");
                    }
                    sb.Append(']');
                }
                else
                {
                    sb.Append("Polygon\",\"arcs\":[[");
                    var arcIndices = polyArcLists[0];
                    for (int ai = 0; ai < arcIndices.Count; ai++)
                    {
                        if (ai > 0) sb.Append(',');
                        sb.Append(arcIndices[ai]);
                    }
                    sb.Append("]]");
                }

                sb.Append(",\"properties\":{");
                sb.Append($"\"iso\":\"{iso}\",");
                sb.Append($"\"name\":\"{name}\"");
                sb.Append("}}");
            }

            sb.Append("]}}}");
            return sb.ToString();
        }


    }

    public record CountryEntry
    {
        public string Code      { get; init; } = "";
        public string IsoCode   { get; init; } = "";
        public string Name      { get; init; } = "";
        public string Sovereign { get; init; } = "";
        public string Type      { get; init; } = "";
    }
}
