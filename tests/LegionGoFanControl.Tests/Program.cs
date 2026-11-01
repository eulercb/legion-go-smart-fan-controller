using LegionGoFanControl.Core;
using LegionGoFanControl.Tray;

var tests = new (string, Func<Task>)[]
{
    ("threshold and continuous cooldown", Threshold), ("oscillation resets cooldown", Oscillation),
    ("sensor failures never release", SensorFailure), ("hung sensor stops access", HungSensor), ("failed write and readback", WriteFailures),
    ("external startup and interference", Ownership), ("prewrite ownership race", OwnershipRace), ("shutdown and resume", Lifecycle),
    ("manual and firmware modes", Modes), ("validation", Validation), ("serialized access", Concurrent), ("bounded logging", Logging), ("missing startup task lookup", MissingStartupTask), ("startup task identity", StartupIdentity)
};
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { Console.Error.WriteLine($"FAIL {name}: {ex}"); Environment.ExitCode = 1; }
}

static Task StartupIdentity()
{
    const string sid = "S-1-5-21-1";
    bool Owned(string? marker, string? principal, int logon, int count, string? path, Func<string, string?> resolve) => StartupTaskOwnership.IsOwned(marker, principal, logon, count, path, sid, resolve);
    var marker = StartupTaskOwnership.Marker;
    string? Resolve(string p) => p == "MACHINE\\user" ? sid : p;
    Check(Owned(marker, sid, 3, 1, "LegionGoFanControl.Tray.exe", Resolve), "SID identity");
    Check(Owned(marker, "MACHINE\\user", 3, 1, "LegionGoFanControl.Tray.exe", Resolve), "account identity");
    Check(!Owned("foreign", "MACHINE\\user", 3, 1, "LegionGoFanControl.Tray.exe", Resolve), "foreign marker");
    Check(!Owned(marker, "MACHINE\\user", 3, 1, "other.exe", Resolve), "foreign action");
    Check(!Owned(marker, "MACHINE\\other", 3, 1, "LegionGoFanControl.Tray.exe", Resolve), "foreign principal");
    Check(!Owned(marker, null, 3, 1, "LegionGoFanControl.Tray.exe", Resolve), "missing principal");
    Check(!Owned(marker, sid, 2, 1, "LegionGoFanControl.Tray.exe", Resolve), "wrong logon type");
    Check(!Owned(marker, sid, 3, 2, "LegionGoFanControl.Tray.exe", Resolve), "multiple actions");
    Check(!Owned(marker, "unknown", 3, 1, "LegionGoFanControl.Tray.exe", _ => throw new InvalidOperationException()), "unresolved principal");
    return Task.CompletedTask;
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static (FanController c, Fake h, Clock clock) Setup(bool initial = false)
{
    var clock = new Clock(); var h = new Fake(clock) { On = initial }; return (new FanController(h, timeProvider: clock), h, clock);
}
static async Task Threshold()
{
    var (c,h,t)=Setup(); h.Temp=80; await c.TickAsync(); Check(h.On && h.Writes==1 && c.Snapshot.OwnsFullSpeed,"activate");
    await c.TickAsync(); Check(h.Writes==1,"avoid repeated writes"); h.Temp=70; t.Advance(2); await c.TickAsync();
    for(int i=0;i<15;i++) { t.Advance(2); await c.TickAsync(); }
    Check(!h.On && h.Writes==2,"release after 30 seconds");
}
static async Task Oscillation()
{
    var(c,h,t)=Setup(); h.Temp=80; await c.TickAsync(); h.Temp=70; t.Advance(2); await c.TickAsync();
    for(int i=0;i<10;i++){t.Advance(2);await c.TickAsync();} h.Temp=71;t.Advance(2);await c.TickAsync();
    h.Temp=70;t.Advance(2);await c.TickAsync(); for(int i=0;i<10;i++){t.Advance(2);await c.TickAsync();}
    Check(h.On,"must reset timer above off threshold");
    for(int i=0;i<5;i++){t.Advance(2);await c.TickAsync();} Check(!h.On,"release after uninterrupted cooling");
}
static async Task SensorFailure()
{
    var(c,h,t)=Setup(); h.Temp=80;await c.TickAsync();h.Temp=70;t.Advance(2);await c.TickAsync();
    h.Temp=double.NaN;t.Advance(2);await c.TickAsync();Check(h.On && c.Snapshot.State==ControllerState.Error,"invalid");
    h.Temp=70;h.Stale=true;t.Advance(30);await c.TickAsync();Check(h.On,"stale");h.Stale=false;h.FailTemp=true;await c.TickAsync();Check(h.On,"failed sensor");
    await c.ShutdownAsync(); Check(h.On, "shutdown must retain full speed after sensor failure");
}
static async Task HungSensor()
{
    var t=new Clock(); var h=new Fake(t) { Temp=90 };
    var c=new FanController(h,new ControllerOptions { PollInterval=TimeSpan.FromSeconds(1), MaximumSampleAge=TimeSpan.FromSeconds(1) },t);
    await c.TickAsync(); h.Hang=true; await c.TickAsync();
    Check(c.Snapshot.State==ControllerState.Error && h.On,"timeout retains fan");
    int reads=h.FanReads; await c.SetModeAsync(ControllerMode.Firmware); await c.SuspendAsync(); await c.ResumeAsync(); await c.ShutdownAsync();
    Check(h.On && h.Writes==1 && h.FanReads==reads,"timeout must prevent later hardware access");
}
static async Task WriteFailures()
{
    var(c,h,_)=Setup();h.Temp=90;h.FailWrite=true;await c.TickAsync();Check(c.Snapshot.State==ControllerState.Error && !c.Snapshot.OwnsFullSpeed,"failed write");
    h.FailWrite=false;await c.TickAsync();Check(h.Writes==1 && c.Snapshot.State==ControllerState.Interference,"no retry");
    var(c2,h2,_2)=Setup();h2.Temp=90;h2.Disagree=true;await c2.TickAsync();Check(c2.Snapshot.State==ControllerState.Error && !c2.Snapshot.OwnsFullSpeed,"readback mismatch");
}
static async Task Ownership()
{
    var(c,h,t)=Setup(true);h.Temp=65;await c.TickAsync();t.Advance(40);await c.TickAsync();await c.ShutdownAsync();Check(h.On && h.Writes==0,"preserve external on");
    var(c2,h2,_)=Setup();h2.Temp=90;await c2.TickAsync();h2.On=false;await c2.TickAsync();await c2.TickAsync();Check(h2.Writes==1 && c2.Snapshot.State==ControllerState.Interference,"external change");
}
static async Task OwnershipRace()
{
    var(c,h,_)=Setup(); h.Temp=90; h.RaceOn=true; await c.TickAsync();
    Check(h.On && !c.Snapshot.OwnsFullSpeed && c.Snapshot.State==ControllerState.ExternalFullSpeed,"prewrite race must not acquire ownership");
    await c.ShutdownAsync(); Check(h.On && h.Writes==1,"shutdown preserves externally enabled fan");
}
static async Task Lifecycle()
{
    var(c,h,_)=Setup();h.Temp=90;await c.TickAsync();await c.ShutdownAsync();Check(!h.On && h.Writes==2,"release owned on shutdown");
    var(c2,h2,_2)=Setup();h2.Temp=90;await c2.TickAsync();await c2.SuspendAsync();await c2.ResumeAsync();h2.Temp=60;await c2.TickAsync();await c2.ShutdownAsync();Check(h2.On,"hot suspend retains full speed");
    var(c3,h3,_3)=Setup();h3.Temp=90;await c3.TickAsync();h3.Temp=60;await c3.TickAsync();await c3.SuspendAsync();Check(!h3.On && h3.Writes==2,"cool suspend releases owned fan");await c3.ResumeAsync();Check(!h3.On,"resume preserves firmware control");
}
static async Task Modes()
{
    var(c,h,_)=Setup();h.FailTemp=true;await c.SetModeAsync(ControllerMode.ManualFullSpeed);Check(h.On,"manual without sensor");await c.SetModeAsync(ControllerMode.Firmware);Check(!h.On,"firmware releases owned");
}
static Task Validation()
{
    try { new ControllerOptions { OffTemperature=80 }.Validate(); throw new Exception("accepted equal thresholds"); } catch(ArgumentOutOfRangeException) { }
    try { new ControllerOptions { PollInterval=TimeSpan.Zero }.Validate(); throw new Exception("accepted zero interval"); } catch(ArgumentOutOfRangeException) { } return Task.CompletedTask;
}
static async Task Concurrent()
{
    var(c,h,_)=Setup();h.Temp=90;await Task.WhenAll(Enumerable.Range(0,10).Select(_=>c.TickAsync()));Check(h.Writes==1,"concurrent ticks must serialize");
}
static Task Logging()
{
    string dir = Path.Combine(Path.GetTempPath(), "legion-log-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        string path = Path.Combine(dir, "controller.log");
        File.WriteAllBytes(path, new byte[BoundedLog.MaxBytes + 1]);
        File.WriteAllBytes(path + ".1", new byte[BoundedLog.MaxBytes + 1]);
        var log = new BoundedLog(path); log.Write("first");
        Check(!File.Exists(path + ".1"), "oversized legacy backup removed");
        Check(new FileInfo(path).Length < BoundedLog.MaxBytes, "oversized legacy current removed");
        Parallel.For(0, 1000, i => log.Write($"entry {i} " + new string('é', 30000)));
        log.Write("latest marker");
        string[] files = Directory.GetFiles(dir);
        Check(files.Length == 2 && files.All(f => new FileInfo(f).Length <= BoundedLog.MaxBytes), "two bounded files after concurrent rotations");
        string tail = File.ReadAllText(path);
        Check(tail.Contains("[truncated]") && tail.Contains("latest marker"), "entries truncated and latest retained");
    }
    finally { Directory.Delete(dir, true); }
    return Task.CompletedTask;
}
static Task MissingStartupTask()
{
    Exception[] missing = [new FileNotFoundException(), new System.Runtime.InteropServices.COMException("missing", unchecked((int)0x80070002)), new System.ComponentModel.Win32Exception(2)];
    foreach (var ex in missing)
        Check(StartupTaskLookup.Find<object>(() => throw ex) is null, "missing task should be disabled");
    Exception[] errors = [new UnauthorizedAccessException(), new System.Runtime.InteropServices.COMException("access denied", unchecked((int)0x80070005)), new System.ComponentModel.Win32Exception(5), new InvalidOperationException("ownership")];
    foreach (var ex in errors)
    {
        try { StartupTaskLookup.Find<object>(() => throw ex); throw new Exception("unexpected error suppressed"); }
        catch (Exception caught) when (ReferenceEquals(caught, ex)) { }
    }
    var found = new object();
    Check(ReferenceEquals(found, StartupTaskLookup.Find(() => found)), "existing task returned");
    return Task.CompletedTask;
}
sealed class Clock : TimeProvider
{
    private long seconds; public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero).AddSeconds(seconds);
    public override long GetTimestamp()=>seconds; public override long TimestampFrequency=>1; public void Advance(int s)=>seconds+=s;
}
sealed class Fake(Clock clock) : IFanHardware
{
    public double Temp=65; public bool On, FailTemp, FailWrite, Disagree, Stale, Hang, RaceOn; public int Writes, FanReads;
    public Task<TemperatureSample> ReadTemperatureAsync(CancellationToken ct) => Hang ? new TaskCompletionSource<TemperatureSample>().Task : FailTemp ? Task.FromException<TemperatureSample>(new Exception("sensor failure")) : Task.FromResult(new TemperatureSample(Temp, clock.GetUtcNow() - (Stale ? TimeSpan.FromSeconds(20) : TimeSpan.Zero)));
    public Task<bool> ReadFullSpeedAsync(CancellationToken ct) { FanReads++; return Task.FromResult(On); }
    public Task<FanWriteResult> SetFullSpeedAsync(bool enabled,CancellationToken ct) { Writes++; if(FailWrite) throw new Exception("write failed"); if(RaceOn) { On=enabled; return Task.FromResult(FanWriteResult.AlreadyInRequestedState); } if(!Disagree) On=enabled; return Task.FromResult(FanWriteResult.Changed); }
}
