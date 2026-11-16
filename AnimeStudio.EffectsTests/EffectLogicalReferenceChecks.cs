using AnimeStudio;
using AnimeStudio.GUI;
using Newtonsoft.Json.Linq;
using System.Text;

// Real source + editable material -> unknown logical Texture -> native slots.
// The path need not exist on this author machine; no EIDX is read to build it.
internal static class EffectLogicalReferenceChecks
{
    internal static void Run(string source, string input, string output)
    {
        void Check(bool value, string label) { if (!value) throw new Exception(label); }
        var edits = JObject.Parse(File.ReadAllText(input));
        var row = ((JArray)edits["gameReferences"]).First(r => ((string)r["type"]).StartsWith("Texture"));
        foreach (string key in new[] {"previewSource","resourceIdentity","resourceClassId","sourcePrefab","sourceRoot","sourceRootIdentity","sourceVfsFingerprint"})
            ((JObject)row).Remove(key);
        row["path"] = "assets/effect-regression/not-imported.png";row["asset"] = "";
        row["sourcePrefab"]="assets/effect-regression/logical-donor.prefab";
        string request=output+".json";Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request))!);
        File.WriteAllText(request,edits.ToString());
        EiemEffectBuildCommand.Build(source,request,output);
        using var manifest=new BinaryReader(File.OpenRead(Path.Combine(output,"runtime.bin")));
        string S()=>Encoding.UTF8.GetString(manifest.ReadBytes(checked((int)manifest.ReadUInt64())));
        S();S();S();S();manifest.ReadUInt64();manifest.ReadUInt64();int count=checked((int)manifest.ReadUInt64());
        var bundles=new List<byte[]>();
        for(int i=0;i<count;i++){string file=S();S();int n=checked((int)manifest.ReadUInt64());for(int j=0;j<n;j++)manifest.ReadUInt64();bundles.Add(File.ReadAllBytes(Path.Combine(output,file)));}
        Check(S()=="EFF_GAME_RESOURCES/3","bare logical resource lost runtime relocation contract");
        int resources=checked((int)manifest.ReadUInt64()),slots=0,pointers=0;string marker="";
        for(int i=0;i<resources;i++){
            string path=S();S();S();string sourcePrefab=S(),sourceRoot=S();string cab=S();ulong id=manifest.ReadUInt64();int n=checked((int)manifest.ReadUInt64());
            int selectedPointers=0;
            for(int j=0;j<n;j++){
                int bundle=checked((int)manifest.ReadUInt64()),offset=checked((int)manifest.ReadUInt64()),kind=checked((int)manifest.ReadUInt64());
                byte[] expected=kind==0?Encoding.UTF8.GetBytes(cab):BitConverter.GetBytes(id);
                Check(bundles[bundle].AsSpan(offset,expected.Length).SequenceEqual(expected),"native relocation offset does not address its declared slot");
                slots++;if(kind==1)selectedPointers++;
            }
            if(path==(string)row["path"]){marker=cab;pointers=selectedPointers;Check(n>0,"new logical resource has no native relocation sites");
                Check(sourcePrefab==(string)row["sourcePrefab"]&&sourceRoot=="","logical source Prefab requires an imported donor/root name");}
        }
        Check(manifest.BaseStream.Position==manifest.BaseStream.Length && slots>pointers && pointers>0,"incomplete native relocation manifest");
        // Verify actual native PPtrs, not just matching marker bytes in the archive.
        var manager=new AssetsManager{Game=GameManager.GetGameByType(GameType.ArknightsEndfield)};int nativePointers=0,materialPointers=0;
        foreach(byte[] bundle in bundles){
            using var reader=new FileReader("logical-reference.ab",new MemoryStream(bundle,false));
            var archive=new VFSFile(reader,"logical-reference.ab",GameType.ArknightsEndfield);
            foreach(var member in archive.fileList){using var bytes=new MemoryStream();member.stream.CopyTo(bytes);member.stream.Dispose();if(member.fileName.EndsWith(".resS"))continue;
                using var nativeReader=new FileReader(member.fileName,new MemoryStream(bytes.ToArray(),false));var native=new SerializedFile(nativeReader,manager);
                foreach(var obj in native.m_Objects){
                    var doc=new TypeTreeDocument(obj.serializedType.m_Type,bytes.ToArray().AsSpan((int)obj.byteStart,(int)obj.byteSize).ToArray(),native.m_RefTypes);
                    foreach(var p in doc.Root.Walk().Where(n=>n.Value.Schema.Type.StartsWith("PPtr<"))){int fid=Convert.ToInt32(p.Value["m_FileID"].Scalar);long pid=Convert.ToInt64(p.Value["m_PathID"].Scalar);
                        if(fid>0&&fid<=native.m_Externals.Count&&native.m_Externals[fid-1].fileName==marker&&pid==1){nativePointers++;if(obj.classID==21)materialPointers++;}
                    }
                }
            }
        }
        Check(nativePointers==pointers&&materialPointers>0,"relocation sites do not match the actual native material and preload PPtrs");
        Console.WriteLine($"PASS real logical resource build: no imported target, exact native CAB/PathID slots, relocations={slots}, PPtrs={pointers}");
    }
}
