using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

// Minimal .NET metadata dumper: lists types matching a regex with their members.
// Usage: MetaDump <assembly> <typeRegex> [--members] [--base]

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: MetaDump <assembly> <typeRegex> [--members] [--base] [--limit N]");
    return 2;
}

string path = args[0];
string pattern = args[1];
bool wantMembers = args.Contains("--members");
bool wantBase = args.Contains("--base");
int limit = 200;
string fieldOfType = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--limit" && i + 1 < args.Length) limit = int.Parse(args[i + 1]);
    if (args[i] == "--field-of-type" && i + 1 < args.Length) fieldOfType = args[i + 1];
}

var rx = new Regex(pattern, RegexOptions.IgnoreCase);
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();

string FullName(TypeDefinitionHandle h)
{
    var td = md.GetTypeDefinition(h);
    string name = md.GetString(td.Name);
    var decl = td.GetDeclaringType();
    if (!decl.IsNil) return FullName(decl) + "+" + name;
    string ns = md.GetString(td.Namespace);
    return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
}

string TypeRefName(EntityHandle h)
{
    switch (h.Kind)
    {
        case HandleKind.TypeDefinition:
            return FullName((TypeDefinitionHandle)h);
        case HandleKind.TypeReference:
            var tr = md.GetTypeReference((TypeReferenceHandle)h);
            return md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
        case HandleKind.TypeSpecification:
            return "<typespec>";
        default:
            return "<" + h.Kind + ">";
    }
}

static string Fmt(MethodSignature<string> s) =>
    s.ReturnType + " (" + string.Join(", ", s.ParameterTypes) + ")";

int printed = 0;
var fieldRx = fieldOfType is null ? null : new Regex(fieldOfType, RegexOptions.IgnoreCase);
foreach (var th in md.TypeDefinitions)
{
    string full = FullName(th);
    if (!rx.IsMatch(full)) continue;

    if (fieldRx is not null)
    {
        bool hit = false;
        foreach (var fh in md.GetTypeDefinition(th).GetFields())
        {
            try
            {
                if (fieldRx.IsMatch(md.GetFieldDefinition(fh).DecodeSignature(new SigProvider(), null))) { hit = true; break; }
            }
            catch { }
        }
        if (!hit)
        {
            foreach (var ph in md.GetTypeDefinition(th).GetProperties())
            {
                try
                {
                    if (fieldRx.IsMatch(Fmt(md.GetPropertyDefinition(ph).DecodeSignature(new SigProvider(), null)))) { hit = true; break; }
                }
                catch { }
            }
        }
        if (!hit) continue;
    }

    if (printed++ >= limit) { Console.WriteLine("... (limit reached)"); break; }

    var td = md.GetTypeDefinition(th);
    string baseName = "";
    if (wantBase && !td.BaseType.IsNil) baseName = " : " + TypeRefName(td.BaseType);
    Console.WriteLine($"\n=== {full}{baseName}  [{td.Attributes}]");

    if (!wantMembers) continue;

    foreach (var fh in td.GetFields())
    {
        var f = md.GetFieldDefinition(fh);
        string sig = "";
        try { sig = f.DecodeSignature(new SigProvider(), null); } catch { sig = "?"; }
        Console.WriteLine($"  field  {sig} {md.GetString(f.Name)}");
    }
    foreach (var ph in td.GetProperties())
    {
        var p = md.GetPropertyDefinition(ph);
        string sig;
        try { sig = Fmt(p.DecodeSignature(new SigProvider(), null)); } catch { sig = "?"; }
        Console.WriteLine($"  prop   {sig} {md.GetString(p.Name)}");
    }
    foreach (var mh in td.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        string sig;
        try { sig = Fmt(m.DecodeSignature(new SigProvider(), null)); } catch { sig = "?"; }
        Console.WriteLine($"  method {sig} {md.GetString(m.Name)}");
    }
}

Console.WriteLine($"\n(matched {printed} types)");
return 0;

sealed class SigProvider : ISignatureTypeProvider<string, object>
{
    public string GetArrayType(string e, ArrayShape s) => e + "[]";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "typedref",
        _ => c.ToString()
    };
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte c)
    {
        var td = r.GetTypeDefinition(h);
        string n = r.GetString(td.Name);
        var d = td.GetDeclaringType();
        return d.IsNil ? n : GetTypeFromDefinition(r, d, c) + "+" + n;
    }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte c)
    {
        var tr = r.GetTypeReference(h);
        return r.GetString(tr.Name);
    }
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte b) => "<spec>";
}
