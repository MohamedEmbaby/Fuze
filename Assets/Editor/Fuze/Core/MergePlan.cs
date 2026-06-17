using System.Collections.Generic;

namespace ProjectMerger
{
    public enum EntryStatus
    {
        Identical,
        RemapOnly,
        NewAsset,
        ConflictByPath,
        ConflictByGuid,
        NearDuplicate,
        ProjectSettingConflict
    }

    public enum Resolution
    {
        Undecided,
        KeepCurrent,
        Overwrite,
        KeepBoth,
        ImportAsNew,
        RemapGuid
    }

    public class MergeEntry
    {
        public AssetRecord Incoming;
        public AssetRecord CurrentMatch;
        public EntryStatus Status;
        public Resolution  Resolution;
        public int         NearDuplicateHamming;
        public string      Note;
    }

    public class MergePlan
    {
        public string IncomingProjectRoot;
        public string IncomingProjectName;
        public List<MergeEntry> Entries = new List<MergeEntry>();

        /// <summary>incoming guid -> existing guid; applied to all copied text/YAML assets on apply.</summary>
        public Dictionary<string, string> GuidRemap = new Dictionary<string, string>();

        /// <summary>
        /// Original incoming asmdef "name" -> renamed "name" assigned during apply.
        /// Populated when an incoming .asmdef collides with a host asmdef name (Unity treats
        /// "name" as the assembly name, so duplicates fail to compile). On apply, the staged
        /// .asmdef's name + rootNamespace are rewritten, every other staged .asmdef updates
        /// matching entries in its "references" array, and scripts under the renamed asmdef
        /// are added to the wrap pass so their types move into <see cref="WrappedNamespace"/>.
        /// </summary>
        public Dictionary<string, string> AsmdefNameRemap = new Dictionary<string, string>();

        /// <summary>
        /// When true, every copied .cs file is wrapped in <see cref="WrappedNamespace"/> so
        /// its declared types cannot collide with types in the current project. Unity GUID
        /// references in scenes/prefabs/SOs remain valid — wrapping only changes the C# type name
        /// (Unity re-scans the MonoScript for m_Namespace / m_ClassName on import).
        /// </summary>
        public bool   WrapImportedScripts;
        public string WrappedNamespace;

        public int Count(EntryStatus s)
        {
            int n = 0;
            foreach (var e in Entries) if (e.Status == s) n++;
            return n;
        }

        public int UndecidedConflicts()
        {
            int n = 0;
            foreach (var e in Entries)
            {
                if ((e.Status == EntryStatus.ConflictByPath ||
                     e.Status == EntryStatus.ConflictByGuid ||
                     e.Status == EntryStatus.NearDuplicate ||
                     e.Status == EntryStatus.ProjectSettingConflict) &&
                    e.Resolution == Resolution.Undecided)
                    n++;
            }
            return n;
        }

        public bool HasScriptKeepBoth()
        {
            foreach (var e in Entries)
                if (e.Incoming.Kind == AssetKind.Script && e.Resolution == Resolution.KeepBoth)
                    return true;
            return false;
        }

        public bool HasScriptTypeCollision()
        {
            foreach (var e in Entries)
                if (e.Incoming.Kind == AssetKind.Script &&
                    e.Note != null &&
                    e.Note.StartsWith("C# type collision"))
                    return true;
            return false;
        }
    }
}
