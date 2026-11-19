using System.Text;
using Quizzler.Cli.Commands;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("quizzler");

    config.AddCommand<QuizCommand>("quiz")
        .WithDescription("Answer the questions that are due now, in the terminal.");
    config.AddCommand<StatsCommand>("stats")
        .WithDescription("Show progress: what's due, what's learned, by topic.");
    config.AddCommand<ValidateCommand>("validate")
        .WithDescription("Check content/ for errors and run every predict-output snippet to verify answers.");
    config.AddCommand<ImportCommand>("import")
        .WithDescription("Validate content/ and import it into the database. Progress is kept.");
    config.AddCommand<FlagsCommand>("flags")
        .WithDescription("List questions you flagged as wrong or unclear while studying.");
    config.AddCommand<SettingsCommand>("settings")
        .WithDescription("Show or change study settings (new questions per day, day rollover hour).");
});

return await app.RunAsync(args);
