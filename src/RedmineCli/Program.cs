using System.Text;
using RedmineCli;

// redmine.exe: one exe with subcommands (the Node version's redmine-cli, ported).

// Write UTF-8 without a BOM, like the Node version, so agents and pipes read Japanese correctly on Windows. The console's
// code page is put back on the way out, so the shell that ran us is left as it was.
Encoding? consoleEncoding = null;
try
{
    consoleEncoding = Console.OutputEncoding;
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (IOException)
{
    consoleEncoding = null;
}
Console.Out.NewLine = "\n";
Console.Error.NewLine = "\n";

try
{
    return await Cli.RunAsync(args);
}
finally
{
    Console.Out.Flush();
    Console.Error.Flush();
    if (consoleEncoding is not null && consoleEncoding.CodePage != Console.OutputEncoding.CodePage)
    {
        try
        {
            Console.OutputEncoding = consoleEncoding;
        }
        catch (IOException)
        {
            // No console to put back.
        }
    }
}
