using Microsoft.Extensions.Options;
using Stamp.Application;
using Stamp.Infrastructure;
using Stamp.Infrastructure.Persistence;
using Stamp.Web.Infrastructure;

// `dotnet run -- expire-stamps` and `dotnet run -- migrate` run once and exit (see Cli).
var command = Cli.CommandIn(args);

var builder = WebApplication.CreateBuilder(command is null ? args : args[1..]);

builder.Services.AddStampWeb(builder.Configuration, builder.Environment);
builder.Services.AddStampApplication();
builder.Services.AddStampInfrastructure(builder.Configuration, builder.Environment);

var app = builder.Build();

if (command is not null)
{
    return await Cli.RunAsync(app, command);
}

if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.AutoMigrate)
{
    await app.Services.MigrateStampDatabaseAsync();
}

app.UseStampWeb();
await app.RunAsync();
return 0;

public partial class Program;
