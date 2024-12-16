using Bindgen.NET;
using System.IO;

var exampleSource = await File.ReadAllTextAsync("Raylib.h");

BindingOptions exampleConfig = new()
{
    Namespace = "ExampleNamespace",
    Class = "ExampleClass",

    DllImportPath = "libexample",

    TreatInputFileAsRawSourceCode = true,
    InputFile = exampleSource,

    // Passing in the include headers provided by zig. See Bindgen.NET.Example.csproj on how to fetch zig's headers.
    SystemIncludeDirectories = { Path.Combine(BuildConstants.ZigLibPath, "include") },

    SuppressedWarnings = { "CA1069" },

    GenerateFunctionPointers = true,
    GenerateMacros = true,
    RemappedTypeNames = new()
    {
        ["Vector2"] = "System.Numerics.Vector2"
    }
};

var generatedSource = BindingGenerator.Generate(exampleConfig);

System.Console.WriteLine(generatedSource);

// Resulting bindings can be viewed in GeneratedExample.cs
// https://github.com/BeanCheeseBurrito/Bindgen.NET/blob/main/Bindgen.NET.Example/GeneratedExample.cs
