using AnimeStudio;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.GUI;

/// <summary>Read complete source objects, including their managed-reference registry.</summary>
internal sealed class EiemAnimationSourceReader
{
    private sealed class Node
    {
        public TypeTreeNode Field;
        public List<Node> Children = new();
    }
    private readonly EndianBinaryReader reader;
    private readonly Dictionary<(string, string, string), SerializedType> references;

    private EiemAnimationSourceReader(AnimeStudio.Object asset)
    {
        reader = new EndianBinaryReader(new MemoryStream(asset.GetRawData()), asset.reader.Endian);
        references = (asset.assetsFile.m_RefTypes ?? new List<SerializedType>())
            .Where(t => t.m_Type?.m_Nodes?.Count > 0)
            .ToDictionary(t => (t.m_KlassName, t.m_NameSpace, t.m_AsmName), t => t);
    }

    internal static JObject Read(AnimeStudio.Object asset)
    {
        var nodes = asset.serializedType?.m_Type?.m_Nodes;
        if (nodes == null || nodes.Count == 0) throw new InvalidDataException("Animation source has no TypeTree.");
        var decoder = new EiemAnimationSourceReader(asset);
        using (decoder.reader)
        {
            var data = (JObject)decoder.Value(Tree(nodes));
            if (decoder.reader.Position != decoder.reader.BaseStream.Length)
                throw new InvalidDataException("Animation source TypeTree did not consume the whole object.");
            return data;
        }
    }

    private static Node Tree(List<TypeTreeNode> fields)
    {
        var root = new Node { Field = fields[0] };
        var stack = new Stack<Node>(); stack.Push(root);
        foreach (var field in fields.Skip(1))
        {
            while (stack.Count > 0 && stack.Peek().Field.m_Level >= field.m_Level) stack.Pop();
            if (stack.Count == 0) throw new InvalidDataException("Invalid animation TypeTree hierarchy.");
            var node = new Node { Field = field }; stack.Peek().Children.Add(node); stack.Push(node);
        }
        return root;
    }

    private void Check(long count)
    {
        if (count < 0 || count > reader.BaseStream.Length - reader.Position)
            throw new InvalidDataException("Animation source exceeds its serialized object boundary.");
    }
    private void Align() { var count = (-reader.Position) & 3; Check(count); reader.Position += count; }
    private JToken Value(Node node, JObject context = null)
    {
        var type = node.Field.m_Type;
        JToken result;
        switch (type)
        {
            case "SInt8": Check(1); result = reader.ReadSByte(); break;
            case "UInt8": case "char": Check(1); result = reader.ReadByte(); break;
            case "bool": Check(1); result = reader.ReadBoolean(); break;
            case "short": case "SInt16": Check(2); result = reader.ReadInt16(); break;
            case "UInt16": Check(2); result = reader.ReadUInt16(); break;
            case "int": case "SInt32": Check(4); result = reader.ReadInt32(); break;
            case "UInt32": Check(4); result = reader.ReadUInt32(); break;
            case "SInt64": Check(8); result = reader.ReadInt64(); break;
            case "UInt64": Check(8); result = reader.ReadUInt64(); break;
            case "float": Check(4); result = reader.ReadSingle(); break;
            case "double": Check(8); result = reader.ReadDouble(); break;
            case "string":
                Check(4); var length = reader.ReadInt32(); Check(length);
                result = new UTF8Encoding(false, true).GetString(reader.ReadBytes(length)); Align(); break;
            case "ReferencedObjectData":
                var info = context?["type"];
                if (info == null || !references.TryGetValue(((string)info["class"], (string)info["ns"],
                        (string)info["asm"]), out var payload))
                    throw new InvalidDataException("Unknown animation managed-reference type: " + info);
                result = Fields(Tree(payload.m_Type.m_Nodes).Children.Where(c => c.Field.m_Type != "ManagedReferencesRegistry"));
                break;
            default:
                if (node.Children.FirstOrDefault()?.Field.m_Type == "Array")
                {
                    var array = node.Children[0]; Check(4); var count = reader.ReadInt32(); Check(count);
                    if (array.Children.Count != 2) throw new InvalidDataException("Invalid animation array schema.");
                    var values = new JArray();
                    for (var i = 0; i < count; i++) values.Add(Value(array.Children[1]));
                    result = values;
                    if ((array.Field.m_MetaFlag & 0x4000) != 0) Align();
                }
                else
                {
                    if (node.Children.Count == 0 && node.Field.m_ByteSize > 0)
                        throw new InvalidDataException("Unsupported animation field: " + type);
                    result = Fields(node.Children);
                }
                break;
        }
        if ((node.Field.m_MetaFlag & 0x4000) != 0) Align();
        return result;
    }
    private JObject Fields(IEnumerable<Node> children)
    {
        var result = new JObject();
        foreach (var child in children) result.Add(child.Field.m_Name, Value(child, result));
        return result;
    }
}
