using System.Globalization;
using System.Text;

namespace MozaHud
{
    public static class Names
    {
        /// <summary>"Monte-Carlo · Sisteron" -> "montecarlosisteron" (letters and digits, no accents), as StageSwitch's Norm.</summary>
        public static string Norm(string s)
        {
            string d = (s ?? "").Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // StageSwitch's rally ids for names that differ (the save file spells Wales "Weles")
        static readonly string[][] RallyIds =
        {
            new[] { "montecarlo", "MonteCarlo" }, new[] { "monaco", "MonteCarlo" }, new[] { "weles", "Wales" },
            new[] { "wales", "Wales" }, new[] { "sweden", "Sweden" }, new[] { "greece", "Greece" },
            new[] { "alsace", "Alsace" }, new[] { "livigno", "Livigno" },
        };

        /// <summary>The rally of a run (StageSwitch id when recorded, else the first part of "Rally · Stage").</summary>
        public static string RallyOf(StageRun run, out string displayName)
        {
            string key = null;
            displayName = null;
            if (!string.IsNullOrEmpty(run.StageId) && run.StageId.Contains("/")) key = run.StageId.Split('/')[0];
            var title = run.Stage ?? "";
            int dot = title.IndexOf('·');
            displayName = (dot > 0 ? title.Substring(0, dot) : title).Trim();
            if (key == null) key = displayName;
            string n = Norm(key);
            foreach (var r in RallyIds) if (n == r[0] || (n.Length > 0 && n.StartsWith(r[0]))) return r[1];
            return key;
        }
    }
}
