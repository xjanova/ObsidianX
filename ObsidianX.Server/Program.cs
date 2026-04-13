using ObsidianX.Server.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ASCII art banner
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine(@"
   ____  _         _     _ _             __  __
  / __ \| |__  ___(_) __| (_) __ _ _ __ \ \/ /
 | |  | | '_ \/ __| |/ _` | |/ _` | '_ \ \  /
 | |__| | |_) \__ \ | (_| | | (_| | | | |/  \
  \____/|_.__/|___/_|\__,_|_|\__,_|_| |_/_/\_\

  ╔══════════════════════════════════════════════╗
  ║   ObsidianX Server — Brain Matchmaking Hub   ║
  ║   Neural Knowledge Network v1.0.0             ║
  ╚══════════════════════════════════════════════╝
");
Console.ResetColor();

// REST API endpoints
app.MapGet("/api/health", () => new
{
    Status = "Healthy",
    Timestamp = DateTime.UtcNow,
    Uptime = Environment.TickCount64 / 1000
});

app.MapGet("/api/peers", () =>
{
    return BrainHub.GetPeersSnapshot();
});

app.MapGet("/api/stats", () =>
{
    return BrainHub.GetStatsSnapshot();
});

// SignalR hub for real-time brain connections
app.MapHub<BrainHub>("/brain-hub");

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("  [OK] Server ready at http://localhost:5142");
Console.WriteLine("  [OK] SignalR hub at http://localhost:5142/brain-hub");
Console.WriteLine("  [OK] Waiting for brains to connect...\n");
Console.ResetColor();

app.Run("http://0.0.0.0:5142");
