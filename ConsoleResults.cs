namespace Srndx;

/// <summary>Formats search results for the console, shared by the <c>search</c> and <c>serve</c> commands.</summary>
public static class ConsoleResults
{
    public static void Print(IReadOnlyList<(SearchRecord Record, float Score)> results)
    {
        if (results.Count == 0)
        {
            Console.WriteLine("No matches.");
            return;
        }

        foreach ((SearchRecord record, float score) in results)
        {
            Console.WriteLine($"{score,6:F3}  [{record.Language}]  {record.Source}:{record.Location}");
            Console.WriteLine($"        {Snippet(record.Title, 90)}");
            Console.WriteLine($"        {Snippet(record.Text.ReplaceLineEndings(" "), 90)}");
            Console.WriteLine();
        }
    }

    private static string Snippet(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "\u2026";
    }
}
