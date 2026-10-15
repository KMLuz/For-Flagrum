using System;
using System.IO;
using System.Net;
using System.Reflection;

namespace Flagrum.Application.Features.ModManager.Services;

/// <summary>
/// Turns exceptions from mod manager operations into messages that can be shown to the user.
/// </summary>
public static class ModOperationError
{
    /// <summary>
    /// Creates a heading and HTML message describing why a mod operation failed.
    /// </summary>
    public static (string Heading, string Message) Describe(Exception exception)
    {
        exception = Unwrap(exception);

        return exception switch
        {
            GameFilesInUseException inUse => ("Game Files in Use",
                $"Flagrum can't change <strong>{WebUtility.HtmlEncode(Path.GetFileName(inUse.FilePath))}</strong> " +
                "because another program is using it, most likely Forspoken. " +
                "Close the game and try again. Nothing was changed."),
            IOException {HResult: SharingViolation or LockViolation} => ("Game Files in Use",
                "A game file is being used by another program, most likely Forspoken. " +
                "Close the game and try again."),
            UnauthorizedAccessException => ("Access Denied",
                "Windows refused access to a game file. Make sure the game folder isn't read-only " +
                "and that no antivirus is blocking Flagrum, then try again."),
            _ => ("Something Went Wrong",
                $"{WebUtility.HtmlEncode(exception.Message)}<br/><br/>" +
                "The full details have been saved to Flagrum's log file.")
        };
    }

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    private static Exception Unwrap(Exception exception)
    {
        while (true)
        {
            switch (exception)
            {
                case AggregateException {InnerExceptions.Count: 1} aggregate:
                    exception = aggregate.InnerExceptions[0];
                    break;
                case TargetInvocationException {InnerException: not null} invocation:
                    exception = invocation.InnerException;
                    break;
                default:
                    return exception;
            }
        }
    }
}
