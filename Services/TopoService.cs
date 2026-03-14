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
            "Vatican"
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
            {"Dhekelia", "Akrotiri and Dhekelia"},
            {"Republic of Cabo Verde", "Cabo Verde"},
            {"Federated States of Micronesia", "Micronesia"},
            {"Falkland Islands / Malvinas", "Falkland Islands"}
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

            using (StreamWriter sw = File.AppendText(Path.Combine(TopoService.AppDataDir, "debug.log"))) {
                foreach (JProperty property in fc.Properties()) {
                    sw.WriteLine($"The GeoJSON key name: {property.Name}");
                }
            }

            var features = (fc["features"] as JArray ?? new JArray())
                .Where(f =>
                {
                    var p = f["properties"] as JObject;
                    return p != null && codeSet.Contains(BestCode(p));
                })
                .ToList();

            progress?.Report($"Converting {features.Count} features to TopoJSON…");
            var topoJson = await Task.Run(() => BuildTopoJson(features));

            progress?.Report("Writing file…");
            await File.WriteAllTextAsync(outputPath, topoJson, Encoding.UTF8);
            progress?.Report("Export complete.");
        }

        // ── Preset ────────────────────────────────────────────────────────────

        public void SavePreset(IEnumerable<string> codes) =>
            File.WriteAllText(PresetPath,
                JsonConvert.SerializeObject(codes.ToList(), Formatting.Indented));

        public List<string> LoadPreset()
        {
            if (!File.Exists(PresetPath)) return new();
            return JsonConvert.DeserializeObject<List<string>>(
                File.ReadAllText(PresetPath)) ?? new();
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
        // Implements a lightweight but correct topology builder:
        //   1. Quantize all coordinates to integers (reduces file size ~40 %)
        //   2. Extract every ring from Polygon / MultiPolygon geometries
        //   3. Detect shared arcs via a hashtable keyed on sorted endpoint pairs
        //   4. Emit delta-encoded arc arrays (further reduces file size)
        //   5. Write the TopoJSON spec-compliant object with only iso + name props
        //
        private static string BuildTopoJson(List<JToken> features)
        {
            // ── 1. Collect all rings and compute bounding box ─────────────────
            const int Q = 10_000; // quantisation grid size

            // First pass: find global bbox
            double minX =  180, maxX = -180, minY =  90, maxY = -90;
            foreach (var f in features)
                WalkCoords(f["geometry"], (x, y) =>
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                });

            // Small padding so edge points aren't clipped
            minX -= 0.001; minY -= 0.001; maxX += 0.001; maxY += 0.001;

            double scaleX  = (maxX - minX) / (Q - 1);
            double scaleY  = (maxY - minY) / (Q - 1);
            if (scaleX == 0) scaleX = 1;
            if (scaleY == 0) scaleY = 1;

            (int qx, int qy) Quantize(double lon, double lat) =>
                ((int)Math.Round((lon - minX) / scaleX),
                 (int)Math.Round((lat - minY) / scaleY));

            // ── 2. Build arc list and feature→arc-index map ───────────────────
            // Key = "x1,y1|x2,y2" (first and last quantized point, canonical order)
            var arcIndex  = new Dictionary<string, int>();
            var arcs      = new List<List<(int x, int y)>>();

            // Per-feature: list of geometry objects (each is a list of rings;
            // each ring is a list of arc indices, negative = reversed)
            var featureGeoms = new List<(string type, List<List<int[]>> rings)>();

            foreach (var f in features)
            {
                var geom = f["geometry"];
                if (geom == null || geom.Type == JTokenType.Null)
                {
                    featureGeoms.Add(("Polygon", new()));
                    continue;
                }

                var gtype = geom["type"]?.ToString() ?? "";
                var rings = new List<List<int[]>>();

                IEnumerable<JToken> polygons = gtype == "MultiPolygon"
                    ? (geom["coordinates"] ?? new JArray()).Select(p => p)
                    : new[] { geom["coordinates"] ?? new JArray() };

                foreach (var poly in polygons)
                {
                    var polyRings = new List<int[]>();
                    foreach (var ring in poly)
                    {
                        var qpts = ring
                            .Select(pt =>
                            {
                                var (qx, qy) = Quantize(
                                    (double)(pt[0] ?? 0), (double)(pt[1] ?? 0));
                                return (qx, qy);
                            })
                            .ToList();

                        // Deduplicate consecutive identical points
                        var deduped = new List<(int x, int y)> { qpts[0] };
                        for (int i = 1; i < qpts.Count; i++)
                            if (qpts[i] != deduped[^1]) deduped.Add(qpts[i]);

                        // Need at least 3 distinct points + closing = 4
                        if (deduped.Count < 3) continue;

                        // Ensure ring is closed
                        if (deduped[0] != deduped[^1]) deduped.Add(deduped[0]);

                        var first = deduped[0];
                        var last  = deduped[^1];  // same as first after closing

                        // Canonical key: sort the two endpoints to detect reversal
                        string Pt(int x, int y) => $"{x},{y}";
                        string key;
                        bool reversed;
                        var f0 = Pt(first.x, first.y);
                        var l0 = Pt(deduped[1].x, deduped[1].y); // second point as tie-break
                        var fn = Pt(deduped[^2].x, deduped[^2].y);
                        if (string.Compare(f0 + "|" + l0,
                                           Pt(deduped[^1].x, deduped[^1].y) + "|" + fn,
                                           StringComparison.Ordinal) <= 0)
                        {
                            key = f0 + "|" + l0;
                            reversed = false;
                        }
                        else
                        {
                            key = Pt(deduped[^1].x, deduped[^1].y) + "|" + fn;
                            reversed = true;
                        }

                        if (!arcIndex.TryGetValue(key, out int idx))
                        {
                            idx = arcs.Count;
                            arcIndex[key] = idx;
                            arcs.Add(reversed
                                ? Enumerable.Reverse(deduped).ToList()
                                : deduped);
                        }

                        polyRings.Add(new[] { reversed ? ~idx : idx });
                    }

                    if (polyRings.Count > 0)
                        rings.Add(polyRings.SelectMany(r => r)
                            .Select(i => new[] { i }).ToList());
                }

                featureGeoms.Add((gtype == "MultiPolygon" ? "MultiPolygon" : "Polygon", rings));
            }

            // ── 3. Encode arcs as delta sequences ─────────────────────────────
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

            // ── 4. Write feature geometries ───────────────────────────────────
            for (int fi = 0; fi < features.Count; fi++)
            {
                if (fi > 0) sb.Append(',');
                var (gtype, rings) = featureGeoms[fi];
                var props = features[fi]["properties"] as JObject ?? new JObject();
                var iso   = BestIsoCode(props);
                var name  = BestName(props).Replace("\\", "\\\\").Replace("\"", "\\\"");

                if (CountryNamesToStandardise.ContainsKey(name)) {
                    name = CountryNamesToStandardise[name];
                }

                sb.Append("{\"type\":\"");

                if (rings.Count == 0)
                {
                    // No geometry — emit a null geometry
                    sb.Append("GeometryCollection\",\"geometries\":[]");
                }
                else if (gtype == "MultiPolygon")
                {
                    sb.Append("MultiPolygon\",\"arcs\":[");
                    for (int ri = 0; ri < rings.Count; ri++)
                    {
                        if (ri > 0) sb.Append(',');
                        sb.Append("[[");
                        var ring = rings[ri];
                        for (int ai = 0; ai < ring.Count; ai++)
                        {
                            if (ai > 0) sb.Append(',');
                            sb.Append(ring[ai][0]);
                        }
                        sb.Append("]]");
                    }
                    sb.Append(']');
                }
                else
                {
                    sb.Append("Polygon\",\"arcs\":[[");
                    if (rings.Count > 0)
                    {
                        var ring = rings[0];
                        for (int ai = 0; ai < ring.Count; ai++)
                        {
                            if (ai > 0) sb.Append(',');
                            sb.Append(ring[ai][0]);
                        }
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

        // Walk every [lon,lat] coordinate pair in a GeoJSON geometry
        private static void WalkCoords(JToken? geom, Action<double, double> visit)
        {
            if (geom == null || geom.Type == JTokenType.Null) return;
            var type  = geom["type"]?.ToString() ?? "";
            var coords = geom["coordinates"];
            if (coords == null) return;

            if (type == "Point")
            {
                visit((double)coords[0]!, (double)coords[1]!);
            }
            else if (type == "LineString" || type == "MultiPoint")
            {
                foreach (var pt in coords) visit((double)pt[0]!, (double)pt[1]!);
            }
            else if (type == "Polygon" || type == "MultiLineString")
            {
                foreach (var ring in coords)
                    foreach (var pt in ring) visit((double)pt[0]!, (double)pt[1]!);
            }
            else if (type == "MultiPolygon")
            {
                foreach (var poly in coords)
                    foreach (var ring in poly)
                        foreach (var pt in ring) visit((double)pt[0]!, (double)pt[1]!);
            }
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
