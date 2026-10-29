using System.Text;
using Vndb.Cli;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
return await new CliApplication(Console.Out, Console.Error).RunAsync(args);
