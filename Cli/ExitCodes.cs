using LLMHostProject.Inference;

namespace LLMHostProject.Cli;

/// <summary>The exit codes of the command line, stable so scripts can rely on them (documented in the README).</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Generic = 1;
    /// <summary>Wrong usage, or something this build does not do.</summary>
    public const int Usage = 2;
    public const int NotFound = 3;
    public const int Unauthorized = 4;
    public const int Unreachable = 5;
    /// <summary>Out of memory, or the model does not fit.</summary>
    public const int OutOfMemory = 6;
    public const int Cancelled = 7;
    /// <summary>The thing already exists or is already running.</summary>
    public const int Conflict = 8;

    /// <summary>The code for an exception that ended a command.</summary>
    public static int For(Exception ex) => ex switch
    {
        OperationCanceledException => Cancelled,
        FileNotFoundException or DirectoryNotFoundException => NotFound,
        UnauthorizedAccessException => Unauthorized,
        ModelLoadException { Kind: "guardrail" or "out-of-memory" } => OutOfMemory,
        ModelLoadException { Kind: "missing" } => NotFound,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => Unauthorized,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => NotFound,
        HttpRequestException or System.Net.Sockets.SocketException => Unreachable,
        ArgumentException => Usage,
        InvalidOperationException e when e.Message.Contains("already", StringComparison.OrdinalIgnoreCase) => Conflict,
        _ => Generic,
    };
}
