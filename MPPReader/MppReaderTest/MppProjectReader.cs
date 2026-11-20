using MPXJ.Net;

namespace MppReaderTest;

public enum ReadFailureKind
{
    None,
    InvalidArguments,
    FileNotFound,
    ReadError
}

public sealed class MppReadResult
{
    public ProjectFile? Project { get; init; }
    public string? ReaderName { get; init; }
    public ReadFailureKind Failure { get; init; }
    public string? Message { get; init; }
}

/// <summary>Validates the input file and loads it exclusively through MPXJ.Net.</summary>
public static class MppProjectReader
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    public static MppReadResult Read(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".mpp", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(ReadFailureKind.InvalidArguments, $"Extensión no válida '{Path.GetExtension(path)}'. Se esperaba un fichero .mpp.");
        }

        if (!File.Exists(path))
        {
            return Fail(ReadFailureKind.FileNotFound, $"No se encuentra el fichero: {path}");
        }

        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (probe.Length == 0) return Fail(ReadFailureKind.ReadError, "El fichero está vacío.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(ReadFailureKind.ReadError, $"Acceso denegado al fichero: {ex.Message}");
        }
        catch (IOException ex) when (ex.HResult is SharingViolation or LockViolation)
        {
            return Fail(ReadFailureKind.ReadError, $"El fichero está bloqueado por otro proceso: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Fail(ReadFailureKind.ReadError, $"Error de E/S al abrir el fichero: {ex.Message}");
        }

        try
        {
            var reader = new UniversalProjectReader();
            UniversalProjectReader.IProjectReaderProxy? proxy;
            IProjectReader? projectReader;
            try
            {
                proxy = reader.GetProjectReaderProxy(path);
                projectReader = proxy?.ProjectReader;
            }
            catch (NullReferenceException)
            {
                // MPXJ.Net 16.x wraps a null Java reader without checking, so an unrecognised file surfaces as a NullReferenceException.
                proxy = null;
                projectReader = null;
            }

            if (proxy == null || projectReader == null)
            {
                return Fail(ReadFailureKind.ReadError, "MPXJ no reconoce el formato del fichero (fichero MPP inválido, dañado o versión no soportada).");
            }

            var readerName = projectReader.GetType().Name;
            var project = proxy.Read();
            if (project == null)
            {
                return Fail(ReadFailureKind.ReadError, $"MPXJ ({readerName}) no devolvió ningún proyecto. El fichero puede estar dañado o su versión no está soportada.");
            }

            return new MppReadResult { Project = project, ReaderName = readerName };
        }
        catch (Exception ex)
        {
            return Fail(ReadFailureKind.ReadError, Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var root = ex;
        while (root.InnerException != null) root = root.InnerException;
        var text = $"{ex.GetType().FullName}: {ex.Message}";
        if (!ReferenceEquals(root, ex)) text += $" | Causa: {root.GetType().FullName}: {root.Message}";

        var lower = text.ToLowerInvariant();
        if (lower.Contains("password") || lower.Contains("encrypt"))
            return "Fichero MPP protegido con contraseña o cifrado. " + text;
        if (lower.Contains("unsupported") || lower.Contains("version") || lower.Contains("not supported"))
            return "Versión o formato MPP no soportado por MPXJ. " + text;
        return "Error leyendo el fichero MPP con MPXJ. " + text;
    }

    private static MppReadResult Fail(ReadFailureKind kind, string message) => new() { Failure = kind, Message = message };
}
