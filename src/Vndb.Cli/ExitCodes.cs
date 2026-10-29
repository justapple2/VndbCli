namespace Vndb.Cli;

public static class ExitCodes
{
    public const int Success = 0;
    public const int UnexpectedError = 1;
    public const int Usage = 2;
    public const int NotFound = 3;
    public const int Network = 4;
    public const int Api = 5;
    public const int Output = 6;
    public const int Cancelled = 130;
}
