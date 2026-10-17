// Spec M3 step 10: load test of POST /api/stations/{id}/scan, the way the line uses it.
// Creates and releases a work order of N boards on Line 1, opens the 5 station sessions, then W workers in parallel
// each walk their boards through SMT -> ASSY -> TEST -> PACK; 2 % fail at TEST and go through Repair
// (check-in + repair) and are retested. Prints p50/p95/p99 per step after a warm-up.
// Run against a perf database (seed-scanlog.sql first), API in Release:
//   dotnet run scripts/scan-bench.cs -- [baseUrl] [boards] [workers]
//   dotnet run scripts/scan-bench.cs -- http://localhost:5157 2000 8
#:project ../src/MiniMes.Contracts/MiniMes.Contracts.csproj
// File-based apps default to Native AOT, which turns off reflection JSON; a dev tool does not need AOT.
#:property PublishAot=false
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MiniMes.Contracts.Auth;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Planning;

var baseUrl = args.ElementAtOrDefault(0) ?? "http://localhost:5157";
var boards = int.Parse(args.ElementAtOrDefault(1) ?? "2000");
var workers = int.Parse(args.ElementAtOrDefault(2) ?? "8");
const int WarmUp = 100;          // first requests: JIT, EF model, connection pool; not counted
const string Password = "Demo@12345";

async Task<HttpClient> LoginAsync(string url, object body)
{
    var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    var login = await (await http.PostAsJsonAsync(url, body)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<LoginResponse>();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
    return http;
}

// ---------- set-up: a released WO without FAI (FAI would hold every board but one) ----------
var planner = await LoginAsync("api/auth/login", new LoginRequest("planner01", Password));
var item = (await planner.GetFromJsonAsync<PagedResult<ItemDto>>("api/items?search=PCB-A&pageSize=10"))!.Items.Single(i => i.Code == "PCB-A");
var start = DateTime.UtcNow.Date.AddDays(1);
var draft = await (await planner.PostAsJsonAsync("api/work-orders",
    new CreateWorkOrderRequest(item.Id, 1, boards, start, start.AddHours(8), RequireFai: false))).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<WorkOrderDetailDto>();
var wo = await (await planner.PostAsJsonAsync($"api/work-orders/{draft!.Id}/release",
    new WorkOrderTransitionRequest(draft.RowVersion))).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<WorkOrderDetailDto>();
var serials = new List<string>();
for (var page = 1; serials.Count < boards; page++)
    serials.AddRange((await planner.GetFromJsonAsync<PagedResult<SerialDto>>($"api/work-orders/{wo!.Id}/serials?page={page}&pageSize=500"))!.Items.Select(s => s.SerialNo));

var op = await LoginAsync("api/auth/login-operator", new OperatorLoginRequest("EMP00123", "L1-SMT-01"));
var stations = (await op.GetFromJsonAsync<List<StationDto>>("api/stations"))!.ToDictionary(s => s.Code, s => s.Id);
foreach (var code in new[] { "L1-SMT-01", "L1-ASM-01", "L1-TEST-01", "L1-PACK-01", "L1-REPAIR" })
    (await op.PostAsJsonAsync($"api/stations/{stations[code]}/sessions", new OpenSessionRequest(wo!.Id))).EnsureSuccessStatusCode();
Console.WriteLine($"{wo!.WoNo}: {serials.Count} boards, {workers} workers, {baseUrl}");

// ---------- load ----------
var samples = new ConcurrentQueue<(long Order, string Step, double Ms)>();
var unexpected = new ConcurrentQueue<string>();
long order = 0;

async Task<T> TimedAsync<T>(string step, Func<Task<HttpResponseMessage>> call)
{
    var watch = Stopwatch.StartNew();
    using var response = await call();
    watch.Stop();
    samples.Enqueue((Interlocked.Increment(ref order), step, watch.Elapsed.TotalMilliseconds));
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{step}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    return (await response.Content.ReadFromJsonAsync<T>())!;
}

Task<ScanResponse> ScanAsync(string step, string code, string serial, string result = "OK", string? defect = null)
    => TimedAsync<ScanResponse>(step, () => op.PostAsJsonAsync($"api/stations/{stations[code]}/scan", new ScanRequest(serial, result, defect)));

var total = Stopwatch.StartNew();
await Parallel.ForEachAsync(serials.Select((s, i) => (Serial: s, Index: i)), new ParallelOptions { MaxDegreeOfParallelism = workers }, async (b, _) =>
{
    void Expect(ScanResponse r, string want, string at) { if (r.Result != want) unexpected.Enqueue($"{b.Serial} {at}: {r.Result} {r.ReasonCode}"); }
    Expect(await ScanAsync("SMT", "L1-SMT-01", b.Serial), "Ok", "SMT");
    Expect(await ScanAsync("ASSY", "L1-ASM-01", b.Serial), "Ok", "ASSY");
    if (b.Index % 50 == 0)   // 2 % NG: Repair, then the test again
    {
        Expect(await ScanAsync("TEST", "L1-TEST-01", b.Serial, "NG", "SLD-BRG"), "Ng", "TEST NG");
        Expect(await ScanAsync("REPAIR check-in", "L1-REPAIR", b.Serial), "Ok", "REPAIR");
        await TimedAsync<RepairResponse>("REPAIR record", () => op.PostAsJsonAsync($"api/stations/{stations["L1-REPAIR"]}/repair",
            new RepairRequest(b.Serial, "SLD-BRG", "Reflow solder joint")));
    }
    Expect(await ScanAsync("TEST", "L1-TEST-01", b.Serial), "Ok", "TEST");
    Expect(await ScanAsync("PACK", "L1-PACK-01", b.Serial), "Ok", "PACK");
});
total.Stop();

// ---------- report ----------
var counted = samples.Where(s => s.Order > WarmUp).ToList();
static double P(List<double> sorted, double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1)];
void Line(string name, IEnumerable<double> values)
{
    var v = values.OrderBy(x => x).ToList();
    if (v.Count == 0) return;
    Console.WriteLine($"| {name,-16} | {v.Count,6} | {P(v, .50),7:F1} | {P(v, .95),7:F1} | {P(v, .99),7:F1} | {v[^1],7:F1} |");
}
Console.WriteLine($"{samples.Count} requests in {total.Elapsed.TotalSeconds:F1} s = {samples.Count / total.Elapsed.TotalSeconds:F0} req/s; first {WarmUp} not counted");
Console.WriteLine("| Step             |      n |  p50 ms |  p95 ms |  p99 ms |  max ms |");
Console.WriteLine("| ---------------- | -----: | ------: | ------: | ------: | ------: |");
Line("all", counted.Select(s => s.Ms));
foreach (var step in counted.Select(s => s.Step).Distinct()) Line(step, counted.Where(s => s.Step == step).Select(s => s.Ms));
Console.WriteLine(unexpected.IsEmpty ? "All answers as expected." : $"{unexpected.Count} unexpected answers, e.g. {string.Join("; ", unexpected.Take(5))}");
