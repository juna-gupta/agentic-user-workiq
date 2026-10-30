using IncidentAgent.Hosting;
using IncidentAgent.Teams;
using Microsoft.Teams.Apps;

var builder = WebApplication.CreateBuilder(args);

// Teams SDK: validates incoming requests with the blueprint's Entra app (the "AzureAd" section) and gets tokens for
// the agent identity and its agent user.
builder.Services.AddTeamsBotApplication();
builder.Services.AddIncidentAgent(builder.Configuration);
builder.Services.AddHealthChecks();

var app = builder.Build();

// POST /api/messages: the notification URL set on the agent blueprint in the Teams Developer Portal.
var teams = app.UseTeamsBotApplication();
teams.UseMiddleware(new KeepReplyToId());
app.Services.GetRequiredService<AgentHandlers>().Map(teams);

app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
