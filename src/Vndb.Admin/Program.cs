using System.Text;
using Vndb.Admin;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
return await new AdminApplication(Console.Out, Console.Error).RunAsync(args);
