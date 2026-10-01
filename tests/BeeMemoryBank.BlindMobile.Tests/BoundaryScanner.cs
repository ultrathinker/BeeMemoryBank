using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// Reads an assembly's metadata (it never loads or runs it, so Android-only types are no problem) and
/// reports every place where a scanned type mentions one of the <c>restricted</c> types: a field, a
/// constructor or method signature (return type, parameters, generic arguments included), or an IL
/// operand in any method body (<c>typeof(T)</c>, <c>new T()</c>, a call on T, <c>GetRequiredService&lt;T&gt;()</c>).
/// What it cannot see is a type chosen at run time (a <c>Type</c> read from a string).
/// </summary>
internal static partial class BoundaryScanner
{
    public sealed record Finding(string Owner, string Member, string Restricted, string Where)
    {
        public override string ToString() => $"{Owner}.{Member} mentions {Restricted} ({Where})";
    }

    private static readonly OpCode[] OneByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    static BoundaryScanner()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)field.GetValue(null)!;
            if (op.Size == 1) OneByte[(ushort)op.Value & 0xFF] = op;
            else TwoByte[(ushort)op.Value & 0xFF] = op;
        }
    }

    /// <param name="isScanned">Decides by the full name of a top-level type (nested types follow their
    /// owner, so a lambda's closure class belongs to the type that declares the lambda).</param>
    public static IReadOnlyList<Finding> Scan(string assemblyPath, Func<string, bool> isScanned, IReadOnlySet<string> restricted)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var names = new NameProvider(md);
        var findings = new List<Finding>();

        void Check(string owner, string member, string typeText, string where)
        {
            foreach (var token in Tokens(typeText))
                if (restricted.Contains(token))
                    findings.Add(new Finding(owner, member, token, where));
        }

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            var fullName = names.Full(typeHandle);
            var owner = fullName.Split('+')[0];
            if (owner == "<Module>" || !isScanned(owner)) continue;

            foreach (var fieldHandle in type.GetFields())
            {
                var field = md.GetFieldDefinition(fieldHandle);
                Check(fullName, md.GetString(field.Name), field.DecodeSignature(names, null), "field");
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                var methodName = md.GetString(method.Name);
                var signature = method.DecodeSignature(names, null);
                var where = methodName == ".ctor" ? "constructor parameter" : "method signature";
                Check(fullName, methodName, signature.ReturnType, where);
                foreach (var parameter in signature.ParameterTypes)
                    Check(fullName, methodName, parameter, where);

                if (method.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
                foreach (var token in OperandTokens(il))
                    foreach (var mentioned in Mentioned(md, names, MetadataTokens.EntityHandle(token)))
                        Check(fullName, methodName, mentioned, "method body");
            }
        }

        return findings.Distinct().ToList();
    }

    private static IEnumerable<string> Mentioned(MetadataReader md, NameProvider names, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                yield return names.Full((TypeReferenceHandle)handle);
                break;
            case HandleKind.TypeDefinition:
                yield return names.Full((TypeDefinitionHandle)handle);
                break;
            case HandleKind.TypeSpecification:
                yield return md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(names, null);
                break;
            case HandleKind.MemberReference:
            {
                var member = md.GetMemberReference((MemberReferenceHandle)handle);
                foreach (var parent in Mentioned(md, names, member.Parent)) yield return parent;
                if (member.GetKind() == MemberReferenceKind.Field)
                {
                    yield return member.DecodeFieldSignature(names, null);
                }
                else
                {
                    var signature = member.DecodeMethodSignature(names, null);
                    yield return signature.ReturnType;
                    foreach (var parameter in signature.ParameterTypes) yield return parameter;
                }
                break;
            }
            case HandleKind.MethodSpecification:
            {
                var spec = md.GetMethodSpecification((MethodSpecificationHandle)handle);
                foreach (var argument in spec.DecodeSignature(names, null)) yield return argument;
                foreach (var inner in Mentioned(md, names, spec.Method)) yield return inner;
                break;
            }
        }
    }

    private static IEnumerable<int> OperandTokens(byte[] il)
    {
        for (var i = 0; i < il.Length;)
        {
            OpCode op;
            if (il[i] == 0xFE) { op = TwoByte[il[i + 1]]; i += 2; }
            else { op = OneByte[il[i]]; i += 1; }
            if (op.Size == 0) throw new InvalidOperationException("Unknown IL opcode while scanning a method body.");

            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineField:
                case OperandType.InlineTok:
                    yield return BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                default:
                    i += 4;
                    break;
            }
        }
    }

    // "System.Collections.Generic.IEnumerable<A.B,C.D[]>&" -> A.B, C.D, ... : exact names only, so
    // IArticleRepository never matches a longer name that merely contains it.
    private static IEnumerable<string> Tokens(string typeText) =>
        TypeTextSeparator().Split(typeText).Where(t => t.Length > 0);

    [GeneratedRegex(@"[<>,\[\]&*\s]+")]
    private static partial Regex TypeTextSeparator();

    private sealed class NameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public string Full(TypeDefinitionHandle handle)
        {
            var type = md.GetTypeDefinition(handle);
            var name = md.GetString(type.Name);
            var declaring = type.GetDeclaringType();
            if (!declaring.IsNil) return Full(declaring) + "+" + name;
            var ns = md.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string Full(TypeReferenceHandle handle)
        {
            var type = md.GetTypeReference(handle);
            var name = md.GetString(type.Name);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference)
                return Full((TypeReferenceHandle)type.ResolutionScope) + "+" + name;
            var ns = md.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Full(handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Full(handle);
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    }
}
