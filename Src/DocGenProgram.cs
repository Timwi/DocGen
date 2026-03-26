using System.Reflection;
using System.Text;
using RT.PostBuild;
using RT.PropellerApi;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace RT.DocGen;

internal static class DocGenProgram
{
#if DEBUG
    public const bool IsDebug = true;
#else
    public const bool IsDebug = false;
#endif

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { }

        if (args.Length == 2 && args[0] == "--post-build-check")
            return PostBuildChecker.RunPostBuildChecks(args[1], Assembly.GetExecutingAssembly());

        Console.BackgroundColor = IsDebug ? ConsoleColor.DarkBlue : ConsoleColor.DarkRed;
        Console.ForegroundColor = ConsoleColor.White;
        var msg = IsDebug ? "DEBUG MODE" : "RELEASE MODE";
        var spaces = new string(' ', (Console.BufferWidth - msg.Length - 7) / 2);
        Console.WriteLine($"{spaces}┌──{new string('─', msg.Length)}──╖{spaces}");
        Console.WriteLine($"{spaces}│  {msg}  ║{spaces}");
        Console.WriteLine($"{spaces}╘══{new string('═', msg.Length)}══╝{spaces}");
        Console.ResetColor();

        PropellerUtil.RunStandalone(PathUtil.AppPathCombine("DocGen.Settings.json"), new DocGenPropellerModule(), propagateExceptions:
#if DEBUG
            true
#else
            false
#endif
        );
        return 0;
    }
}
