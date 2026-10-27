using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace ClipboardManager.Updates
{
    [DataContract]
    public sealed class UpdateManifest
    {
        [DataMember(Name = "schema", IsRequired = true)] public int Schema { get; set; }
        [DataMember(Name = "application", IsRequired = true)] public string Application { get; set; }
        [DataMember(Name = "version", IsRequired = true)] public string Version { get; set; }
        [DataMember(Name = "platform", IsRequired = true)] public string Platform { get; set; }
        [DataMember(Name = "packaging", IsRequired = true)] public string Packaging { get; set; }
        [DataMember(Name = "files", IsRequired = true)] public List<UpdateFile> Files { get; set; }
        public UpdateManifest() { Application = "ClipboardManager"; Version = ""; Platform = "win-x64"; Packaging = ""; Files = new List<UpdateFile>(); }
    }

    [DataContract]
    public sealed class UpdateFile
    {
        [DataMember(Name = "path", IsRequired = true)] public string Path { get; set; }
        [DataMember(Name = "sha256", IsRequired = true)] public string Sha256 { get; set; }
        [DataMember(Name = "size", IsRequired = true)] public long Size { get; set; }
        [DataMember(Name = "compressedSize", IsRequired = true)] public long CompressedSize { get; set; }
        public UpdateFile() { Path = ""; Sha256 = ""; }
        public string AssetName { get { return "update-" + Sha256 + ".gz"; } }
    }

    public sealed class UpdatePlan
    {
        public List<UpdateFile> Changes { get; private set; }
        public List<UpdateFile> Deletes { get; private set; }
        public UpdatePlan() { Changes = new List<UpdateFile>(); Deletes = new List<UpdateFile>(); }
    }

    [DataContract]
    public sealed class UpdateOperation
    {
        [DataMember(Name = "path", IsRequired = true)] public string Path { get; set; }
        [DataMember(Name = "hadOriginal", IsRequired = true)] public bool HadOriginal { get; set; }
        [DataMember(Name = "deleteOnly", IsRequired = true)] public bool DeleteOnly { get; set; }
        [DataMember(Name = "sha256", IsRequired = true)] public string Sha256 { get; set; }
        public UpdateOperation() { Path = ""; Sha256 = ""; }
    }
}
