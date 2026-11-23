using Playzek.Client.Core;
using Playzek.ProtectionService;

// PLAYZEK Protection Service — rulează ca serviciu Windows (cont LocalService, privilegii minime).
// Poate rula și ca aplicație de consolă pentru dezvoltare: dotnet run.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "PlayzekProtectionService");

// Instalatorul scrie adresa backend-ului în HKLM\SOFTWARE\PLAYZEK\AntiCheat (proprietatea MSI BACKENDURL);
// are prioritate față de appsettings.json, ca o actualizare să nu suprascrie configurația.
var registryUrl = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PLAYZEK\AntiCheat")?.GetValue("BackendUrl") as string;
var backendUrl = new Uri(registryUrl ?? builder.Configuration["Playzek:BackendUrl"]
                         ?? throw new InvalidOperationException("Playzek:BackendUrl lipsește din appsettings.json."));
BackendClient.EnsureSecureEndpoint(backendUrl);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<BackendClient>(c =>
{
    c.BaseAddress = backendUrl;
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd($"PLAYZEK-AntiCheat/{ProtectionEngine.CurrentVersion()}");
});
builder.Services.AddSingleton<ITokenStore>(_ => new DpapiTokenStore(
    builder.Configuration["Playzek:TokenStorePath"] ?? DpapiTokenStore.DefaultPath()));
builder.Services.AddSingleton<ISystemChecks, WindowsSystemChecks>();
builder.Services.AddSingleton<IDeviceIdentity, WindowsDeviceIdentity>();
builder.Services.AddSingleton(sp => new ProtectionEngine(
    sp.GetRequiredService<BackendClient>(), sp.GetRequiredService<ITokenStore>(),
    sp.GetRequiredService<ISystemChecks>(), sp.GetRequiredService<IDeviceIdentity>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();
