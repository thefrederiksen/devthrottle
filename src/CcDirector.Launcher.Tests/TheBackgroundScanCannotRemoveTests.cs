using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using CcDirector.Reclaim.Background;
using CcDirector.Reclaim.Windows;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The background scan must never be ABLE to remove anything (Reclaim the Disk, issue 3120).
///
/// The mission forbids unattended removal outright. The Launcher hosts the background scan, and the
/// removal code - the runner, the holding store, the refusal gate - sits in the same engine assembly
/// the Launcher already references, one line away from the scan it is allowed to call. Until now the
/// only thing keeping them apart was a sentence in a class comment. These tests are what makes that
/// sentence true: a change that lets the background scan reach removal fails here, by name.
///
/// They read the COMPILED assemblies, never the source text. Source text can hide a type behind an
/// inferred variable, an alias or a file in another folder; a compiled assembly cannot use a type
/// without naming it. Three ways in are closed, one test each:
///
///   1. the Launcher names a removal type itself;
///   2. the Windows rules the background scan runs name one - the scan calls every rule, so a rule
///      that held or purged would be the background scan removing;
///   3. the background scan's own classes reach one through anything else in the engine assembly.
///
/// Every one of these passes on an absence - no removal type was found - so every one first proves
/// its instrument works: that the removal types are really where it looks, and that the same reading
/// DOES find types known to be there. A reader that found nothing at all fails instead of passing.
///
/// What this does not cover: an assembly the Launcher might load by name at run time. Nothing does
/// that today, and no reading of compiled references could see it.
/// </summary>
public sealed class TheBackgroundScanCannotRemoveTests
{
    private const string RemovalNamespace = "CcDirector.Reclaim.Removal";
    private const string BackgroundNamespace = "CcDirector.Reclaim.Background";

    // The three the review named. They are checked to exist inside the removal namespace, so moving
    // one out of it fails these tests rather than quietly taking it out of their sight.
    private static readonly string[] NamedRemovalTypes = ["ReclaimRunner", "HoldingStore", "RefusalGate"];

    [Fact]
    public void LauncherAssembly_EveryTypeItNames_IsNotARemovalType()
    {
        using var engine = new CompiledAssembly(typeof(BackgroundScanJob).Assembly.Location);
        AssertTheRemovalTypesAreWhereTheseTestsLook(engine);

        using var launcher = new CompiledAssembly(typeof(BackgroundDiskScan).Assembly.Location);
        var named = launcher.TypesNamedInOtherAssemblies();

        // The instrument: the Launcher does host the scan, so this reading must see it name the job.
        Assert.Contains($"{BackgroundNamespace}.{nameof(BackgroundScanJob)}", named);

        var removal = named.Where(IsARemovalType).ToList();
        Assert.True(
            removal.Count == 0,
            "The Launcher names removal code, and the background scan it hosts must never be able to " +
            $"remove anything: {string.Join(", ", removal)}. Removal is a person's act, from the command line.");
    }

    [Fact]
    public void WindowsRulesAssembly_EveryTypeItNames_IsNotARemovalType()
    {
        using var engine = new CompiledAssembly(typeof(BackgroundScanJob).Assembly.Location);
        AssertTheRemovalTypesAreWhereTheseTestsLook(engine);

        using var rules = new CompiledAssembly(typeof(WindowsRuleSet).Assembly.Location);
        var named = rules.TypesNamedInOtherAssemblies();

        // The instrument: every rule implements the engine's rule contract, so it must be seen here.
        Assert.Contains("CcDirector.Reclaim.Rules.IReclaimRule", named);

        var removal = named.Where(IsARemovalType).ToList();
        Assert.True(
            removal.Count == 0,
            "A Windows rule names removal code. The background scan runs every rule unattended, so a " +
            $"rule must only ever look and recommend: {string.Join(", ", removal)}.");
    }

    [Fact]
    public void BackgroundScanClasses_EverythingTheyCanReachInTheEngine_IsNotARemovalType()
    {
        using var engine = new CompiledAssembly(typeof(BackgroundScanJob).Assembly.Location);
        AssertTheRemovalTypesAreWhereTheseTestsLook(engine);

        var startingFrom = engine.TypesDefined()
            .Where(type => engine.NameOf(type).StartsWith(BackgroundNamespace + ".", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(startingFrom, type => engine.NameOf(type) == $"{BackgroundNamespace}.{nameof(BackgroundScanJob)}");

        // Follow every type a background class names, then every type THOSE name, until nothing new
        // turns up. A removal type is recorded and not looked inside: reaching it is already the defect.
        var reachedThrough = startingFrom.ToDictionary(type => type, _ => default(TypeDefinitionHandle));
        var toLookInside = new Queue<TypeDefinitionHandle>(startingFrom);
        while (toLookInside.Count > 0)
        {
            var type = toLookInside.Dequeue();
            if (IsARemovalType(engine.NameOf(type))) continue;

            foreach (var named in engine.TypesNamedBy(type))
            {
                if (reachedThrough.TryAdd(named, type)) toLookInside.Enqueue(named);
            }
        }

        // The instrument: the job walks the disk and runs the rules, through calls inside compiled
        // method bodies. A reader that could not follow a call would find neither of these.
        var reached = reachedThrough.Keys.Select(engine.NameOf).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("CcDirector.Reclaim.Scanning.DirectoryScanner", reached);
        Assert.Contains("CcDirector.Reclaim.Rules.RecommendationRun", reached);

        var removal = reachedThrough.Keys
            .Where(type => IsARemovalType(engine.NameOf(type)))
            .Select(type => HowItWasReached(engine, reachedThrough, type))
            .ToList();
        Assert.True(
            removal.Count == 0,
            "The background scan can reach removal code, and it must never be able to remove anything. " +
            $"Each line reads from the removal type back to a background class: {string.Join("; ", removal)}.");
    }

    private static bool IsARemovalType(string fullName) =>
        fullName.StartsWith(RemovalNamespace + ".", StringComparison.Ordinal);

    private static void AssertTheRemovalTypesAreWhereTheseTestsLook(CompiledAssembly engine)
    {
        var defined = engine.TypesDefined().Select(engine.NameOf).ToHashSet(StringComparer.Ordinal);
        foreach (var name in NamedRemovalTypes)
            Assert.Contains($"{RemovalNamespace}.{name}", defined);
    }

    private static string HowItWasReached(
        CompiledAssembly engine,
        Dictionary<TypeDefinitionHandle, TypeDefinitionHandle> reachedThrough,
        TypeDefinitionHandle type)
    {
        var steps = new List<string>();
        for (var at = type; !at.IsNil; at = reachedThrough[at])
            steps.Add(engine.NameOf(at));
        return string.Join(" <- ", steps);
    }

    /// <summary>
    /// One compiled assembly, read from its file without loading it a second time: the types it
    /// defines, the types in other assemblies it names, and the types one of its own types names.
    /// </summary>
    private sealed class CompiledAssembly : IDisposable
    {
        // How many bytes follow each instruction, taken from the runtime's own table of instructions
        // so that a method body can be walked from one instruction to the next.
        private static readonly Dictionary<ushort, OperandType> OperandOf = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)(field.GetValue(null)
                ?? throw new InvalidOperationException($"The instruction {field.Name} has no value.")))
            .ToDictionary(code => unchecked((ushort)code.Value), code => code.OperandType);

        private readonly PEReader _file;
        private readonly MetadataReader _metadata;

        public CompiledAssembly(string path)
        {
            _file = new PEReader(File.OpenRead(path));
            _metadata = _file.GetMetadataReader();
        }

        public void Dispose() => _file.Dispose();

        public IEnumerable<TypeDefinitionHandle> TypesDefined() => _metadata.TypeDefinitions;

        /// <summary>The full name of a type this assembly defines, with a nested type under its outer one.</summary>
        public string NameOf(TypeDefinitionHandle handle)
        {
            var type = _metadata.GetTypeDefinition(handle);
            var name = _metadata.GetString(type.Name);
            var outer = type.GetDeclaringType();
            return outer.IsNil
                ? $"{_metadata.GetString(type.Namespace)}.{name}"
                : $"{NameOf(outer)}+{name}";
        }

        /// <summary>
        /// The full name of every type in ANOTHER assembly that this one names. The compiler writes
        /// one row for each such type wherever it is used - a call, a field, a local, a base type - so
        /// this list is complete by construction.
        /// </summary>
        public IReadOnlyList<string> TypesNamedInOtherAssemblies() =>
            _metadata.TypeReferences.Select(NameOf).ToList();

        private string NameOf(TypeReferenceHandle handle)
        {
            var type = _metadata.GetTypeReference(handle);
            var name = _metadata.GetString(type.Name);
            return type.ResolutionScope.Kind == HandleKind.TypeReference
                ? $"{NameOf((TypeReferenceHandle)type.ResolutionScope)}+{name}"
                : $"{_metadata.GetString(type.Namespace)}.{name}";
        }

        /// <summary>
        /// Every type of THIS assembly that one of its types names: its base type and interfaces, the
        /// types nested in it (which is where the compiler puts the bodies of its asynchronous methods
        /// and its lambdas), its fields, its method signatures and local variables, and everything a
        /// compiled method body calls, creates, reads or catches.
        /// </summary>
        public IReadOnlySet<TypeDefinitionHandle> TypesNamedBy(TypeDefinitionHandle handle)
        {
            var found = new HashSet<TypeDefinitionHandle>();
            var collector = new Collector(_metadata, found);
            var type = _metadata.GetTypeDefinition(handle);

            collector.Take(type.BaseType);
            foreach (var implemented in type.GetInterfaceImplementations())
                collector.Take(_metadata.GetInterfaceImplementation(implemented).Interface);
            foreach (var nested in type.GetNestedTypes())
                found.Add(nested);
            foreach (var field in type.GetFields())
                _metadata.GetFieldDefinition(field).DecodeSignature(collector, null);

            foreach (var methodHandle in type.GetMethods())
            {
                var method = _metadata.GetMethodDefinition(methodHandle);
                method.DecodeSignature(collector, null);
                if (method.RelativeVirtualAddress == 0) continue;

                var body = _file.GetMethodBody(method.RelativeVirtualAddress);
                if (!body.LocalSignature.IsNil)
                    _metadata.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(collector, null);
                foreach (var region in body.ExceptionRegions)
                {
                    if (region.Kind == ExceptionRegionKind.Catch) collector.Take(region.CatchType);
                }

                var instructions = body.GetILBytes()
                    ?? throw new InvalidOperationException($"The method {_metadata.GetString(method.Name)} has a body with no instructions.");
                foreach (var token in TokensIn(instructions))
                    collector.Take(MetadataTokens.EntityHandle(token));
            }

            return found;
        }

        // Walks a compiled method body one instruction at a time and hands back every reference to a
        // type, a method, a field or a signature. An instruction this table does not know stops the
        // walk with an error: a walk that lost its place would read references out of thin air, or
        // miss them.
        private static IEnumerable<int> TokensIn(byte[] instructions)
        {
            var at = 0;
            while (at < instructions.Length)
            {
                ushort code = instructions[at++];
                if (code == 0xFE) code = (ushort)(0xFE00 | instructions[at++]);

                if (!OperandOf.TryGetValue(code, out var operand))
                    throw new InvalidOperationException($"There is no instruction 0x{code:X} at byte {at}.");

                switch (operand)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        at += 1;
                        break;
                    case OperandType.InlineVar:
                        at += 2;
                        break;
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineI:
                    case OperandType.InlineString:
                    case OperandType.ShortInlineR:
                        at += 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        at += 8;
                        break;
                    case OperandType.InlineSwitch:
                        at += 4 + (4 * BitConverter.ToInt32(instructions, at));
                        break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineSig:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                        yield return BitConverter.ToInt32(instructions, at);
                        at += 4;
                        break;
                    default:
                        throw new InvalidOperationException($"The instruction 0x{code:X} takes an operand this reader does not know: {operand}.");
                }
            }
        }

        /// <summary>
        /// Gathers the types of this assembly out of anything that can name one. It is also what the
        /// metadata reader calls back while it decodes a signature, which is how a type used only as
        /// the argument of a generic type - a list of holding entries, say - is still found.
        /// </summary>
        private sealed class Collector(MetadataReader metadata, HashSet<TypeDefinitionHandle> found)
            : ISignatureTypeProvider<int, object?>
        {
            public void Take(EntityHandle handle)
            {
                if (handle.IsNil) return;

                switch (handle.Kind)
                {
                    case HandleKind.TypeDefinition:
                        found.Add((TypeDefinitionHandle)handle);
                        break;
                    case HandleKind.TypeReference:
                    case HandleKind.ModuleReference:
                        // Another assembly's, or none at all. The rows for other assemblies are read
                        // whole by TypesNamedInOtherAssemblies.
                        break;
                    case HandleKind.TypeSpecification:
                        metadata.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null);
                        break;
                    case HandleKind.MethodDefinition:
                        found.Add(metadata.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType());
                        break;
                    case HandleKind.FieldDefinition:
                        found.Add(metadata.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType());
                        break;
                    case HandleKind.MemberReference:
                        var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                        Take(member.Parent);
                        if (member.GetKind() == MemberReferenceKind.Method)
                            member.DecodeMethodSignature(this, null);
                        else
                            member.DecodeFieldSignature(this, null);
                        break;
                    case HandleKind.MethodSpecification:
                        var specification = metadata.GetMethodSpecification((MethodSpecificationHandle)handle);
                        Take(specification.Method);
                        specification.DecodeSignature(this, null);
                        break;
                    case HandleKind.StandaloneSignature:
                        var signature = metadata.GetStandaloneSignature((StandaloneSignatureHandle)handle);
                        if (signature.GetKind() == StandaloneSignatureKind.Method)
                            signature.DecodeMethodSignature(this, null);
                        else
                            signature.DecodeLocalSignature(this, null);
                        break;
                    default:
                        throw new InvalidOperationException($"A method body names a {handle.Kind}, which this reader does not know how to follow.");
                }
            }

            public int GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                found.Add(handle);
                return 0;
            }

            public int GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            {
                Take(handle);
                return 0;
            }

            public int GetFunctionPointerType(MethodSignature<int> signature) => 0;
            public int GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => 0;
            public int GetGenericInstantiation(int genericType, ImmutableArray<int> typeArguments) => 0;
            public int GetGenericMethodParameter(object? genericContext, int index) => 0;
            public int GetGenericTypeParameter(object? genericContext, int index) => 0;
            public int GetModifiedType(int modifier, int unmodifiedType, bool isRequired) => 0;
            public int GetArrayType(int elementType, ArrayShape shape) => 0;
            public int GetByReferenceType(int elementType) => 0;
            public int GetPinnedType(int elementType) => 0;
            public int GetPointerType(int elementType) => 0;
            public int GetPrimitiveType(PrimitiveTypeCode typeCode) => 0;
            public int GetSZArrayType(int elementType) => 0;
        }
    }
}
