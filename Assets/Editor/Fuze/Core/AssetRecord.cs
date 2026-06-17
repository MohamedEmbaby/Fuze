using System;
using System.Collections.Generic;

namespace ProjectMerger
{
    public enum AssetKind
    {
        Texture,
        Script,
        Prefab,
        Scene,
        Material,
        ScriptableObject,
        Audio,
        Model,
        Shader,
        Animation,
        Dll,
        ProjectSetting,
        Asmdef,
        PackageManifest,
        Other
    }

    [Serializable]
    public class AssetRecord
    {
        public string RelativePath;
        public string AbsolutePath;
        public string Guid;
        public string Md5;
        public ulong  DHash;
        public long   Size;
        public AssetKind Kind;
        public bool   IsTextYaml;
        public bool   UnderProjectSettings;
        public bool   UnderPackages;
        public bool   UnderAsmdef;
        public string AsmdefName;       // null if none in ancestors
        public string AsmdefRootNs;     // "rootNamespace" field of owning asmdef, if any

        /// <summary>For .cs files: MD5 of normalized text (BOM stripped, CRLF→LF, trailing ws trimmed).</summary>
        public string NormalizedMd5;

        /// <summary>For .cs files: fully-qualified type names declared in the file (e.g. "My.Ns.Foo").</summary>
        public List<string> ScriptTypes = new List<string>();

        public string FileName
        {
            get
            {
                if (string.IsNullOrEmpty(RelativePath)) return string.Empty;
                int slash = RelativePath.LastIndexOfAny(new[] {'/', '\\'});
                return slash < 0 ? RelativePath : RelativePath.Substring(slash + 1);
            }
        }
    }
}
