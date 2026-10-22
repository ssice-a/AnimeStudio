using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AnimeStudio;

/// <summary>
/// Strict author-side object codec. Uses the actual file's TypeTree, consumes
/// the complete object, and retains scalar representations/alignment bytes.
/// This is separate from the permissive display-only TypeTreeHelper.
/// </summary>
public sealed class TypeTreeDocument
{
    public sealed class Schema
    {
        public string Type { get; init; }
        public string Name { get; init; }
        public int MetaFlag { get; init; }
        public int ByteSize { get; init; }
        public int Version { get; init; }
        public List<Schema> Children { get; } = new();
        public bool Aligned => (MetaFlag & 0x4000) != 0 || Type == "string";

        public static Schema Compile(TypeTree tree)
        {
            if (tree?.m_Nodes == null || tree.m_Nodes.Count == 0) throw new InvalidDataException("Missing TypeTree");
            var stack = new List<Schema>(); Schema root = null;
            foreach (var node in tree.m_Nodes)
            {
                if (node.m_Level < 0 || node.m_Level > stack.Count) throw new InvalidDataException("TypeTree skips a level");
                if (stack.Count > node.m_Level) stack.RemoveRange(node.m_Level, stack.Count - node.m_Level);
                var schema = new Schema { Type=node.m_Type, Name=node.m_Name, MetaFlag=node.m_MetaFlag, ByteSize=node.m_ByteSize, Version=node.m_Version };
                if (stack.Count == 0)
                {
                    if (root != null) throw new InvalidDataException("Multiple TypeTree roots");
                    root = schema;
                }
                else stack[^1].Children.Add(schema);
                stack.Add(schema);
            }
            return root;
        }
    }

    public sealed class Value
    {
        public Schema Schema { get; internal set; }
        public object Scalar { get; set; }
        public Dictionary<string, Value> Fields { get; } = new(StringComparer.Ordinal);
        public List<Value> Items { get; } = new();
        public Value DynamicData { get; internal set; }
        public int Offset { get; internal set; }
        public int Length { get; internal set; }
        internal byte[] Atom, Padding = System.Array.Empty<byte>();
        internal object OriginalScalar;
        internal bool EmptyManagedData;
        public Value this[string field] => Fields.TryGetValue(field, out var v) ? v : throw new InvalidDataException("Missing field: " + Schema.Name + "/" + field);
        public Value Array => Schema.Type == "Array" ? this : Fields.Count == 1 && Fields.TryGetValue("Array", out var a) ? a : throw new InvalidDataException("Not an array: " + Schema.Name);

        /// <summary>Independent template for inserting an array entry with the same schema.</summary>
        public Value Clone()
        {
            var clone = new Value { Schema=Schema, Scalar=Scalar is byte[] data ? data.ToArray() : Scalar,
                OriginalScalar=OriginalScalar, Atom=Atom?.ToArray(), Padding=Padding.ToArray(),
                EmptyManagedData=EmptyManagedData, Offset=Offset, Length=Length, DynamicData=DynamicData?.Clone() };
            foreach (var field in Fields) clone.Fields.Add(field.Key, field.Value.Clone());
            foreach (var item in Items) clone.Items.Add(item.Clone());
            return clone;
        }

        public JToken ToJson()
        {
            if (DynamicData != null) return DynamicData.ToJson();
            if (EmptyManagedData) return new JObject();
            if (Scalar is float f && !float.IsFinite(f)) return new JValue(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Scalar is double d && !double.IsFinite(d)) return new JValue(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Scalar is byte[] bytes) return new JObject { ["encoding"]="base64", ["bytes"]=Convert.ToBase64String(bytes) };
            if (Schema.Type == "Array") return new JArray(Items.Select(v => v.ToJson()));
            if (Fields.Count != 0)
            {
                if (Fields.Count == 1 && Fields.ContainsKey("Array")) return Fields["Array"].ToJson();
                var obj = new JObject(); foreach (var row in Fields) obj.Add(row.Key, row.Value.ToJson()); return obj;
            }
            return Scalar == null ? new JObject() : JToken.FromObject(Scalar);
        }

        public IEnumerable<(string Path, Value Value)> Walk(string path = "$")
        {
            yield return (path, this);
            // JSON flattens the native vector.Array wrapper. Keep exported
            // pointer paths usable against that same JSON representation.
            if (Fields.Count == 1 && Fields.TryGetValue("Array", out var array))
            {
                foreach (var child in array.Walk(path)) yield return child;
                yield break;
            }
            foreach (var row in Fields) foreach (var child in row.Value.Walk(path + "." + row.Key)) yield return child;
            for (int i=0; i<Items.Count; i++) foreach (var child in Items[i].Walk(path + "[" + i + "]")) yield return child;
            if (DynamicData != null) foreach (var child in DynamicData.Walk(path)) yield return child;
        }
    }

    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private byte[] bytes;
    private readonly Dictionary<(string Class, string Namespace, string Assembly), Schema> referenceTypes = new();
    private int position;
    public Value Root { get; private set; }

    public TypeTreeDocument(TypeTree tree, byte[] bytes, IEnumerable<SerializedType> referencedTypes = null)
    {
        this.bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        foreach (var type in referencedTypes ?? Enumerable.Empty<SerializedType>())
        {
            if (type.m_Type?.m_Nodes?.Count > 0 && !referenceTypes.TryAdd((type.m_KlassName, type.m_NameSpace, type.m_AsmName), Schema.Compile(type.m_Type)))
                throw new InvalidDataException("Duplicate managed reference type");
        }
        Root = Read(Schema.Compile(tree), null);
        if (position != bytes.Length) throw new InvalidDataException($"TypeTree consumed {position}/{bytes.Length} bytes");
        if (!Encode().AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Unchanged TypeTree roundtrip differs");
    }

    private byte[] Take(int count)
    {
        if (count < 0 || count > bytes.Length-position) throw new InvalidDataException("Truncated TypeTree object");
        var result = bytes.AsSpan(position, count).ToArray(); position += count; return result;
    }
    private int Count()
    {
        var count = BitConverter.ToInt32(Take(4));
        if (count < 0) throw new InvalidDataException("Negative TypeTree array/string length");
        return count;
    }
    private Value Read(Schema schema, Value parent)
    {
        var value = new Value { Schema=schema, Offset=position };
        int width = PrimitiveSize(schema.Type);
        if (width != 0)
        {
            value.Atom = Take(width);
            value.Scalar = ReadScalar(schema.Type, value.Atom);
        }
        else if (schema.Type is "string" or "TypelessData")
        {
            var data = Take(Count()); value.Scalar = schema.Type == "string" ? Utf8.GetString(data) : data;
        }
        else if (schema.Type == "Array")
        {
            if (schema.Children.Count != 2 || schema.Children[0].Name != "size") throw new InvalidDataException("Invalid array schema");
            int count=Count(); var element=schema.Children[1];
            // Never allocate based on an unchecked serialized count. Zero-byte
            // element arrays are not editable through this codec.
            int minimum = MinimumSize(element);
            if (count != 0 && (minimum == 0 || count > (bytes.Length-position)/minimum)) throw new InvalidDataException("Array count exceeds object extent");
            if ((element.Type is "UInt8" or "char") && !element.Aligned) value.Scalar=Take(count);
            else for (int i=0; i<count; i++) value.Items.Add(Read(element, null));
        }
        else if (schema.Type == "ReferencedObjectData")
        {
            if (parent == null || !parent.Fields.TryGetValue("type", out var type)) throw new InvalidDataException("Managed data has no type descriptor");
            string name=(string)type["class"].Scalar, ns=(string)type["ns"].Scalar, asm=(string)type["asm"].Scalar;
            if (string.IsNullOrEmpty(name))
            {
                long rid = parent.Fields.TryGetValue("rid", out var id) ? Convert.ToInt64(id.Scalar) : 0;
                if (rid is not (-1 or -2)) throw new InvalidDataException("Untyped non-null managed reference");
                value.EmptyManagedData=true;
            }
            else
            {
                if (!referenceTypes.TryGetValue((name,ns,asm), out var referenced)) throw new InvalidDataException("Missing managed reference schema: " + ns+"."+name+", "+asm);
                value.DynamicData=Read(referenced, null);
            }
        }
        else
        {
            if (schema.Children.Count == 0) throw new InvalidDataException("Unsupported TypeTree leaf: " + schema.Type);
            foreach (var child in schema.Children)
            {
                if (value.Fields.ContainsKey(child.Name)) throw new InvalidDataException("Duplicate struct field: " + child.Name);
                value.Fields.Add(child.Name, Read(child, value));
                if (schema.Type == "ManagedReferencesRegistry" && child.Name == "version" && Convert.ToInt32(value["version"].Scalar) != 2)
                    throw new InvalidDataException("Unsupported managed reference registry version");
            }
        }
        value.OriginalScalar=value.Scalar;
        if (schema.Aligned) value.Padding=Take((-position)&3);
        value.Length=position-value.Offset;
        return value;
    }

    public byte[] Encode()
    {
        using var output=new MemoryStream(); using var writer=new BinaryWriter(output, Utf8, true);
        Write(writer, Root, null); return output.ToArray();
    }

    /// <summary>Import the complete JSON view against this exact native schema.
    /// Validation occurs before the original document is mutated.</summary>
    public void ApplyJson(JToken json)
    {
        int budget = 2_000_000;
        var candidate = FromJson(Root.Schema, json, Root, null, ref budget);
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Utf8, true)) Write(writer, candidate, null);
        var encoded = output.ToArray();
        // Reparse to validate all lengths, alignment and managed descriptors.
        position = 0;
        var saved = bytes;
        try { bytes = encoded; var verified = Read(Root.Schema, null); if (position != bytes.Length) throw new InvalidDataException("JSON leaves trailing bytes"); Root = verified; }
        finally { bytes = saved; position = 0; }
    }

    private Value FromJson(Schema schema, JToken json, Value original, Value parent, ref int budget)
    {
        if (--budget < 0) throw new InvalidDataException("JSON object exceeds value limit");
        if (original != null && JToken.DeepEquals(original.ToJson(), json)) return original.Clone();
        var value = new Value { Schema=schema, Padding=original?.Padding?.ToArray() ?? System.Array.Empty<byte>() };
        if (PrimitiveSize(schema.Type) != 0)
        {
            if (schema.Type == "bool")
            {
                if (json?.Type != JTokenType.Boolean) throw new InvalidDataException("Expected boolean: " + schema.Name);
                value.Scalar = (bool)json;
            }
            else if (schema.Type is "float" or "double")
            {
                if (json?.Type is not (JTokenType.Integer or JTokenType.Float)) throw new InvalidDataException("Expected number: " + schema.Name);
                double number = (double)json;
                if (!double.IsFinite(number) || (schema.Type == "float" && !float.IsFinite((float)number))) throw new InvalidDataException("Non-finite edit: " + schema.Name);
                value.Scalar = schema.Type == "float" ? (object)(float)number : number;
            }
            else
            {
                if (json?.Type != JTokenType.Integer) throw new InvalidDataException("Expected integer: " + schema.Name);
                try
                {
                    using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                    WriteScalar(writer, schema.Type, ((JValue)json).Value);
                    value.Scalar = ReadScalar(schema.Type, stream.ToArray());
                }
                catch (Exception error) when (error is OverflowException or InvalidCastException or FormatException)
                { throw new InvalidDataException("Integer outside native range: " + schema.Name, error); }
            }
        }
        else if (schema.Type == "string")
        {
            if (json?.Type != JTokenType.String) throw new InvalidDataException("Expected string: " + schema.Name);
            value.Scalar = (string)json;
        }
        else if (schema.Type == "TypelessData" || (schema.Type == "Array" && (schema.Children[1].Type is "UInt8" or "char") && !schema.Children[1].Aligned))
        {
            Fields(json, new[] { "encoding", "bytes" }, schema.Name);
            if ((string)json["encoding"] != "base64" || json["bytes"].Type != JTokenType.String) throw new InvalidDataException("Expected base64 buffer");
            try { value.Scalar = Convert.FromBase64String((string)json["bytes"]); }
            catch (FormatException error) { throw new InvalidDataException("Invalid base64 buffer", error); }
        }
        else if (schema.Type == "Array")
        {
            if (json is not JArray array || array.Count > budget) throw new InvalidDataException("Invalid array: " + schema.Name);
            for (int i=0; i<array.Count; i++) value.Items.Add(FromJson(schema.Children[1], array[i],
                original != null && i<original.Items.Count ? original.Items[i] : null, null, ref budget));
        }
        else if (schema.Type == "ReferencedObjectData")
        {
            var descriptor = parent?["type"] ?? throw new InvalidDataException("Missing managed descriptor");
            var key = ((string)descriptor["class"].Scalar, (string)descriptor["ns"].Scalar, (string)descriptor["asm"].Scalar);
            if (string.IsNullOrEmpty(key.Item1) && Convert.ToInt64(parent["rid"].Scalar) is -1 or -2)
            { Fields(json, System.Array.Empty<string>(), schema.Name); value.EmptyManagedData = true; }
            else
            {
                if (!referenceTypes.TryGetValue(key, out var dynamicSchema)) throw new InvalidDataException("Unknown managed type");
                value.DynamicData = FromJson(dynamicSchema, json,
                    ReferenceEquals(original?.DynamicData?.Schema, dynamicSchema) ? original.DynamicData : null, null, ref budget);
            }
        }
        else if (schema.Children.Count == 1 && schema.Children[0].Type == "Array")
        {
            value.Fields.Add(schema.Children[0].Name, FromJson(schema.Children[0], json, original?.Fields.GetValueOrDefault(schema.Children[0].Name), value, ref budget));
        }
        else
        {
            Fields(json, schema.Children.Select(c => c.Name), schema.Name);
            if (schema.Children.Count == 0) throw new InvalidDataException("Unsupported JSON leaf: " + schema.Type);
            foreach (var child in schema.Children)
                value.Fields.Add(child.Name, FromJson(child, json[child.Name], original?.Fields.GetValueOrDefault(child.Name), value, ref budget));
            if (schema.Type == "ManagedReferencesRegistry" && Convert.ToInt32(value["version"].Scalar) != 2)
                throw new InvalidDataException("Unsupported managed reference registry version");
        }
        if (original != null && PrimitiveSize(schema.Type) != 0 && SameScalar(value.Scalar, original.Scalar)) return original.Clone();
        return value;
    }

    private static void Fields(JToken json, IEnumerable<string> expected, string label)
    {
        if (json is not JObject obj || !obj.Properties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidDataException("Missing or unknown fields: " + label);
    }
    private void Write(BinaryWriter writer, Value value, Value parent)
    {
        var schema=value.Schema;
        if (PrimitiveSize(schema.Type) != 0)
        {
            if (value.Atom != null && SameScalar(value.Scalar, value.OriginalScalar)) writer.Write(value.Atom);
            else WriteScalar(writer, schema.Type, value.Scalar);
        }
        else if (schema.Type == "string") { var data=Utf8.GetBytes((string)value.Scalar); writer.Write(data.Length); writer.Write(data); }
        else if (schema.Type == "TypelessData") { var data=(byte[])value.Scalar; writer.Write(data.Length); writer.Write(data); }
        else if (schema.Type == "Array")
        {
            if (value.Scalar is byte[] data)
            {
                if (schema.Children[1].Type is not ("UInt8" or "char") || schema.Children[1].Aligned)
                    throw new InvalidDataException("Byte buffer does not match array schema");
                writer.Write(data.Length); writer.Write(data);
            }
            else
            {
                writer.Write(value.Items.Count);
                foreach (var item in value.Items)
                {
                    if (!ReferenceEquals(item.Schema, schema.Children[1])) throw new InvalidDataException("Array item schema changed");
                    Write(writer,item,null);
                }
            }
        }
        else if (schema.Type == "ReferencedObjectData")
        {
            var type = parent?["type"] ?? throw new InvalidDataException("Managed data has no type descriptor");
            var key = ((string)type["class"].Scalar, (string)type["ns"].Scalar, (string)type["asm"].Scalar);
            if (value.DynamicData != null)
            {
                if (!referenceTypes.TryGetValue(key, out var expected) || !ReferenceEquals(expected, value.DynamicData.Schema))
                    throw new InvalidDataException("Managed data type descriptor changed without matching data");
                Write(writer,value.DynamicData,null);
            }
            else if (!value.EmptyManagedData || !string.IsNullOrEmpty(key.Item1) || Convert.ToInt64(parent["rid"].Scalar) is not (-1 or -2))
                throw new InvalidDataException("Unresolved managed data");
        }
        else
        {
            if (value.Fields.Count != schema.Children.Count) throw new InvalidDataException("Incomplete TypeTree struct");
            foreach (var child in schema.Children)
            {
                if (!ReferenceEquals(value[child.Name].Schema, child)) throw new InvalidDataException("Struct field schema changed");
                Write(writer,value[child.Name],value);
            }
        }
        if (schema.Aligned)
        {
            int count=(int)((-writer.BaseStream.Position)&3);
            writer.Write(value.Padding.Length==count ? value.Padding : new byte[count]);
        }
    }

    private static bool SameScalar(object a, object b) => a is float fa && b is float fb ? BitConverter.SingleToInt32Bits(fa)==BitConverter.SingleToInt32Bits(fb)
        : a is double da && b is double db ? BitConverter.DoubleToInt64Bits(da)==BitConverter.DoubleToInt64Bits(db) : Equals(a,b);
    private static int MinimumSize(Schema s) => PrimitiveSize(s.Type) is var n && n!=0 ? n
        : s.Type is "string" or "TypelessData" or "Array" ? 4 : s.Children.Sum(MinimumSize);
    private static int PrimitiveSize(string type) => type switch
    {
        "bool" or "char" or "SInt8" or "UInt8"=>1,
        "short" or "SInt16" or "UInt16" or "unsigned short"=>2,
        "int" or "SInt32" or "UInt32" or "unsigned int" or "Type*" or "float"=>4,
        "long long" or "SInt64" or "UInt64" or "unsigned long long" or "FileSize" or "double"=>8,
        _=>0
    };
    private static object ReadScalar(string t, byte[] b) => t switch
    {
        "bool"=>b[0] switch {0=>false,1=>true,_=>throw new InvalidDataException("Invalid boolean")},
        "SInt8"=>(sbyte)b[0], "char" or "UInt8"=>b[0],
        "short" or "SInt16"=>BitConverter.ToInt16(b), "UInt16" or "unsigned short"=>BitConverter.ToUInt16(b),
        "int" or "SInt32"=>BitConverter.ToInt32(b), "UInt32" or "unsigned int" or "Type*"=>BitConverter.ToUInt32(b),
        "long long" or "SInt64"=>BitConverter.ToInt64(b), "UInt64" or "unsigned long long" or "FileSize"=>BitConverter.ToUInt64(b),
        "float"=>BitConverter.ToSingle(b), "double"=>BitConverter.ToDouble(b),
        _=>throw new InvalidDataException("Unknown scalar " + t)
    };
    private static void WriteScalar(BinaryWriter w,string t,object v)
    {
        switch(t)
        {
            case "bool": w.Write(Convert.ToBoolean(v)); break;
            case "SInt8": w.Write(Convert.ToSByte(v)); break;
            case "char": case "UInt8": w.Write(Convert.ToByte(v)); break;
            case "short": case "SInt16": w.Write(Convert.ToInt16(v)); break;
            case "UInt16": case "unsigned short": w.Write(Convert.ToUInt16(v)); break;
            case "int": case "SInt32": w.Write(Convert.ToInt32(v)); break;
            case "UInt32": case "unsigned int": case "Type*": w.Write(Convert.ToUInt32(v)); break;
            case "long long": case "SInt64": w.Write(Convert.ToInt64(v)); break;
            case "UInt64": case "unsigned long long": case "FileSize": w.Write(Convert.ToUInt64(v)); break;
            case "float": w.Write(Convert.ToSingle(v)); break;
            case "double": w.Write(Convert.ToDouble(v)); break;
            default: throw new InvalidDataException("Unknown scalar " + t);
        }
    }
}
