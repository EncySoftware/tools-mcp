using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ToolsMcp;

/** Reads the active snapshot and its operation tree in the STFilesInStreamStorage .eop written by ExportOperation. */
internal static class EopUnits
{
    private sealed record Entry(byte Kind, int Parent, int Next, int First, string Name, long Start, long Size, long RawSize, bool Compressed);
    private const int FileLimit = 4 * 1024 * 1024;

    internal static string? Read(string path, Guid operation)
    {
        using var input = new FileStream(path, FileMode.Open,FileAccess.Read,FileShare.Read);
        using var reader = new BinaryReader(input,Encoding.UTF8,true);
        if (input.Length<128 || reader.ReadUInt64()!=0xA0B1C2D3E4F50617UL) return null;
        int signatureSize=reader.ReadByte();
        if(Encoding.ASCII.GetString(reader.ReadBytes(signatureSize))!="STFilesInStreamStorage")return null;
        input.Position=51;
        long offset=reader.ReadInt64();
        if(offset<128 || offset>input.Length-8)return null;
        input.Position=offset+4;
        int count=reader.ReadInt32();
        if(count<1 || count>100_000)return null;
        var entries=new Entry[count];
        for(int i=0;i<count;i++) {
            byte kind=reader.ReadByte();
            if(kind==0){entries[i]=new(kind,-1,-1,-1,"",0,0,0,false);continue;}
            int parent=reader.ReadInt32(), next=reader.ReadInt32();
            if(kind==1){
                reader.ReadByte();
                long start=reader.ReadInt64(), size=reader.ReadInt64();
                entries[i]=new(kind,parent,next,-1,"",start,size,size,false);
            }else if(kind is 2 or 3){
                int first=reader.ReadInt32();
                input.Seek(20,SeekOrigin.Current);
                int length=reader.ReadInt32();
                if(length<0 || length>16384)return null;
                string name=Encoding.Unicode.GetString(reader.ReadBytes(checked(length*2)));
                long size=kind==2?reader.ReadInt64():0;
                bool compressed=kind==2 && reader.ReadByte()!=0;
                long raw=compressed?reader.ReadInt64():size;
                entries[i]=new(kind,parent,next,first,name,0,size,raw,compressed);
            }else return null;
        }
        var byPath=new Dictionary<string,Entry>(StringComparer.OrdinalIgnoreCase);
        for(int i=1;i<count;i++) {
            if(entries[i].Kind!=2)continue;
            var names=new Stack<string>();
            int index=i;
            for(int depth=0;index>0;depth++){
                if(depth>128 || index>=count)return null;
                names.Push(entries[index].Name);index=entries[index].Parent;
            }
            if(!byPath.TryAdd(string.Join("/",names),entries[i]))return null;
        }
        byte[] FileBytes(string name) {
            if(!byPath.TryGetValue(name,out var file) || file.Size is <0 or >FileLimit || file.RawSize is <0 or >FileLimit)
                throw new InvalidDataException("Unrecognised operation-export metadata.");
            var bytes=new byte[(int)file.Size];int written=0;
            var visited=new HashSet<int>();
            int block=file.First;
            while(written<bytes.Length){
                if(block<0 || block>=count || !visited.Add(block))throw new InvalidDataException("Incomplete metadata blocks.");
                var b=entries[block];
                if(b.Kind!=1 || b.Start<0 || b.Size<=0 || b.Start>input.Length-b.Size)throw new InvalidDataException("Invalid metadata block.");
                int size=(int)Math.Min(b.Size,bytes.Length-written);
                input.Position=b.Start;input.ReadExactly(bytes.AsSpan(written,size));written+=size;block=b.Next;
            }
            if(!file.Compressed)return bytes;
            using var stream=new ZLibStream(new MemoryStream(bytes,false),CompressionMode.Decompress);
            var plain=new byte[(int)file.RawSize];stream.ReadExactly(plain);
            if(stream.ReadByte()!=-1)throw new InvalidDataException("Invalid metadata length.");
            return plain;
        }
        using var reference=new StreamReader(new MemoryStream(FileBytes("ActiveSnapshot.ref")),Encoding.UTF8,true);
        string snapshot=reference.ReadToEnd().Trim();
        if(!snapshot.EndsWith(".snp",StringComparison.OrdinalIgnoreCase))return null;
        var project=Xml(FileBytes(snapshot));
        string? tree=project.Element("Technology")?.Element("FilePath")?.Value.Trim().Replace('\\','/');
        if(project.Name!="SCProject" || string.IsNullOrEmpty(tree))return null;
        return ForOperation(project,Xml(FileBytes(tree)),operation);
    }

    private static XElement Xml(byte[] bytes) {
        using var xml=XmlReader.Create(new MemoryStream(bytes,false),new XmlReaderSettings {
            DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=FileLimit
        });
        return XElement.Load(xml);
    }

    internal static string? ForOperation(XElement snapshot, XElement tree, Guid operation) {
        if(snapshot.Name!="SCProject" || tree.Name!="SCTechOpList" || tree.Element("Operations") is not {} ops) return null;
        if(!ops.Descendants("SCTechOperation").Select(x=>(string?)x.Element("InstanceID"))
                .Any(id=>Guid.TryParse(id,out var parsed) && parsed==operation))return null;
        return snapshot.Element("UnitsSystem")?.Value.Trim() switch {
            "Metric"=>"metric", "Imperial"=>"imperial", _=>null
        };
    }
}
