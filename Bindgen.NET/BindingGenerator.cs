﻿using ClangSharp;
using ClangSharp.Interop;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Type = ClangSharp.Type;

namespace Bindgen.NET;

/// <summary>
/// Static class for generating bindings from configuration classes.
/// </summary>
public static class BindingGenerator
{
    private const string MacroPrefix = "BindgenMacro";
    private const string AnonymousPrefix = "Anonymous";

    private static BindingOptions _options = new();

    /// <summary>
    /// Generates bindings based on the values specified in the <c>options</c> parameter.
    /// </summary>
    /// <param name="options">The configuration options to use when generating bindings.</param>
    /// <returns>A string of the generated source code.</returns>
    public static string Generate(BindingOptions options)
    {
        _options = options;
        (TranslationUnit translationUnit, CXIndex index) = ProcessTranslationUnit();

        Cursor[] cursors = translationUnit.TranslationUnitDecl.CursorChildren
            .Where(cursor => cursor is FunctionDecl or RecordDecl or EnumDecl or VarDecl)
            .Where(cursor => !cursor.Location.IsInSystemHeader)
            .Where(IsUserInclude)
            .GroupBy(cursor => cursor.Handle.Spelling.CString)
            .Select(group => group.First()) // Duplicate cursors that have same spelling.
            .Where(cursor => !_options.Ignored.Contains(cursor.Handle.Spelling.CString))
            .ToArray();

        FunctionDecl[] functionDecls = [.. cursors
            .OfType<FunctionDecl>()
            .OrderBy(x => x.Name)];

        RecordDecl[] recordDecls = cursors
            .OfType<RecordDecl>()
            .OrderBy(x => x.Name)
            .GroupBy(x => x.Name)
            .Select(x => x.First())
            .ToArray();

        EnumDecl[] enumDecls = [.. cursors
            .OfType<EnumDecl>()
            .OrderBy(x => x.Name)];

        VarDecl[] varDecls = [.. cursors
            .OfType<VarDecl>()
            .OrderBy(x => x.Name)];

        VarDecl[] macroVarDecls = varDecls
            .Where(x => x.Name.StartsWith(MacroPrefix, StringComparison.Ordinal))
            .ToArray();

        VarDecl[] externVarDecls = varDecls
            .Where(x => x.HasExternalStorage)
            .ToArray();

        StringBuilder outputBuilder = new();
        StringBuilder nativeOutputBuilder = new();

        foreach (FunctionDecl functionDecl in functionDecls)
        {
            outputBuilder.AppendLine(GenerateFunctionDecl(functionDecl));
        }

        foreach (RecordDecl recordDecl in recordDecls)
        {
            if (options.RemappedTypeNames.ContainsKey(recordDecl.Name))
            {
                continue;
            }

            outputBuilder.AppendLine(GenerateRecordDecl(recordDecl));
        }

        foreach (EnumDecl enumDecl in enumDecls)
        {
            outputBuilder.AppendLine(GenerateEnumDecl(enumDecl));
        }

        foreach (EnumDecl enumDecl in enumDecls)
        {
            outputBuilder.AppendLine(GenerateEnumDeclConstants(enumDecl));
        }

        if (_options.GenerateMacros)
        {
            foreach (VarDecl varDecl in macroVarDecls)
            {
                outputBuilder.AppendLine(GenerateMacroVarDecl(varDecl));
            }
        }

        if (_options.GenerateExternVariables)
        {
            foreach (VarDecl varDecl in externVarDecls)
            {
                outputBuilder.AppendLine(GenerateExternVarDeclManagedGetter(varDecl));
            }

            foreach (VarDecl varDecl in externVarDecls)
            {
                outputBuilder.AppendLine(GenerateExternVarDeclField(varDecl));
            }

            foreach (VarDecl varDecl in externVarDecls)
            {
                outputBuilder.AppendLine(GenerateExternVarDeclProperty(varDecl));
            }

            foreach (VarDecl varDecl in externVarDecls)
            {
                nativeOutputBuilder.AppendLine(GenerateExternVarDeclNativeVariable(varDecl));
            }

            foreach (VarDecl varDecl in externVarDecls)
            {
                nativeOutputBuilder.AppendLine(GenerateExternVarDeclNativeGetter(varDecl));
            }
        }

        string output = CodeFormatter.Format($$"""
                #nullable enable
                {{(_options.SuppressedWarnings.Count > 0 ? $"#pragma warning disable {string.Join(' ', _options.SuppressedWarnings)}" : string.Empty)}}
                namespace {{_options.Namespace}}
                {
                    public static unsafe partial class {{_options.Class}}
                    {
                        {{outputBuilder}}
                        {{GenerateBindgenInternal()}}
                    }
                }
                {{(_options.SuppressedWarnings.Count > 0 ? $"#pragma warning restore {string.Join(' ', _options.SuppressedWarnings)}" : string.Empty)}}
                #nullable disable
            """);

        string nativeOutput = $$"""
            #ifdef _WIN32
                #define BINDGEN_API __declspec(dllexport)
            #else
                #define BINDGEN_API __attribute__((visibility("default")))
            #endif
            {{nativeOutputBuilder}}
            """;


        if (options.OutputFile != null)
        {
            File.WriteAllText(options.OutputFile, output);
            Diagnostic.Log(DiagnosticLevel.Info, $"Generated {Path.GetFullPath(options.OutputFile)} from {GetInputFileName()}");
        }

        if (options.NativeOutputFile != null)
        {
            File.WriteAllText(options.NativeOutputFile, nativeOutput);
            Diagnostic.Log(DiagnosticLevel.Info, $"Generated {Path.GetFullPath(options.NativeOutputFile)} from {GetInputFileName()}");
        }

        translationUnit.Dispose();
        index.Dispose();

        return output;
    }

    private static (TranslationUnit, CXIndex) ProcessTranslationUnit()
    {
        Diagnostic.CurrentDiagnosticLevel = _options.DiagnosticLevel;

        string inputFileName = GetInputFileName();

        List<string> arguments = _options.IncludeDirectories
            .Union(_options.SystemIncludeDirectories)
            .Select(includeDirectory => "-I" + Path.GetFullPath(includeDirectory))
            .ToList();

        List<CXUnsavedFile> unsavedFiles = [];
        CXTranslationUnit_Flags flags = default;

        if (_options.GenerateMacros)
        {
#pragma warning disable S3265 // Non-flags enums should not be used in bitwise operations
            flags |= CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord;
#pragma warning restore S3265 // Non-flags enums should not be used in bitwise operations
        }

        if (_options.TreatInputFileAsRawSourceCode)
        {
            unsavedFiles.Add(CXUnsavedFile.Create(inputFileName, _options.InputFile));
        }
        else if (!Path.Exists(inputFileName))
        {
            throw new FileNotFoundException($"Input file at path \"{inputFileName}\" does not exist.", inputFileName);
        }

        CXIndex index = CXIndex.Create();
        CXErrorCode errorCode = CXTranslationUnit.TryParse(index, inputFileName, arguments.ToArray(), unsavedFiles.ToArray(), flags, out CXTranslationUnit handle);

        foreach (CXUnsavedFile unsavedFile in unsavedFiles)
        {
            unsavedFile.Dispose();
        }

        ProcessDiagnostics(errorCode, handle);

        TranslationUnit translationUnit = TranslationUnit.GetOrCreate(handle);

        if (_options.GenerateMacros)
        {
            translationUnit = ProcessMacros(index, translationUnit, arguments.ToArray(), flags);
        }

        return (translationUnit, index);
    }

    private static string GetInputFileName()
    {
        return _options.TreatInputFileAsRawSourceCode ? _options.RawSourceName : Path.GetFullPath(_options.InputFile);
    }

    // TODO: Handle errors
    private static void ProcessDiagnostics(CXErrorCode errorCode, CXTranslationUnit handle)
    {
        if (handle.NumDiagnostics != 0)
        {
            for (uint i = 0; i < handle.NumDiagnostics; i++)
            {
                using CXDiagnostic diagnostic = handle.GetDiagnostic(i);
                Diagnostic.Log(DiagnosticLevel.Warning, diagnostic.Format(CXDiagnostic.DefaultDisplayOptions).ToString());
            }
        }
    }

    // We collect all macro definition records and append them to the end of the file as type-inferred auto variables.
    private static TranslationUnit ProcessMacros(CXIndex index, TranslationUnit translationUnit, ReadOnlySpan<string> arguments, CXTranslationUnit_Flags flags)
    {
        string inputFileName = GetInputFileName();
        CXTranslationUnit translationUnitHandle = translationUnit.Handle;

        CXFile file = translationUnitHandle.GetFile(inputFileName);
        ReadOnlySpan<byte> inputFileContents = translationUnitHandle.GetFileContents(file, out UIntPtr _);

        StringBuilder newFileBuilder = new();
        newFileBuilder.AppendLine(Encoding.UTF8.GetString(inputFileContents));

        MacroDefinitionRecord[] macroDefinitionRecords = translationUnit.TranslationUnitDecl.CursorChildren
            .OfType<MacroDefinitionRecord>()
            .Where(macro => !macro.Location.IsInSystemHeader)
            .Where(macro => !IsFromNamelessFile(macro))
            .Where(IsUserInclude)
            .ToArray();

        foreach (MacroDefinitionRecord macroDefinitionRecord in macroDefinitionRecords)
        {
            newFileBuilder.AppendLine(GenerateMacroDummy(macroDefinitionRecord));
        }

        translationUnit.Dispose();

        List<CXUnsavedFile> unsavedFiles = [CXUnsavedFile.Create(inputFileName, newFileBuilder.ToString())];

#pragma warning disable S3265 // Non-flags enums should not be used in bitwise operations
        CXTranslationUnit handle = CXTranslationUnit.Parse(index, inputFileName, arguments.ToArray(), unsavedFiles.ToArray(), flags & ~CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord);
#pragma warning restore S3265 // Non-flags enums should not be used in bitwise operations

        foreach (CXUnsavedFile unsavedFile in unsavedFiles)
        {
            unsavedFile.Dispose();
        }

        return TranslationUnit.GetOrCreate(handle);
    }

    private static string GetSourceRangeContents(CXTranslationUnit translationUnit, CXSourceRange sourceRange)
    {
        sourceRange.Start.GetFileLocation(out CXFile startFile, out uint _, out uint _, out uint startOffset);
        sourceRange.End.GetFileLocation(out CXFile endFile, out uint _, out uint _, out uint endOffset);

        if (startFile != endFile)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> fileContents = translationUnit.GetFileContents(startFile, out UIntPtr _);
        fileContents = fileContents.Slice(unchecked((int)startOffset), unchecked((int)(endOffset - startOffset)));

        return Encoding.UTF8.GetString(fileContents);
    }

    // We don't want to generate bindings for stuff inside of system includes. We use this to filter for non-system headers.
    private static bool IsUserInclude(Cursor cursor)
    {
        cursor.Location.GetFileLocation(out CXFile file, out _, out _, out _);
        string fileName = file.Name.ToString();

        return _options.SystemIncludeDirectories
            .Select(Path.GetFullPath)
            .All(fullIncludeDirectory => !fileName.StartsWith(fullIncludeDirectory, StringComparison.Ordinal));
    }

    // TODO: Are there cases where nameless files are fine?
    // Don't generate macros from files with empty names because it includes some junk.
    private static bool IsFromNamelessFile(Cursor cursor)
    {
        cursor.Location.GetFileLocation(out CXFile file, out uint _, out uint _, out uint _);
        return string.IsNullOrEmpty(file.Name.ToString());
    }

    private static bool RecordHasDefinition(RecordDecl recordDecl)
    {
        while (!recordDecl.IsThisDeclarationADefinition)
        {
            if (recordDecl.Definition == null)
            {
                return false;
            }

            recordDecl = recordDecl.Definition;
        }

        return true;
    }

    private static RecordDecl GetRecordDefinition(RecordDecl recordDecl)
    {
        while (!recordDecl.IsThisDeclarationADefinition)
        {
            if (recordDecl.Definition == null)
            {
                break;
            }

            recordDecl = recordDecl.Definition;
        }

        return recordDecl;
    }

    private static bool IsType<T>(Type type, [NotNullWhen(true)] out T? value) where T : Type
    {
        if (type is T t)
        {
            value = t;
            return true;
        }

        if (type is ElaboratedType elaboratedType)
        {
            return IsType(elaboratedType.CanonicalType, out value);
        }

        if (type is PointerType pointerType)
        {
            return IsType(pointerType.PointeeType, out value);
        }

        value = default;

        return false;
    }

    private static int GetConstantArraySize(ConstantArrayType constantArrayType)
    {
        long size = constantArrayType.Size;

        while (constantArrayType.ElementType is ConstantArrayType elementType)
        {
            size *= elementType.Size;
            constantArrayType = elementType;
        }

        return (int)size;
    }

    private static bool IsSupportedFixedSizedBufferType(string typeName)
    {
        return typeName switch
        {
            "bool" or "byte" or "char" or "double" or "float" or "int" or "long" or "sbyte" or "short" or "ushort"
                or "uint" or "ulong" => true,
            _ => false
        };
    }

    private static string GenerateFixedBufferName(string name)
    {
        return name + "_FixedBuffer";
    }

    private static string GenerateExternFieldName(string name)
    {
        return name + "_Ptr";
    }

    private static string GenerateExternGetterName(string name)
    {
        return name + "_BindgenGetExtern";
    }

    private static string GenerateBindgenInternal()
    {
        return $$"""
            public partial class BindgenInternal
            {
                public const string DllImportPath = @"{{_options.DllImportPath}}";
            }
        """;
    }

    private static string GenerateFunctionDecl(FunctionDecl functionDecl)
    {
        IEnumerable<string> parameters = functionDecl.Parameters
            .Select(parameter => $"{GetTypeName(parameter.Type)} {GetValidIdentifier(parameter.Name)}")
            .ToArray();

        return $@"
            {(_options.GenerateSuppressGcTransition ? "[System.Runtime.InteropServices.SuppressGCTransition]" : string.Empty)}
            [System.Runtime.InteropServices.DllImport(BindgenInternal.DllImportPath, EntryPoint = ""{GetValidIdentifier(functionDecl.NameInfoName, false)}"", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
            public static extern {GetTypeName(functionDecl.ReturnType)} {GetValidIdentifier(functionDecl.Name)}({string.Join(", ", parameters)});
        ";
    }

    private static string GenerateRecordDecl(RecordDecl recordDecl)
    {
        if (RecordHasDefinition(recordDecl) && !recordDecl.IsThisDeclarationADefinition)
        {
            return GenerateRecordDecl(GetRecordDefinition(recordDecl));
        }

        string recordName = GetRemappedCursorName(recordDecl);

        FieldDecl[] fieldsDecls = recordDecl.Decls
            .OfType<FieldDecl>()
            .ToArray();

        IndirectFieldDecl[] indirectFieldDecls = recordDecl.Decls
            .OfType<IndirectFieldDecl>()
            .ToArray();

        FieldDecl[] fixedBufferFieldDecls = recordDecl.Decls
            .OfType<FieldDecl>()
            .Where(fieldDecl => fieldDecl.Type is ConstantArrayType)
            .Where(fieldDecl => !IsSupportedFixedSizedBufferType(GetTypeName(((ConstantArrayType)fieldDecl.Type).ElementType)))
            .ToArray();

        RecordDecl[] recordFieldsDecls = recordDecl.Decls
            .OfType<RecordDecl>()
            .ToArray();

        StringBuilder fields = new();

        foreach (FieldDecl fieldDecl in fieldsDecls)
        {
            if (recordDecl.IsUnion)
            {
                fields.AppendLine("[System.Runtime.InteropServices.FieldOffset(0)]");
            }

            string fieldName = GetValidIdentifier(fieldDecl.Name);
            string typeName = GetRemappedTypeName(fieldDecl.Type);

            if (fieldDecl.IsAnonymousField)
            {
                fieldName = GetRemappedTypeName(fieldDecl.Type) + "_Field";
            }

            if (fieldDecl.Type is ConstantArrayType constantArrayType && IsSupportedFixedSizedBufferType(GetTypeName(constantArrayType.ElementType)))
            {
                int size = GetConstantArraySize(constantArrayType);
                fields.AppendLine(CultureInfo.InvariantCulture, $"public fixed {GetTypeName(constantArrayType.ElementType)} {GetValidIdentifier(fieldDecl.Name)}[{size}];");
                continue;
            }

            if (fieldDecl.Type is ConstantArrayType)
            {
                typeName = GenerateFixedBufferName(fieldName);
            }

            bool commentFunctionPointer = IsType(fieldDecl.Type, out FunctionProtoType? functionProtoType) && !_options.GenerateFunctionPointers;

            fields.AppendLine(CultureInfo.InvariantCulture, $"public {typeName} {ToCamelCase(fieldName)}; {(commentFunctionPointer ? "// " + GetCSharpFunctionPointer(functionProtoType!) : string.Empty)}");
        }

        foreach (IndirectFieldDecl indirectFieldDecl in indirectFieldDecls)
        {
            IDeclContext declContext = indirectFieldDecl.AnonField.DeclContext!;

            if (declContext is RecordDecl contextRecordDecl)
            {
                string typeName = GetRemappedTypeName(indirectFieldDecl.Type);
                string declContextName = GetRemappedCursorName(contextRecordDecl);
                string fieldName = GetValidIdentifier(indirectFieldDecl.Name);

                if (IsAnonymous(indirectFieldDecl.Type))
                {
                    typeName = $"{declContextName}.{typeName}";
                }

                fields.AppendLine(CultureInfo.InvariantCulture,
                    $"public ref {typeName} {fieldName} => ref {declContextName}_Field.{fieldName};");
            }
        }

        foreach (FieldDecl fieldDecl in fixedBufferFieldDecls)
        {
            ConstantArrayType constantArrayType = (ConstantArrayType)fieldDecl.Type;

            string fieldName = GenerateFixedBufferName(GetValidIdentifier(fieldDecl.Name));
            string typeName = GetTypeName(constantArrayType.ElementType);
            int arraySize = GetConstantArraySize(constantArrayType);

            StringBuilder fixedBufferFields = new();

            for (int i = 0; i < arraySize; i++)
            {
                fixedBufferFields.AppendLine(CultureInfo.InvariantCulture, $"public {typeName} Item{i};");
            }

            string indexer;

            if (IsType(constantArrayType.ElementType, out PointerType? _))
            {
                indexer = $$"""
                    public ref {{typeName}} this[int index]
                    {
                        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                        get
                        {
                            if (index >= {{arraySize}})
                                throw new System.ArgumentOutOfRangeException($"Index {index} is out of range.");

                            fixed ({{typeName}}* pThis = &Item0)
                                return ref pThis[index];
                        }
                    }
                """;
            }
            else
            {
                indexer = $$"""
                    public ref {{typeName}} this[int index] => ref AsSpan()[index];

                    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                    public System.Span<{{typeName}}> AsSpan() => System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref Item0, {{arraySize}});
                """;
            }

            string fixedBufferSource = $$"""
                public partial struct {{fieldName}}{{(_options.GenerateStructEqualityFunctions ? $" : System.IEquatable<{fieldName}> " : "")}}
                {
                    {{fixedBufferFields}}
                    {{indexer}}
                    {{(_options.GenerateStructEqualityFunctions ? GenerateRecordEqualityFunctions(fieldName) : "")}}
                }
            """;

            fields.AppendLine(fixedBufferSource);
        }

        foreach (RecordDecl recordFieldDecl in recordFieldsDecls)
        {
            fields.AppendLine(GenerateRecordDecl(recordFieldDecl));
        }

        return $@"
            {(recordDecl.IsUnion ? "[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]" : "")}
            public partial struct {recordName}{(_options.GenerateStructEqualityFunctions ? $" : System.IEquatable<{recordName}> " : "")}
            {{
                {fields}
                {(_options.GenerateStructEqualityFunctions ? GenerateRecordEqualityFunctions(recordName) : "")}
            }} 
        ";
    }

    private static string GenerateEnumDecl(EnumDecl enumDecl)
    {
        List<string> enumMembers = [];
        bool hasNegatives = false;

        foreach (EnumConstantDecl enumConstant in enumDecl.Enumerators)
        {
            if (enumConstant.IsNegative)
            {
                hasNegatives = true;
            }

            enumMembers.Add(GenerateEnumConstantDecl(enumConstant));
        }

        return $@"
            public enum {GetCursorName(enumDecl)} : {GetIntegerName(enumDecl.IntegerType.Handle.SizeOf, hasNegatives, "INVALID_ENUM_INTEGER")}
            {{ 
                {string.Join(",\n", enumMembers)}
            }}
        ";
    }

    private static string GenerateEnumConstantDecl(EnumConstantDecl enumConstantDecl)
    {
        string value = enumConstantDecl.IsSigned
            ? enumConstantDecl.InitVal.ToString(CultureInfo.InvariantCulture)
            : enumConstantDecl.UnsignedInitVal.ToString(CultureInfo.InvariantCulture);

        return $"{GetValidIdentifier(enumConstantDecl.Name)} = {value}";
    }

    private static string GenerateEnumDeclConstants(EnumDecl enumDecl)
    {
        string enumName = GetCursorName(enumDecl);

        IEnumerable<string> constantFields = enumDecl.Decls.OfType<EnumConstantDecl>().Select(enumConstantDecl =>
        {
            string enumMemberName = GetValidIdentifier(enumConstantDecl.Name);
            return $"public const {enumName} {enumMemberName} = {enumName}.{enumMemberName};";
        });

        return string.Join("\n", constantFields);
    }

    // TODO: Add binding option to allow for generating refs to extern variables
    private static string GenerateMacroVarDecl(VarDecl varDecl)
    {
        if (!varDecl.Name.StartsWith(MacroPrefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (!varDecl.HasInit)
        {
            return string.Empty;
        }

        Expr init = varDecl.Init;
        CXEvalResult result = varDecl.Handle.Evaluate;

        string typeName = GetTypeName(varDecl.Type);
        string expression;

        switch (result.Kind)
        {
            case CXEvalResultKind.CXEval_Float:
                expression = init.Type.Kind switch
                {
                    CXTypeKind.CXType_Double => result.AsDouble.ToString(CultureInfo.InvariantCulture),
                    CXTypeKind.CXType_Float => ((float)result.AsDouble).ToString(CultureInfo.InvariantCulture) + "f",
                    CXTypeKind.CXType_LongDouble => ((decimal)result.AsDouble).ToString(CultureInfo.InvariantCulture),
                    _ => $"INVALID_FLOAT_{init.Type.Kind}"
                };
                break;
            case CXEvalResultKind.CXEval_Int:
                expression = init.Type.Handle.SizeOf switch
                {
                    1 => result.IsUnsignedInt ? ((byte)result.AsUnsigned).ToString(CultureInfo.InvariantCulture) : ((sbyte)result.AsLongLong).ToString(CultureInfo.InvariantCulture),
                    2 => result.IsUnsignedInt ? ((ushort)result.AsUnsigned).ToString(CultureInfo.InvariantCulture) : ((short)result.AsLongLong).ToString(CultureInfo.InvariantCulture),
                    4 => result.IsUnsignedInt ? ((uint)result.AsUnsigned).ToString(CultureInfo.InvariantCulture) : ((int)result.AsLongLong).ToString(CultureInfo.InvariantCulture),
                    8 => result.IsUnsignedInt ? result.AsUnsigned.ToString(CultureInfo.InvariantCulture) : result.AsLongLong.ToString(CultureInfo.InvariantCulture),
                    _ => $"INVALID_INTEGER_SIZEOF_{init.Type.Handle.SizeOf}"
                };
                break;
            case CXEvalResultKind.CXEval_StrLiteral:
                typeName = "string";
                expression = "\"" + result.AsStr + "\"";
                break;
            case CXEvalResultKind.CXEval_ObjCStrLiteral:
            case CXEvalResultKind.CXEval_CFStr:
            case CXEvalResultKind.CXEval_Other:
            case CXEvalResultKind.CXEval_UnExposed:
            default:
                return "";
        }

        return string.IsNullOrEmpty(expression)
            ? string.Empty
            : $"public const {typeName} {GetValidIdentifier(varDecl.Name[MacroPrefix.Length..])} = {expression};";
    }

    private static string GenerateExternVarDeclNativeVariable(VarDecl varDecl)
    {
        return $$"""
            extern void* {{varDecl.Name}};
            """;
    }

    private static string GenerateExternVarDeclNativeGetter(VarDecl varDecl)
    {
        return $$"""
        BINDGEN_API void* {{GenerateExternGetterName(varDecl.Name)}}() {
            return &{{varDecl.Name}};
        }
        """;
    }

    private static string GenerateExternVarDeclManagedGetter(VarDecl varDecl)
    {
        string validName = GetValidIdentifier(varDecl.Name);
        return $$"""
            [System.Runtime.InteropServices.DllImport(BindgenInternal.DllImportPath, EntryPoint = "{{GenerateExternGetterName(varDecl.Name)}}", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
            private static extern void* {{GenerateExternGetterName(validName)}}();
            """;
    }

    private static string GenerateExternVarDeclField(VarDecl varDecl)
    {
        string validName = GetValidIdentifier(varDecl.Name);
        string fieldName = GenerateExternFieldName(validName);
        return varDecl.HasExternalStorage ? $"private static void* {fieldName};" : "";
    }

    private static string GenerateExternVarDeclProperty(VarDecl varDecl)
    {
        if (!varDecl.HasExternalStorage)
        {
            return "";
        }

        string typeName = GetTypeName(varDecl.Type);
        string validName = GetValidIdentifier(varDecl.Name);
        string fieldName = GenerateExternFieldName(validName);
        string getterName = GenerateExternGetterName(validName);

        // We can't use Unsafe.AsRef<T>(void*) because T can't be a pointer.
        return $"public static ref {typeName} {validName} => ref *({typeName}*)({fieldName} == null ? {fieldName} = {getterName}() : {fieldName});";
    }

    // This converts value-like macros to type-inferred variables so we can get access to it's type information.
    // The macro's constants will be generated in GenerateMacroVarDecl().
    private static string GenerateMacroDummy(MacroDefinitionRecord macro)
    {
        if (macro.IsFunctionLike)
        {
            return string.Empty;
        }

        CXTranslationUnit translationUnitHandle = macro.TranslationUnit.Handle;
        Span<CXToken> tokens = translationUnitHandle.Tokenize(macro.Extent);

        bool hasNoValue = tokens[0].Kind != CXTokenKind.CXToken_Identifier ||
                          tokens[0].GetSpelling(translationUnitHandle).CString != macro.Spelling ||
                          tokens.Length == 1;

        if (hasNoValue)
        {
            return string.Empty;
        }

        CXSourceLocation sourceRangeEnd = tokens[^1].GetExtent(translationUnitHandle).End;
        CXSourceLocation sourceRangeStart = tokens[1].GetLocation(translationUnitHandle);
        CXSourceRange sourceRange = CXSourceRange.Create(sourceRangeStart, sourceRangeEnd);

        string value = GetSourceRangeContents(translationUnitHandle, sourceRange);

        return $"const __auto_type {MacroPrefix}{macro.Name} = {value};";
    }

    private static string GenerateRecordEqualityFunctions(string recordName)
    {
        return $@"
            public bool Equals({recordName} other)
            {{
                fixed ({recordName}* __self = &this)
                {{
                    return System.MemoryExtensions.SequenceEqual(
                        new System.ReadOnlySpan<byte>((byte*)__self, sizeof({recordName})),
                        new System.ReadOnlySpan<byte>((byte*)&other, sizeof({recordName}))
                    );
                }}
            }}

            public override bool Equals(object? obj)
            {{
                return obj is {recordName} other && Equals(other);
            }}

            public static bool operator ==({recordName} left, {recordName} right)
            {{
                return left.Equals(right);
            }}

            public static bool operator !=({recordName} left, {recordName} right)
            {{
                return !(left == right);
            }}

            public override int GetHashCode()
            {{
                fixed ({recordName}* __self = &this)
                {{
#if NET6_0_OR_GREATER
                    System.HashCode hash = new System.HashCode();
                    hash.AddBytes(new System.ReadOnlySpan<byte>((byte*)__self, sizeof({recordName})));
                    return hash.ToHashCode();
#else
                    return base.GetHashCode();
#endif
                }}
            }}
        ";
    }

    private static string GetCSharpFunctionPointer(FunctionProtoType functionProtoType)
    {
        List<string> parameters = functionProtoType.ParamTypes.Select(GetTypeName).ToList();
        parameters.Add(GetTypeName(functionProtoType.ReturnType));
        return $"delegate* unmanaged<{string.Join(", ", parameters)}>";
    }

    private static bool IsAnonymous(Type type)
    {
        string? cursorName = null;

        if (IsType(type, out RecordType? recordType))
        {
            cursorName = GetCursorName(recordType!.Decl);
        }

        if (IsType(type, out EnumType? enumType))
        {
            cursorName = GetCursorName(enumType!.Decl);
        }

        return cursorName?.StartsWith(AnonymousPrefix, StringComparison.InvariantCulture) ?? false;
    }

    private static string GetAnonymousName(Cursor cursor, string kind)
    {
        cursor.Location.GetFileLocation(out CXFile file, out uint line, out uint column, out _);
        string fileName = Path.GetFileNameWithoutExtension(file.Name.ToString());
        return $"{AnonymousPrefix}{kind}_{fileName}_L{line}_C{column}";
    }

    private static string GetCursorName(NamedDecl namedDecl)
    {
        string name = GetValidIdentifier(namedDecl.Name);

        if (namedDecl is TypeDecl typeDecl)
        {
            bool isAnonymous =
                string.IsNullOrWhiteSpace(name) ||
                name.StartsWith("struct (unnamed", StringComparison.Ordinal) ||
                name.StartsWith("union (unnamed", StringComparison.Ordinal) ||
                name.StartsWith("enum (unnamed", StringComparison.Ordinal) ||
                name.StartsWith("struct (anonymous", StringComparison.Ordinal) ||
                name.StartsWith("union (anonymous", StringComparison.Ordinal) ||
                name.StartsWith("enum (anonymous", StringComparison.Ordinal) ||
                name.StartsWith("(unnamed struct", StringComparison.Ordinal) ||
                name.StartsWith("(unnamed union", StringComparison.Ordinal) ||
                name.StartsWith("(unnamed enum", StringComparison.Ordinal);

            return isAnonymous ? GetAnonymousName(typeDecl, typeDecl.TypeForDecl.KindSpelling) : name;
        }

        return name;
    }

    private static string GetSignedIntegerName(long size, string? error = null)
    {
        return size switch
        {
            1 => "sbyte",
            2 => "short",
            4 => "int",
            8 => "long",
            _ => error ?? "INVALID_SIGNED_INTEGER"
        };
    }

    private static string GetUnsignedIntegerName(long size, string? error = null)
    {
        return size switch
        {
            1 => "byte",
            2 => "ushort",
            4 => "uint",
            8 => "ulong",
            _ => error ?? "INVALID_UNSIGNED_INTEGER"
        };
    }

    private static string GetIntegerName(long size, bool signed, string? error = null)
    {
        return signed ? GetSignedIntegerName(size, error) : GetUnsignedIntegerName(size, error);
    }

    private static string GetRemappedCursorName(NamedDecl namedDecl)
    {
        string name = GetCursorName(namedDecl);

        if (namedDecl is RecordDecl recordDecl && name.StartsWith(AnonymousPrefix, StringComparison.Ordinal) && recordDecl.Parent is RecordDecl parentRecordDecl)
        {
            FieldDecl? matchingField = parentRecordDecl.Fields
                .FirstOrDefault(fieldDecl => fieldDecl.Type.CanonicalType == recordDecl.TypeForDecl.CanonicalType);

            if (matchingField is not null && !string.IsNullOrEmpty(matchingField.Name))
            {
                return $"{GetValidIdentifier(matchingField.Name)}_AnonymousRecord";
            }
        }

        return name;
    }

    private static string GetRemappedTypeName(Type type)
    {
        string name = GetTypeName(type);

        if (IsType(type, out RecordType? recordType) && name.StartsWith(AnonymousPrefix, StringComparison.Ordinal) && recordType.Decl.Parent is RecordDecl parentRecordDecl)
        {
            RecordDecl recordDecl = recordType.Decl;

            FieldDecl? matchingField = parentRecordDecl.Fields
                .FirstOrDefault(fieldDecl => fieldDecl.Type.CanonicalType == recordDecl.TypeForDecl.CanonicalType);

            if (matchingField is not null && !string.IsNullOrEmpty(matchingField.Name))
            {
                return $"{GetValidIdentifier(matchingField.Name)}_AnonymousRecord";
            }
        }

        return name;
    }

    private static string GetTypeName(Type type)
    {
        string GetTypeNameInner()
        {
            if (type is AutoType autoType)
            {
                return GetTypeName(autoType.GetDeducedType);
            }

            if (type is BuiltinType builtinType)
            {
                return builtinType.Kind switch
                {
                    CXTypeKind.CXType_Bool => "byte",
                    CXTypeKind.CXType_Float => "float",
                    CXTypeKind.CXType_Double => "double",
                    CXTypeKind.CXType_LongDouble => "decimal",
                    CXTypeKind.CXType_Void => "void",
                    CXTypeKind.CXType_Char16 or CXTypeKind.CXType_Char32 or CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U or CXTypeKind.CXType_SChar or CXTypeKind.CXType_UChar or CXTypeKind.CXType_WChar => GetIntegerName(
                                            builtinType.Handle.SizeOf,
                                            builtinType.Handle.IsSigned,
                                            $"INVALID_CHAR_{builtinType.Kind}"),
                    CXTypeKind.CXType_Short or CXTypeKind.CXType_Int or CXTypeKind.CXType_Long or CXTypeKind.CXType_LongLong => GetSignedIntegerName(builtinType.Handle.SizeOf, $"INVALID_SIGNED_INTEGER_{builtinType.Kind}_SIZEOF_{builtinType.Handle.SizeOf}"),
                    CXTypeKind.CXType_UShort or CXTypeKind.CXType_UInt or CXTypeKind.CXType_ULong or CXTypeKind.CXType_ULongLong => GetUnsignedIntegerName(builtinType.Handle.SizeOf, $"INVALID_UNSIGNED_INTEGER_{builtinType.Kind}_SIZEOF_{builtinType.Handle.SizeOf}"),
                    _ => $"INVALID_BUILTIN_{builtinType.Kind}",
                };
            }

            if (type is ConstantArrayType constantArrayType)
            {
                return GetTypeName(constantArrayType.ElementType);
            }

            if (type is ElaboratedType elaboratedType)
            {
                return elaboratedType.AsString switch
                {
                    "size_t" => "System.IntPtr",
                    "va_list" => "void*",
                    _ => GetTypeName(elaboratedType.CanonicalType)
                };
            }

            if (type is EnumType enumType)
            {
                return GetCursorName(enumType.Decl);
            }

            if (type is FunctionProtoType functionProtoType)
            {
                return !_options.GenerateFunctionPointers ? "System.IntPtr" : GetCSharpFunctionPointer(functionProtoType);
            }

            if (type is IncompleteArrayType incompleteArrayType)
            {
                return GetTypeName(incompleteArrayType.ElementType) + "*";
            }

            if (type is PointerType pointerType)
            {
                if (pointerType.CanonicalType.PointeeType is FunctionProtoType)
                {
                    return GetTypeName(pointerType.PointeeType);
                }

                if (pointerType.PointeeType.AsString == "FILE")
                {
                    return "void*";
                }

                return GetTypeName(pointerType.CanonicalType.PointeeType) + "*";
            }

            if (type is RecordType recordType)
            {
                return GetCursorName(recordType.Decl);
            }

            return "UNHANDLED_TYPE";
        }

        string typeName = GetTypeNameInner();

        return _options.RemappedTypeNames.TryGetValue(typeName, out string? remappedTypeName)
            ? remappedTypeName
            : typeName;
    }

    private static string GetValidIdentifier(string identifier, bool remap = true)
    {
        if (remap)
        {
            foreach ((string prefix, string replacement) in _options.RemappedPrefixes)
            {
                if (!identifier.StartsWith(prefix, StringComparison.InvariantCulture))
                {
                    continue;
                }

                identifier = replacement + identifier[prefix.Length..];
                break;
            }
        }

        return identifier switch
        {
            "abstract" or "as" or "base" or "bool" or "break" or "byte" or "case" or "catch" or "char" or "checked" or "class" or "const" or "continue" or "decimal" or "default" or "delegate" or "do" or "double" or "else" or "enum" or "event" or "explicit" or "extern" or "false" or "finally" or "fixed" or "float" or "for" or "foreach" or "goto" or "if" or "implicit" or "in" or "int" or "interface" or "internal" or "is" or "lock" or "long" or "namespace" or "new" or "null" or "object" or "operator" or "out" or "override" or "params" or "private" or "protected" or "public" or "readonly" or "ref" or "return" or "sbyte" or "sealed" or "short" or "sizeof" or "stackalloc" or "static" or "string" or "struct" or "switch" or "this" or "throw" or "true" or "try" or "typeof" or "uint" or "ulong" or "unchecked" or "unsafe" or "ushort" or "using" or "virtual" or "void" or "volatile" or "while" => "@" + identifier,
            _ => identifier,
        };
    }

    private static string ToCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s;
        }

        char[] chars = s.ToCharArray();

        for (int i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
            {
                break;
            }

            bool hasNext = (i + 1 < chars.Length);
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
            {
                // if the next character is a space, which is not considered uppercase 
                // (otherwise we wouldn't be here...)
                // we want to ensure that the following:
                // 'FOO bar' is rewritten as 'foo bar', and not as 'foO bar'
                // The code was written in such a way that the first word in uppercase
                // ends when if finds an uppercase letter followed by a lowercase letter.
                // now a ' ' (space, (char)32) is considered not upper
                // but in that case we still want our current character to become lowercase
                if (char.IsSeparator(chars[i + 1]))
                {
                    chars[i] = char.ToLower(chars[i], CultureInfo.InvariantCulture);
                }

                break;
            }

            chars[i] = char.ToLower(chars[i], CultureInfo.InvariantCulture);
        }

        if (char.IsLower(chars[0]))
        {
            chars[0] = char.ToUpper(chars[0], CultureInfo.InvariantCulture);
        }

        return new string(chars);
    }
}
