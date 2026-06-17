using System.IO;
using System.Text;

namespace ProjectMerger
{
    public static class DryRunReport
    {
        public static string BuildMarkdown(MergePlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Project Merge Dry-Run — {plan.IncomingProjectName}");
            sb.AppendLine();
            sb.AppendLine($"- Source: `{plan.IncomingProjectRoot}`");
            sb.AppendLine($"- Entries: **{plan.Entries.Count}**");
            sb.AppendLine($"- Identical: {plan.Count(EntryStatus.Identical)}");
            sb.AppendLine($"- RemapOnly: {plan.Count(EntryStatus.RemapOnly)}");
            sb.AppendLine($"- New:       {plan.Count(EntryStatus.NewAsset)}");
            sb.AppendLine($"- ConflictByPath: {plan.Count(EntryStatus.ConflictByPath)}");
            sb.AppendLine($"- ConflictByGuid: {plan.Count(EntryStatus.ConflictByGuid)}");
            sb.AppendLine($"- NearDuplicate:  {plan.Count(EntryStatus.NearDuplicate)}");
            sb.AppendLine($"- ProjectSettingConflict: {plan.Count(EntryStatus.ProjectSettingConflict)}");
            sb.AppendLine($"- GUID Remaps: {plan.GuidRemap.Count}");
            sb.AppendLine($"- Wrap imported scripts: {plan.WrapImportedScripts}   ns=`{plan.WrappedNamespace}`");
            sb.AppendLine();
            sb.AppendLine("## Entries");
            sb.AppendLine();
            sb.AppendLine("| Status | Resolution | Kind | Incoming | Current | Note |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var e in plan.Entries)
            {
                sb.Append("| ").Append(e.Status)
                  .Append(" | ").Append(e.Resolution)
                  .Append(" | ").Append(e.Incoming.Kind)
                  .Append(" | `").Append(e.Incoming.RelativePath).Append('`')
                  .Append(" | `").Append(e.CurrentMatch?.RelativePath ?? "").Append('`')
                  .Append(" | ").Append(e.Note ?? "").AppendLine(" |");
            }
            return sb.ToString();
        }

        public static string BuildCsv(MergePlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Status,Resolution,Kind,IncomingPath,IncomingGuid,IncomingMd5,CurrentPath,CurrentGuid,CurrentMd5,Note");
            foreach (var e in plan.Entries)
            {
                sb.Append(e.Status).Append(',')
                  .Append(e.Resolution).Append(',')
                  .Append(e.Incoming.Kind).Append(',')
                  .Append(Escape(e.Incoming.RelativePath)).Append(',')
                  .Append(e.Incoming.Guid).Append(',')
                  .Append(e.Incoming.Md5).Append(',')
                  .Append(Escape(e.CurrentMatch?.RelativePath)).Append(',')
                  .Append(e.CurrentMatch?.Guid).Append(',')
                  .Append(e.CurrentMatch?.Md5).Append(',')
                  .Append(Escape(e.Note)).AppendLine();
            }
            return sb.ToString();
        }

        public static void WriteToFile(string absPath, string content)
        {
            var dir = Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(absPath, content, new UTF8Encoding(false));
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.IndexOfAny(new[] { ',', '"', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
