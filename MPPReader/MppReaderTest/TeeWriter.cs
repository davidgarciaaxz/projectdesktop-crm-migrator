using System.Text;

namespace MppReaderTest;

/// <summary>Writes every line simultaneously to the console and to a UTF-8 text file.</summary>
public sealed class TeeWriter : IDisposable
{
    private readonly StreamWriter _file;

    public TeeWriter(string filePath)
    {
        _file = new StreamWriter(filePath, false, new UTF8Encoding(true));
    }

    public void WriteLine(string text = "")
    {
        Console.WriteLine(text);
        _file.WriteLine(text);
    }

    public void Header(string title)
    {
        WriteLine();
        WriteLine($"=== {title} ===");
    }

    public void Dispose() => _file.Dispose();
}
