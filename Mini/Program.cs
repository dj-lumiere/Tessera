using Mini;

// mini <input.mini> [-o <output.tess>]
//
// Compiles a Mini program to Tessera source. Without -o the Tessera goes to stdout.

if (args.Length is not (1 or 3) || (args.Length == 3 && args[1] != "-o"))
{
    Console.Error.WriteLine("usage: mini <input.mini> [-o <output.tess>]");
    return 2;
}

string input = args[0];
try
{
    var program = Parser.Parse(File.ReadAllText(input));
    string tessera = Emitter.Emit(program, Path.GetFileName(input));
    if (args.Length == 3) File.WriteAllText(args[2], tessera);
    else Console.Write(tessera);
    return 0;
}
catch (MiniError e)
{
    Console.Error.WriteLine($"{input}:{e.Message}");
    return 1;
}
