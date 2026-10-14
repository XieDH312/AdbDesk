using System.Diagnostics;
using AdbDesk.Core;

if (args.Length == 2)
{
    RunChild(args[0], args[1]);
    return;
}

var checks = new (string Name, Func<Task> Run)[]
{
    ("secondary processes cannot own gate and notify the first process", SecondaryAsync),
    ("closing owner permits a new primary instance", ReacquireAsync),
    ("crashed owner does not permanently lock the app", CrashAsync),
    ("missing owner notification returns within bounded timeout", MissingOwnerAsync)
};
var failed = 0;
foreach (var check in checks)
{
    try { await check.Run(); Console.WriteLine($"PASS {check.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {check.Name}: {ex.Message}"); }
}
Console.WriteLine($"RESULT {checks.Length - failed}/{checks.Length} passed");
Environment.ExitCode = failed == 0 ? 0 : 1;

static void RunChild(string mode, string name)
{
    // Named mutex ownership is thread-affine: acquire and dispose on this same thread.
    using var owner = SingleInstanceGate.TryAcquire(name, () => Console.WriteLine("ACTIVATE"));
    if (owner is null)
    {
        if (!SingleInstanceGate.NotifyAsync(name, allowForeground: pid => Console.WriteLine($"GRANT:{pid}"))
            .GetAwaiter().GetResult()) Environment.ExitCode = 3;
        Console.WriteLine("SECONDARY");
        return;
    }
    Console.WriteLine("PRIMARY");
    if (mode == "hold") Console.ReadLine();
}

static async Task SecondaryAsync()
{
    var name = "AdbDesk-test-" + Guid.NewGuid().ToString("N");
    using var owner = Launch("hold", name);
    try
    {
        Equal("PRIMARY", await LineAsync(owner), "first instance owns mutex");
        var probes = Enumerable.Range(0, 4).Select(_ => Launch("probe", name)).ToArray();
        try
        {
            foreach (var probe in probes)
            {
                Equal($"GRANT:{owner.Id}", await LineAsync(probe), "foreground permission targets actual owner");
                Equal("SECONDARY", await LineAsync(probe), "second launch excluded");
                await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                Equal(0, probe.ExitCode, "activation message sent");
            }
            foreach (var _ in probes) Equal("ACTIVATE", await LineAsync(owner), "owner receives notification");
        }
        finally { foreach (var probe in probes) { Kill(probe); probe.Dispose(); } }
    }
    finally { Kill(owner); }
}

static async Task ReacquireAsync()
{
    var name = "AdbDesk-test-" + Guid.NewGuid().ToString("N");
    using var owner = Launch("hold", name);
    try
    {
        Equal("PRIMARY", await LineAsync(owner), "first acquisition");
        await owner.StandardInput.WriteLineAsync("exit");
        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Equal(0, owner.ExitCode, "owner shuts down cleanly on owning thread");
        using var next = Launch("probe", name);
        try { Equal("PRIMARY", await LineAsync(next), "new launch can own gate"); await next.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); Equal(0, next.ExitCode, "next exits cleanly"); }
        finally { Kill(next); }
    }
    finally { Kill(owner); }
}

static async Task CrashAsync()
{
    var name = "AdbDesk-test-" + Guid.NewGuid().ToString("N");
    using var owner = Launch("hold", name);
    try { Equal("PRIMARY", await LineAsync(owner), "owner started"); }
    finally { Kill(owner); }
    using var next = Launch("probe", name);
    try { Equal("PRIMARY", await LineAsync(next), "crash does not leave lock"); }
    finally { Kill(next); }
}

static async Task MissingOwnerAsync()
{
    var elapsed = Stopwatch.StartNew();
    Equal(false, await SingleInstanceGate.NotifyAsync("AdbDesk-missing-" + Guid.NewGuid()), "absent owner");
    if (elapsed.Elapsed > TimeSpan.FromSeconds(6)) throw new Exception("notification timeout exceeded");
}

static Process Launch(string mode, string name)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add(mode); start.ArgumentList.Add(name);
    return Process.Start(start) ?? throw new Exception("Could not start fixture process");
}
static async Task<string> LineAsync(Process process)
{
    var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
    if (line is not null) return line;
    var error = await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
    throw new Exception($"Fixture closed without output: {error}");
}
static void Kill(Process process)
{
    if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
}
static void Equal<T>(T expected, T actual, string label)
{
    if (!Equals(expected, actual)) throw new Exception($"{label}: expected {expected}, got {actual}");
}
