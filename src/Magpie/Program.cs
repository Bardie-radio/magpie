using Bardie.Module.Hosting;
using Bardie.Module.Source;
using Bardie.Module.Channel.Participant;
using Magpie.Features.Source;
using Magpie.Infrastructure.Media;
using Microsoft.Extensions.Options;
#if DEBUG
using Bardie.Module.Source.Debug;
#endif

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables();

var manifest = builder.AddBardieModuleHosting(
    configure: options =>
    {
        options.ServerDnsNames = ["magpie", "localhost"];
        options.ExpectedHostClientIdentity = "kithara";
    },
    otelFallbackServiceName: "bardie.source.magpie");

builder.Services.AddSourceModuleDefaults(builder.Configuration);
#if DEBUG
builder.Services.AddSourceModuleDevFixtures(builder.Configuration);
#endif
builder.Services.PostConfigure<SourceModuleOptions>(options =>
{
    var max = builder.Configuration["MAGPIE_MAX_PARALLEL_JOBS"]
        ?? builder.Configuration["SourceModule:MaxParallelJobs"];
    if (int.TryParse(max, out var n))
    {
        options.MaxParallelJobs = n;
    }
});

builder.Services.Configure<MagpieOptions>(builder.Configuration.GetSection(MagpieOptions.SectionName));
builder.Services.PostConfigure<MagpieOptions>(options =>
{
    var ffmpegRoot = builder.Configuration["MAGPIE_FFMPEG_ROOT"];
    if (!string.IsNullOrWhiteSpace(ffmpegRoot))
    {
        options.FfmpegRootPath = ffmpegRoot;
    }
});

builder.Services.AddSingleton<IYouTubeCatalog, YoutubeExplodeCatalog>();
builder.Services.AddSingleton<IPcmTranscoder, FfmpegPcmTranscoder>();
builder.Services.AddSingleton<TrackPlaybackService>();
builder.Services.AddGrpc();

var app = builder.Build();

await app.EnsureModuleParticipantServerCertificateAsync().ConfigureAwait(false);

var participantOptions = app.Services.GetRequiredService<IOptions<ModuleParticipantOptions>>().Value;
var httpPort = ModuleHostingPorts.ResolveHttpPort(builder.Configuration);

app.Logger.LogInformation(
    "Magpie starting as {Slug} ({Otel}); health HTTP :{HttpPort}; work gRPC :{Port}; host={Host}",
    manifest.Slug,
    manifest.OtelServiceName,
    httpPort,
    participantOptions.WorkGrpcPort,
    participantOptions.HostGrpcAddress);

app.MapGrpcService<SourceModuleService>();
app.MapModuleHostingEndpoints();

app.Run();

public partial class Program;
