using LegionGoFanControl.Core;
using LegionGoFanControl.Tray;
using LegionGoFanControl.Hardware;

var tests = new (string, Func<Task>)[]
{
    ("SSD safety and cooldown", SsdSafety), ("NVMe response parsing", NvmeParsing), ("threshold and continuous cooldown", Threshold), ("oscillation resets cooldown", Oscillation),
    ("sensor failures never release", SensorFailure), ("hung sensor stops access", HungSensor), ("failed write and readback", WriteFailures),
    ("external startup and interference", Ownership), ("prewrite ownership race", OwnershipRace), ("shutdown and resume", Lifecycle),
    ("manual and firmware modes", Modes), ("validation", Validation), ("serialized access", Concurrent), ("bounded logging", Logging), ("missing startup task lookup", MissingStartupTask), ("startup task identity", StartupIdentity), ("Full Speed operation", HardwareWrite)
};
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { Console.Error.WriteLine($"FAIL {name}: {ex}"); Environment.ExitCode = 1; }
}

static Task HardwareWrite()
{
    bool state = false; int deviceChecks = 0, writes = 0;
    void CheckDevice() => deviceChecks++;
    bool Read() => state;
    void Write(bool value) { writes++; state = value; }
    Check(FullSpeedOperation.Set(true, CancellationToken.None, CheckDevice, Read, Write) == FanWriteResult.Changed && state && writes == 1 && deviceChecks == 1, "enable and verify");
    Check(FullSpeedOperation.Set(true, CancellationToken.None, CheckDevice, Read, Write) == FanWriteResult.AlreadyInRequestedState && writes == 1 && deviceChecks == 2, "already enabled");
    try { FullSpeedOperation.Set(false, CancellationToken.None, CheckDevice, Read, _ => { }); throw new Exception("missing mismatch error"); } catch (InvalidOperationException) { }
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { FullSpeedOperation.Set(false, cancelled.Token, CheckDevice, Read, Write); throw new Exception("missing cancellation"); } catch (OperationCanceledException) { }
    Check(state && writes == 1, "cancel prevents write");
    try { FullSpeedOperation.Set(false, CancellationToken.None, () => throw new InvalidOperationException("unsupported"), Read, Write); throw new Exception("missing device error"); } catch (InvalidOperationException) { }
    Check(state && writes == 1, "device gate prevents write");
    return Task.CompletedTask;
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
static async Task SsdSafety()
{
    var (c,h,t) = Setup(); h.Ssd=70; await c.TickAsync(); Check(h.On, "hot SSD activates");
    h.Ssd=50; await c.TickAsync(); for (int i=0;i<15;i++) { t.Advance(2); await c.TickAsync(); } Check(!h.On, "both cool release");
    h.Temp=80; h.SsdError="unavailable"; await c.TickAsync(); Check(h.On && c.Snapshot.SsdError is not null, "CPU activates despite SSD error");
    h.Temp=60; t.Advance(40); await c.TickAsync(); Check(h.On, "missing SSD cannot release");
    h.SsdError=null; h.Ssd=double.NaN; await c.TickAsync(); Check(h.On, "invalid SSD cannot release");
    h.Ssd=50; h.SsdStale=true; await c.TickAsync(); Check(h.On, "stale SSD cannot release");
    var (c2,h2,_)=Setup(true); h2.Ssd=75; await c2.TickAsync(); Check(h2.On && h2.Writes==0, "already enabled and hot SSD retained");
    var (c3,h3,_3)=Setup(); h3.CpuError="CPU unavailable"; h3.Ssd=75; await c3.TickAsync(); Check(h3.On, "SSD activates despite CPU error");
    await c3.SuspendAsync(); Check(h3.On, "hot SSD prevents suspend release");
    var (c4,h4,_4)=Setup(); h4.Ssd=75; await c4.TickAsync(); await c4.SetModeAsync(ControllerMode.Firmware); await c4.ShutdownAsync(); Check(h4.On, "hot SSD prevents firmware and shutdown release");
    var (c5,h5,t5)=Setup(); h5.Ssd=75; await c5.TickAsync(); h5.Ssd=50; t5.Advance(2); await c5.TickAsync(); for (int i=0;i<10;i++) { t5.Advance(2); await c5.TickAsync(); } h5.SsdError="missing"; t5.Advance(2); await c5.TickAsync(); h5.SsdError=null; t5.Advance(2); await c5.TickAsync(); for (int i=0;i<14;i++) { t5.Advance(2); await c5.TickAsync(); } Check(h5.On, "SSD error resets cooldown"); t5.Advance(2); await c5.TickAsync(); Check(!h5.On, "full cooldown after recovery");
    var options=System.Text.Json.JsonSerializer.Deserialize<ControllerOptions>("{\"OnTemperature\":80,\"OffTemperature\":70}")!; Check(options.SsdOnTemperature==70 && options.SsdOffTemperature==60, "old settings defaults");
    try { new ControllerOptions { SsdOffTemperature=70 }.Validate(); throw new Exception("accepted equal SSD thresholds"); } catch (ArgumentOutOfRangeException) { }
}
static Task NvmeParsing()
{
    var data=new byte[560]; var b=data.AsSpan(); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b,48); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[4..],48); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[8..],3); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[12..],2); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[24..],40); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[28..],512); System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b[49..],323);
    Check(Math.Abs(SystemSsdTemperature.ParseComposite(data)-49.85)<0.01, "composite conversion");
    b[49]=0; b[50]=0; try { SystemSsdTemperature.ParseComposite(data); throw new Exception("accepted missing temperature"); } catch (InvalidOperationException) { }
    try { SystemSsdTemperature.ParseComposite(data.AsSpan(0,50)); throw new Exception("accepted truncated data"); } catch (InvalidOperationException) { }
    return Task.CompletedTask;
}
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
    var(c,h,t)=Setup(true); h.Temp=65; await c.TickAsync(); Check(c.Snapshot.OwnsFullSpeed && h.Writes==0,"adopt startup on without write");
    for(int i=0;i<14;i++){t.Advance(2);await c.TickAsync();} Check(h.On,"startup on remains during cooldown"); t.Advance(2); await c.TickAsync(); Check(!h.On && h.Writes==1,"startup on releases after continuous cooldown");
    var(hot,hh,ht)=Setup(true); hh.Temp=90; await hot.TickAsync(); for(int i=0;i<20;i++){ht.Advance(2);await hot.TickAsync();} Check(hh.On && hh.Writes==0,"hot startup retained");
    hh.Temp=70; ht.Advance(2); await hot.TickAsync(); for(int i=0;i<10;i++){ht.Advance(2);await hot.TickAsync();} hh.Temp=75; ht.Advance(2); await hot.TickAsync(); hh.Temp=70; ht.Advance(2); await hot.TickAsync(); for(int i=0;i<14;i++){ht.Advance(2);await hot.TickAsync();} Check(hh.On,"hysteresis resets startup cooldown"); ht.Advance(2); await hot.TickAsync(); Check(!hh.On,"release after restarted cooldown");
    var(external,eh,et)=Setup(); await external.TickAsync(); eh.On=true; await external.TickAsync(); Check(external.Snapshot.OwnsFullSpeed,"adopt externally enabled state"); for(int i=0;i<15;i++){et.Advance(2);await external.TickAsync();} Check(!eh.On,"release observed external state after cooldown");
    var(failed,fh,ft)=Setup(true); fh.FailTemp=true; await failed.TickAsync(); ft.Advance(40); await failed.TickAsync(); await failed.ShutdownAsync(); Check(fh.On && fh.Writes==0,"sensor failure retains adopted startup state");
    var(c2,h2,_)=Setup();h2.Temp=90;await c2.TickAsync();h2.On=false;await c2.TickAsync();await c2.TickAsync();Check(h2.Writes==1 && c2.Snapshot.State==ControllerState.Interference,"external change");
}
static async Task OwnershipRace()
{
    var(c,h,t)=Setup(); h.Temp=90; h.RaceOn=true; await c.TickAsync();
    Check(h.On && c.Snapshot.OwnsFullSpeed && c.Snapshot.State==ControllerState.FullSpeed,"automatic prewrite race adopted");
    h.RaceOn=false; h.Temp=70; t.Advance(2); await c.TickAsync(); for(int i=0;i<15;i++){t.Advance(2);await c.TickAsync();} Check(!h.On,"race state released after cooldown");
}
static async Task Lifecycle()
{
    var(c,h,_)=Setup();h.Temp=90;await c.TickAsync();await c.ShutdownAsync();Check(h.On && h.Writes==1,"hot shutdown retains fan");
    var(c2,h2,_2)=Setup();h2.Temp=90;await c2.TickAsync();await c2.SuspendAsync();await c2.ResumeAsync();h2.Temp=60;await c2.TickAsync();await c2.ShutdownAsync();Check(!h2.On,"resume adopts retained full speed and shutdown releases with fresh telemetry");
    var(disabled,dh,_d)=Setup(true); dh.Temp=90; await disabled.TickAsync(); await disabled.SetModeAsync(ControllerMode.Firmware); await disabled.ShutdownAsync(); Check(dh.On && dh.Writes==0,"hot disable and shutdown retain fan");
    var(c3,h3,_3)=Setup();h3.Temp=90;await c3.TickAsync();h3.Temp=60;await c3.TickAsync();await c3.SuspendAsync();Check(!h3.On && h3.Writes==2,"cool suspend releases owned fan");await c3.ResumeAsync();Check(!h3.On,"resume preserves firmware control");
}
static async Task Modes()
{
    var(c,h,_)=Setup();h.FailTemp=true;await c.SetModeAsync(ControllerMode.ManualFullSpeed);Check(h.On,"manual without sensor");await c.SetModeAsync(ControllerMode.Firmware);Check(h.On,"firmware retains without safe telemetry");
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
    public double Temp=65, Ssd=50; public string? SsdError, CpuError; public bool SsdStale, On, FailTemp, FailWrite, Disagree, Stale, Hang, RaceOn; public int Writes, FanReads;
    public Task<TemperatureSample> ReadTemperatureAsync(CancellationToken ct) => Hang ? new TaskCompletionSource<TemperatureSample>().Task : FailTemp ? Task.FromException<TemperatureSample>(new Exception("sensor failure")) : Task.FromResult(new TemperatureSample(Temp, clock.GetUtcNow() - (Stale ? TimeSpan.FromSeconds(20) : TimeSpan.Zero), Ssd, clock.GetUtcNow() - (SsdStale ? TimeSpan.FromSeconds(20) : TimeSpan.Zero), SsdError, CpuError));
    public Task<bool> ReadFullSpeedAsync(CancellationToken ct) { FanReads++; return Task.FromResult(On); }
    public Task<FanWriteResult> SetFullSpeedAsync(bool enabled,CancellationToken ct) { Writes++; if(FailWrite) throw new Exception("write failed"); if(RaceOn) { On=enabled; return Task.FromResult(FanWriteResult.AlreadyInRequestedState); } if(!Disagree) On=enabled; return Task.FromResult(FanWriteResult.Changed); }
}
