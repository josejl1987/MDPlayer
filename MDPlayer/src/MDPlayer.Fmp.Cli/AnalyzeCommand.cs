namespace Fmp.Cli;

internal static class AnalyzeCommand
{
    public static int Handle(string[] args)
    {
        AnalyzeOptions options;
        try { options = AnalyzeOptionsParser.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 2; }
        try { return AnalysisRunner.Run(options); }
        catch (TrackPreparationException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return ex.ExitCode; }
        catch (InvalidOperationException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 4; }
        catch (Exception ex) { Console.Error.WriteLine($"error: analysis failed — {ex.Message}"); return 7; }
    }
}
