#:project ../../src/HarScope.Core/HarScope.Core.csproj
#:property PublishAot=false

// Synthetic large-HAR benchmark for HarScope.Core.
// Usage: dotnet run scripts/bench/Bench.cs -- <outputDir> [targetMB=150]
// Writes <outputDir>/bench-<MB>mb.har, then measures load time, memory and typical queries.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HarScope.Core;
using HarScope.Core.Analysis;
using HarScope.Core.Indexing;
using HarScope.Core.Querying;
using HarScope.Core.Security;

var dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "harscope-bench");
var targetMb = args.Length > 1 ? int.Parse(args[1]) : 150;
Directory.CreateDirectory(dir);
var file = Path.Combine(dir, $"bench-{targetMb}mb.har");

if (!File.Exists(file) || new FileInfo(file).Length < targetMb * 1024L * 1024 * 0.95)
{
    var sw0 = Stopwatch.StartNew();
    Generate(file, targetMb * 1024L * 1024);
    Console.WriteLine($"generated {file} ({new FileInfo(file).Length / 1048576.0:F1} MB) in {sw0.Elapsed.TotalSeconds:F1}s");
}

var options = new HarScopeOptions { RootPath = dir, MaxFileBytes = 2L * 1024 * 1024 * 1024 };
var redactor = new Redactor(options.Redaction);
var store = new CaptureStore(options);
var cache = new JsonBodyCache();

GC.Collect();
var before = GC.GetTotalMemory(true);
var proc = Process.GetCurrentProcess();
proc.Refresh();
var wsBefore = proc.WorkingSet64;

var sw = Stopwatch.StartNew();
var handle = store.Open(Path.GetFileName(file));
var loadMs = sw.ElapsedMilliseconds;

GC.Collect();
var after = GC.GetTotalMemory(true);
proc.Refresh();
var fileMb = new FileInfo(file).Length / 1048576.0;
Console.WriteLine($"entries            : {handle.Capture.Entries.Count:N0}");
Console.WriteLine($"load (read+parse+index): {loadMs:N0} ms");
Console.WriteLine($"managed heap delta : {(after - before) / 1048576.0:F1} MB  ({(after - before) / 1048576.0 / fileMb:F2}x file)");
Console.WriteLine($"working set delta  : {(proc.WorkingSet64 - wsBefore) / 1048576.0:F1} MB");

Time("overview", () => new OverviewService(redactor).Build(handle));
var search = new SearchService(options, redactor);
Time("search failedOnly", () => search.Search(handle, new SearchQuery { FailedOnly = true }));
Time("search pathContains", () => search.Search(handle, new SearchQuery { PathContains = "/api/orders" }));
Time("search bodyContains (all bodies)", () => search.Search(handle, new SearchQuery { BodyContains = "NEEDLE-404" }));
Time("trace flow", () => new FlowService(options, redactor).Trace(handle, new FlowQuery()));

var big = handle.Capture.Entries.First(e => e.HasResponseBody);
var bq = new BodyQueryService(options, cache);
Time("query_body cold (parse)", () => bq.Query(handle, big, new BodyQuery { Path = "$.data[*]", Aggregate = BodyAggregate.Count }));
Time("query_body warm (cache)", () => bq.Query(handle, big, new BodyQuery { Path = "$.data[*].amount", Aggregate = BodyAggregate.Sum }));
Time("query_body where+fields", () => bq.Query(handle, big, new BodyQuery { Path = "$.data[*]", Where = ["amount > 900"], Fields = ["id"] }));

var twin = handle.Capture.Entries.Where(e => e.HasResponseBody).Skip(1).First();
Time("compare_entries", () => new CompareService(options, redactor, cache).CompareEntries(handle, big, handle, twin));

void Time(string name, Func<object> action)
{
    var t = Stopwatch.StartNew();
    var result = action();
    var json = HarJson.Serialize(result);
    Console.WriteLine($"{name,-34}: {t.ElapsedMilliseconds,6:N0} ms  (output {json.Length:N0} chars)");
}

static void Generate(string path, long targetBytes)
{
    using var stream = File.Create(path);
    using var w = new Utf8JsonWriter(stream);
    var rnd = new Random(42);
    var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    w.WriteStartObject();
    w.WriteStartObject("log");
    w.WriteString("version", "1.2");
    w.WriteStartObject("creator"); w.WriteString("name", "bench"); w.WriteString("version", "1"); w.WriteEndObject();
    w.WriteStartArray("entries");

    var i = 0;
    while (stream.Position + w.BytesPending < targetBytes)
    {
        i++;
        var status = i % 97 == 0 ? 500 : i % 53 == 0 ? 404 : 200;
        var path2 = (i % 4) switch { 0 => "/api/orders", 1 => "/api/products", 2 => "/api/customers", _ => "/static/app.js" };
        var started = start.AddMilliseconds(i * 150 + (i % 10 == 0 ? 2000 : 0));

        w.WriteStartObject();
        w.WriteString("_resourceType", path2.StartsWith("/static") ? "script" : "fetch");
        w.WriteString("startedDateTime", started.ToString("O"));
        w.WriteNumber("time", rnd.Next(5, 1500));
        w.WriteStartObject("request");
        w.WriteString("method", i % 5 == 0 ? "POST" : "GET");
        w.WriteString("url", $"https://api.bench.test{path2}?page={i % 20}");
        w.WriteString("httpVersion", "h2");
        w.WriteStartArray("headers");
        Header(w, "accept", "application/json");
        Header(w, "authorization", "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig");
        Header(w, "x-request-id", $"req-{i}");
        w.WriteEndArray();
        w.WriteStartArray("queryString"); w.WriteEndArray();
        w.WriteStartArray("cookies"); w.WriteEndArray();
        w.WriteNumber("headersSize", -1); w.WriteNumber("bodySize", 0);
        w.WriteEndObject();

        w.WriteStartObject("response");
        w.WriteNumber("status", status);
        w.WriteString("statusText", "");
        w.WriteString("httpVersion", "h2");
        w.WriteStartArray("headers"); Header(w, "content-type", "application/json"); w.WriteEndArray();
        w.WriteStartArray("cookies"); w.WriteEndArray();
        w.WriteStartObject("content");
        var body = Body(rnd, i, rows: 60 + rnd.Next(0, 120));
        w.WriteNumber("size", body.Length);
        w.WriteString("mimeType", "application/json");
        w.WriteString("text", body);
        w.WriteEndObject();
        w.WriteString("redirectURL", "");
        w.WriteNumber("headersSize", -1); w.WriteNumber("bodySize", -1);
        w.WriteEndObject();
        w.WriteStartObject("cache"); w.WriteEndObject();
        w.WriteStartObject("timings"); w.WriteNumber("send", 0); w.WriteNumber("wait", 10); w.WriteNumber("receive", 1); w.WriteEndObject();
        w.WriteEndObject();

        if (i % 200 == 0) w.Flush();
    }

    w.WriteEndArray();
    w.WriteEndObject();
    w.WriteEndObject();
}

static void Header(Utf8JsonWriter w, string name, string value)
{
    w.WriteStartObject(); w.WriteString("name", name); w.WriteString("value", value); w.WriteEndObject();
}

static string Body(Random rnd, int i, int rows)
{
    var sb = new StringBuilder("{\"data\":[");
    for (var r = 0; r < rows; r++)
    {
        if (r > 0) sb.Append(',');
        sb.Append("{\"id\":").Append(i * 1000 + r)
          .Append(",\"name\":\"customer-").Append(rnd.Next(100000)).Append('"')
          .Append(",\"amount\":").Append(rnd.Next(1, 1000))
          .Append(",\"status\":\"").Append(r % 7 == 0 ? "FAILED" : "OK").Append("\"}");
    }
    sb.Append("],\"errors\":null}");
    return sb.ToString();
}
